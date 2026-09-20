using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Channels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

/// <summary>One line of the preview: what the proposed rule made of a transcript line.</summary>
public sealed record ChannelPreviewMatch(string Speaker, string Text)
{
    public string Label => Speaker.Length > 0 ? Speaker + ": " + Text : Text;
}

/// <summary>
/// The "mark as channel" dialog: one transcript line, the shape Wandur guessed for it, and a live preview
/// of what the rule would catch. Editing a piece (head, speaker, separator) recomposes the rule; editing
/// the rule directly leaves the pieces as they were. Saving teaches the world the rule; "not a channel"
/// teaches it an exclusion for the same shape instead.
/// </summary>
public sealed partial class MarkChannelViewModel : ObservableObject
{
    public const int PreviewLines = 200;
    public const int PreviewShown = 5;
    private readonly WorkspaceController _controller;
    private readonly IReadOnlyList<string> _lines;
    private readonly ChannelRuleProposal _proposal;
    private bool _composing;

    public MarkChannelViewModel(WorkspaceController controller, string example, string? second = null)
    {
        _controller = controller;
        Example = ChannelRuleSet.Strip(example).Trim();
        _lines = controller.RecentTranscriptLines(PreviewLines);
        _proposal = ChannelRuleProposer.Propose(Example, second);
        CurrentChannel = controller.ChannelRules.Match(Example, DateTimeOffset.Now)?.Channel;
        foreach (var channel in controller.ChannelRules.Channels) ChannelChoices.Add(channel);
        ChannelChoices.Add(L.TeachChannelNewChannel);
        _headPattern = _proposal.HeadPattern; _speakerPattern = _proposal.SpeakerPattern; _separatorPattern = _proposal.SeparatorPattern;
        _pattern = _proposal.Rule.Pattern;
        var guess = _proposal.Rule.Channel;
        var known = ChannelChoices.Take(ChannelChoices.Count - 1).ToList().FindIndex(c => string.Equals(c, guess, StringComparison.OrdinalIgnoreCase));
        _channelIndex = known >= 0 ? known : ChannelChoices.Count - 1;
        _newChannelName = known >= 0 ? "" : guess;
        _replyCommand = _proposal.Rule.ReplyCommand ?? "";
        _isPrivate = _proposal.Rule.IsPrivate;
        if (known >= 0) AdoptChannel(guess);
        RefreshPreview();
    }

    public WorkspaceController Controller => _controller;
    public string Example { get; }
    public ChannelRuleProposal Proposal => _proposal;
    /// <summary>The pieces as Wandur read them, shown beside the patterns so the reader can see what each stands for.</summary>
    public string Head => _proposal.Head;
    public string Speaker => _proposal.Speaker;
    public string Separator => _proposal.Separator;
    public string Text => _proposal.Text;
    /// <summary>The channel this line is shown under today, or null when nothing claims it.</summary>
    public string? CurrentChannel { get; }
    public bool CanExclude => CurrentChannel is not null && _controller.ActiveProfile is not null;
    public string ExcludeHelp => CurrentChannel is null ? "" : L.Format(L.TeachChannelNotAChannelHelp, CurrentChannel);
    public bool CanTeach => _controller.ActiveProfile is not null;
    public string PreviewTitle => L.Format(L.TeachChannelPreview, _lines.Count);
    public ObservableCollection<string> ChannelChoices { get; } = [];
    public ObservableCollection<ChannelPreviewMatch> Matches { get; } = [];
    [ObservableProperty] private string _headPattern;
    [ObservableProperty] private string _speakerPattern;
    [ObservableProperty] private string _separatorPattern;
    [ObservableProperty] private string _pattern;
    [ObservableProperty] private int _channelIndex;
    [ObservableProperty] private string _newChannelName;
    [ObservableProperty] private string _replyCommand;
    [ObservableProperty] private bool _isPrivate;
    [ObservableProperty] private int _matchCount;
    [ObservableProperty] private string _previewSummary = "";
    [ObservableProperty] private string _error = "";
    public bool HasError => Error.Length > 0;
    public bool IsNewChannel => ChannelIndex == ChannelChoices.Count - 1;
    public string Channel => (IsNewChannel ? NewChannelName : ChannelIndex >= 0 && ChannelIndex < ChannelChoices.Count - 1 ? ChannelChoices[ChannelIndex] : "").Trim().ToLowerInvariant();
    /// <summary>The rule as the fields stand, or null while they do not make one.</summary>
    public ChannelRule? Rule => Channel.Length > 0 && Channel.Length <= 40 && Pattern.Length > 0 && Pattern.Length <= 400 && ChannelRuleSet.Compile(Pattern) is not null
        ? new(Channel, Pattern, ReplyCommand.Trim().Length == 0 ? null : ReplyCommand.Trim(), IsPrivate) : null;
    public bool CanSave => CanTeach && Rule is not null;
    public event Action? CloseRequested;

