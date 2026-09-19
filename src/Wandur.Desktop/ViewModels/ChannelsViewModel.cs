using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Channels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

/// <summary>One channel's copy of the conversation, plus how to answer on it.</summary>
public sealed partial class ChannelTabViewModel : ObservableObject
{
    /// <summary>A mirror is not an archive; the transcript keeps everything, the panel keeps the conversation.</summary>
    public const int MaximumMessages = 500;
    public ChannelTabViewModel(string channel, bool isPrivate, bool isAll = false)
    { Channel = channel; IsPrivate = isPrivate; IsAll = isAll; }
    public string Channel { get; }
    public bool IsPrivate { get; }
    public bool IsAll { get; }
    public ObservableCollection<ChannelMessage> Messages { get; } = [];
    public string? ReplyCommand { get; private set; }
    public string? LastSpeaker { get; private set; }
    public string Title => IsAll ? L.ChannelsAll : Channel;
    [ObservableProperty] private int _unread;
    public bool HasUnread => Unread > 0;
    public string UnreadLabel => Unread > 99 ? "99+" : Unread.ToString(System.Globalization.CultureInfo.CurrentCulture);
    partial void OnUnreadChanged(int value) { OnPropertyChanged(nameof(HasUnread)); OnPropertyChanged(nameof(UnreadLabel)); }
    public void RefreshLanguage() => OnPropertyChanged(nameof(Title));

    public void Add(ChannelMessage message, bool isContinuation)
    {
        if (message.ReplyCommand is { Length: > 0 } reply) ReplyCommand = reply;
        if (message.Speaker.Length > 0) LastSpeaker = message.Speaker;
        if (isContinuation && Messages.Count > 0) Messages[^1] = message;
        else Messages.Add(message);
        while (Messages.Count > MaximumMessages) Messages.RemoveAt(0);
    }

    /// <summary>The command that speaks on this channel, with the person being answered filled in.</summary>
    public string? Reply(string text)
    {
        if (ReplyCommand is not { Length: > 0 } command) return null;
        if (command.Contains("{speaker}", StringComparison.OrdinalIgnoreCase))
        {
            if (LastSpeaker is not { Length: > 0 } speaker) return null;
            command = command.Replace("{speaker}", speaker, StringComparison.OrdinalIgnoreCase);
        }
        return command.Trim() + " " + text;
    }
}

/// <summary>
/// The docked Channels panel: a copy of the channel traffic, tabbed per channel, with a reply box. Nothing
/// here removes anything from the transcript, which keeps every line exactly as the world sent it.
/// </summary>
public sealed partial class ChannelsViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceController _controller;
    private bool _attached;
    public ObservableCollection<ChannelTabViewModel> Tabs { get; } = [];
    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private string _draft = "";

    public ChannelsViewModel(WorkspaceController controller)
    {
        _controller = controller;
        Tabs.Add(new ChannelTabViewModel("", false, isAll: true));
    }

    public WorkspaceController Controller => _controller;
    public ChannelTabViewModel Selected => Tabs[Math.Clamp(SelectedIndex, 0, Tabs.Count - 1)];
    public bool IsEmpty => Tabs[0].Messages.Count == 0;
    public bool IsMirrorNoteVisible => IsEmpty;
    public string MirrorNote => L.ChannelsMirrorNote;
    /// <summary>The All tab shows every channel at once, so there is no one channel it could answer on.</summary>
    public string ReplyHint => Selected.IsAll ? L.ChannelsReplyAllHint
        : Selected.Reply("x") is null ? L.ChannelsReplyUnknown
        : L.Format(L.ChannelsReplyPlaceholder, Selected.Title);
    public bool CanReply => !Selected.IsAll && Selected.Reply("x") is not null && _controller.IsConnected && !_controller.IsPrivate;
    public bool CanSend => CanReply && !string.IsNullOrWhiteSpace(Draft);

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _controller.ChannelMessageReceived += Received;
        _controller.Changed += Refresh;
        Wandur.Core.Localization.UiLanguage.Changed += RefreshLanguage;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _controller.ChannelMessageReceived -= Received;
        _controller.Changed -= Refresh;
        Wandur.Core.Localization.UiLanguage.Changed -= RefreshLanguage;
    }

    public void Dispose() => Detach();

    private void RefreshLanguage()
    {
        foreach (var tab in Tabs) tab.RefreshLanguage();
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanReply)); OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ReplyHint)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(IsMirrorNoteVisible));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The panel is a copy: every message lands on its channel and on All, newest at the bottom.</summary>
    public void Received(ChannelMessage message, bool isContinuation)
    {
        var tab = Tabs.FirstOrDefault(t => !t.IsAll && string.Equals(t.Channel, message.Channel, StringComparison.OrdinalIgnoreCase))
            ?? Insert(new ChannelTabViewModel(message.Channel, message.IsPrivate));
        Tabs[0].Add(message, isContinuation);
        tab.Add(message, isContinuation);
        if (!isContinuation)
            foreach (var unseen in new[] { Tabs[0], tab }.Distinct().Where(t => !ReferenceEquals(t, Selected))) unseen.Unread++;
        Selected.Unread = 0;
        Refresh();
    }

    /// <summary>Tells and pages come first: a private message waits on an answer, a channel does not.</summary>
    private ChannelTabViewModel Insert(ChannelTabViewModel tab)
    {
        var index = tab.IsPrivate ? Tabs.Count(t => t.IsAll || t.IsPrivate) : Tabs.Count;
        var selected = Selected;
        Tabs.Insert(index, tab);
        // Inserting ahead of the shown tab must not silently switch the reader to another conversation.
        var restored = Tabs.IndexOf(selected);
        if (restored >= 0 && restored != SelectedIndex) SelectedIndex = restored;
        return tab;
    }

    partial void OnSelectedIndexChanged(int value) { Selected.Unread = 0; Draft = ""; Refresh(); }
    partial void OnDraftChanged(string value) => Refresh();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        // The command can be invoked directly; a disconnected or private session still refuses it here.
        if (!CanSend) return;
        var text = Draft.Trim();
        if (text.Length == 0 || Selected.Reply(text) is not { } command) return;
        Draft = "";
        await _controller.SendAsync(command);
    }
}
