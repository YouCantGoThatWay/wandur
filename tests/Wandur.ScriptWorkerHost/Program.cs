using Wandur.Core.Scripting;

Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// A controllable unresponsive child tests the parent's deadline independently of Jint's limits.
var firstLine = await Console.In.ReadLineAsync();
if (firstLine?.Contains("test-host-unresponsive", StringComparison.Ordinal) == true)
{
    Console.WriteLine("{\"Handled\":false,\"Actions\":[],\"Error\":null}");
    await Console.Out.FlushAsync();
    await Task.Delay(Timeout.Infinite);
}
else if (firstLine is not null)
{
    await ScriptWorker.RunAsync(new PrefixedReader(firstLine + "\n", Console.In), Console.Out);
}

sealed class PrefixedReader(string prefix, TextReader rest) : TextReader
{
    private int _position;

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= prefix.Length) return rest.ReadAsync(buffer, cancellationToken);
        var length = Math.Min(buffer.Length, prefix.Length - _position);
        prefix.AsMemory(_position, length).CopyTo(buffer);
        _position += length;
        return ValueTask.FromResult(length);
    }
}
