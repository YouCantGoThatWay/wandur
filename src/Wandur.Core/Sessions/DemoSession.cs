namespace Wandur.Core.Sessions;

public sealed class DemoSession : IMudSession
{
    public event Action<string>? Output;
    public event Action<SessionStatus>? StatusChanged;
    public event Action<bool>? PrivateInputChanged;
    private int _room;
    public bool IsConnected { get; private set; }
    private sealed record Room(string Name, string Description, Dictionary<string, int> Exits);
    private static readonly Room[] Rooms =
    [
        new("The Lantern & the Rain", "Rain taps against the leaded windows of a small wayside inn.\nAn amber lantern hangs above the hearth, catching the edges of\na map spread across an oak table. Somewhere, a kettle sings.\n\nThe innkeeper looks up. \"There's still a little daylight left.\nThe old market is north of here, if you've a mind to wander.\"", new() { ["north"] = 1, ["east"] = 2 }),
        new("The Old Market", "Water gathers between worn cobblestones. The market stalls\nare shuttered for the evening, but a flower seller has left\na jar of white asters beneath the clock tower.\n\nA narrow stair curls upward into the tower.", new() { ["south"] = 0, ["up"] = 3 }),
        new("A Bridge of Moss & Stone", "A low stone bridge crosses a stream the color of old silver.\nBeyond the arch, the forest breathes mist into the valley.\nThe lights of the inn glow through the rain to the west.", new() { ["west"] = 0, ["east"] = 4 }),
        new("Above the Rooftops", "The clock has stopped at a quarter past something. From here\nyou can see the inn, the bridge, and the dark line of the woods.\nA swallow has made a home inside the silent bell.", new() { ["down"] = 1 }),
        new("The Edge of the Wood", "Wet pine needles soften your footsteps. Fireflies gather\nbetween the roots of an ancient oak. A weathered sign reads:\n\"Every world begins with a first step.\"", new() { ["west"] = 2 })
    ];

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _room = 0; IsConnected = true;
        StatusChanged?.Invoke(new(true, "Offline demo · no server required"));
        PrivateInputChanged?.Invoke(false);
        Output?.Invoke("\u001b[38;2;209;155;104m  W U N D U R\u001b[0m\n  A little world, waiting to be wandered.\n\n\u001b[90m  OFFLINE DEMO  ·  Five rooms to explore\n  Try look, north, east, up, who, or help.\u001b[0m\n\n");
        Describe();
        return Task.CompletedTask;
    }

    public Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected) throw new InvalidOperationException("Start the demo first.");
        Output?.Invoke("\n");
        var value = command.Trim().ToLowerInvariant();
        value = value switch { "n" => "north", "s" => "south", "e" => "east", "w" => "west", "u" => "up", "d" => "down", "l" => "look", _ => value };
        if (Rooms[_room].Exits.TryGetValue(value, out var next)) { _room = next; Describe(); }
        else if (value == "look" || value.Length == 0) Describe();
        else if (value is "north" or "south" or "east" or "west" or "up" or "down") Say("You can't go that way.\n");
        else if (value == "help") Say("DEMO COMMANDS\n  look           Read the room again\n  n / s / e / w  Follow an exit\n  up / down      Take the tower stairs\n  who            See the demo cast\n  say <message>  Speak into the world\n  inventory      Check your pockets\n\nThis is a local, scripted sample. Connect to your own MUD\nfrom the world library for a real adventure.\n");
        else if (value == "who") Say("OFFLINE DEMO CAST\n  You          A returning wanderer\n  Innkeeper    A scripted character\n\nNo other players are connected.\n");
        else if (value is "inventory" or "i") Say("You carry a brass compass, a folded map, and a little curiosity.\n");
        else if (value.StartsWith("say ")) Say($"You say, \"{command.Trim()[4..]}\"\n" + (_room == 0 ? "The innkeeper smiles. \"Good to have a voice in this old place.\"\n" : "Your words mingle with the sound of the rain.\n"));
        else Say("That command isn't part of the demo. Try help.\n");
        return Task.CompletedTask;
    }

    private void Describe()
    {
        var room = Rooms[_room];
        Output?.Invoke($"\u001b[1;38;2;229;192;123m{room.Name}\u001b[0m\n\u001b[90m{new string('─', 42)}\u001b[0m\n{room.Description}\n\n\u001b[36mExits: {string.Join("  ·  ", room.Exits.Keys)}\u001b[0m\n\n> ");
    }
    private void Say(string text) => Output?.Invoke(text + "\n> ");
    public ValueTask DisposeAsync()
    {
        if (IsConnected) { IsConnected = false; StatusChanged?.Invoke(new(false, "Demo ended")); }
        return ValueTask.CompletedTask;
    }
}
