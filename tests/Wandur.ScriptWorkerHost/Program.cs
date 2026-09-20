using Wandur.Core.Scripting;

Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// The real worker loop, behind a reader that tests can steer: a request carrying "test-host-unresponsive" is
// never answered, which exercises the parent's deadline independently of Jint's limits, and one carrying
// "test-host-crash" ends the process at once, which exercises crash recovery.
await ScriptWorker.RunAsync(new HookedReader(Console.In), Console.Out);

sealed class HookedReader(TextReader inner) : TextReader
{
    private string _pending = "";
    private int _position;

    public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= _pending.Length)
        {
            var line = await inner.ReadLineAsync(cancellationToken);
            if (line is null) return 0;
            if (line.Contains("test-host-unresponsive", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (line.Contains("test-host-crash", StringComparison.Ordinal)) Environment.Exit(70);
            _pending = line + "\n"; _position = 0;
        }
        var length = Math.Min(buffer.Length, _pending.Length - _position);
        _pending.AsMemory(_position, length).CopyTo(buffer);
        _position += length;
        return length;
    }
}