    partial void OnHeadPatternChanged(string value) => Recompose();
    partial void OnSpeakerPatternChanged(string value) => Recompose();
    partial void OnSeparatorPatternChanged(string value) => Recompose();
    partial void OnPatternChanged(string value) { if (!_composing) RefreshPreview(); }
    partial void OnChannelIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsNewChannel));
        if (!IsNewChannel) AdoptChannel(Channel);
        RefreshPreview();
    }
    partial void OnNewChannelNameChanged(string value) => RefreshPreview();
    partial void OnReplyCommandChanged(string value) => RefreshPreview();
    partial void OnIsPrivateChanged(bool value) => RefreshPreview();
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>A known channel brings its reply command and privacy with it.</summary>
    private void AdoptChannel(string channel)
    {
        var existing = _controller.ChannelRules.ForChannel(channel);
        ReplyCommand = existing?.ReplyCommand ?? ChannelRuleProposer.GuessReply(channel) ?? "";
        IsPrivate = existing?.IsPrivate ?? ChannelRuleProposer.IsPrivateChannel(channel);
    }

    private void Recompose()
    {
        if (_composing) return;
        _composing = true;
        try { Pattern = ChannelRuleProposer.Compose(HeadPattern, SpeakerPattern, SeparatorPattern, _proposal.Closing); }
        finally { _composing = false; }
        RefreshPreview();
    }

    /// <summary>Runs the rule over the recent transcript so over- and under-matching show before anything is saved.</summary>
    private void RefreshPreview()
    {
        Matches.Clear();
        var rule = Rule;
        Error = ChannelRuleSet.Compile(Pattern) is null ? L.TeachChannelInvalidPattern
            : IsNewChannel && Channel.Length == 0 ? L.TeachChannelNeedsName
            : !CanTeach ? L.TeachChannelNoProfile : "";
        if (rule is null && ChannelRuleSet.Compile(Pattern) is { })
            rule = new("preview", Pattern);
        var count = 0;
        if (rule is not null)
        {
            foreach (var line in _lines)
            {
                if (ChannelRuleSet.Apply(rule, line, DateTimeOffset.Now) is not { } message) continue;
                count++;
                if (Matches.Count < PreviewShown) Matches.Add(new(message.Speaker, message.Text));
            }
            // The example itself may have scrolled out of the window the preview looks at.
            if (count == 0 && ChannelRuleSet.Apply(rule, Example, DateTimeOffset.Now) is { } own) { count = 1; Matches.Add(new(own.Speaker, own.Text)); }
        }
        MatchCount = count;
        PreviewSummary = count == 0 ? L.TeachChannelPreviewNone : L.Format(L.TeachChannelPreviewCount, count, Math.Max(_lines.Count, 1));
        OnPropertyChanged(nameof(Rule)); OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(Channel));
        SaveCommand.NotifyCanExecuteChanged(); ExcludeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (Rule is not { } rule) return;
        try { _controller.TeachChannelRule(rule); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { Error = ex.Message; return; }
        CloseRequested?.Invoke();
    }

    /// <summary>The same shape, taught as something to leave alone.</summary>
    [RelayCommand(CanExecute = nameof(CanExclude))]
    private void Exclude()
    {
        if (CurrentChannel is not { } channel || ChannelRuleSet.Compile(Pattern) is null) { Error = L.TeachChannelInvalidPattern; return; }
        try { _controller.TeachChannelRule(new(channel, Pattern, Exclude: true)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { Error = ex.Message; return; }
        CloseRequested?.Invoke();
    }
}
