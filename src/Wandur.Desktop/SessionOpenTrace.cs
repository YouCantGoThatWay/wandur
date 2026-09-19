using System.Diagnostics;

namespace Wandur.Desktop;

/// <summary>
/// Opt-in phase timing for the session open path. Disabled by default: <see cref="Measure"/> then
/// returns an empty scope whose disposal does nothing, so instrumented code pays one static read.
/// </summary>
internal static class SessionOpenTrace
{
    private static readonly Lock Gate = new();
    private static readonly List<(string Phase, double Milliseconds)> Samples = [];
    private static readonly Dictionary<string, int> Counts = [];

    internal static bool Enabled { get; set; }

    internal readonly struct Scope(string? phase, long started) : IDisposable
    {
        public void Dispose()
        {
            if (phase is not null) Record(phase, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    internal static Scope Measure(string phase) => Enabled ? new(phase, Stopwatch.GetTimestamp()) : default;

    internal static void Record(string phase, double milliseconds)
    {
        if (!Enabled) return;
        lock (Gate) Samples.Add((phase, milliseconds));
    }

    internal static void Reset() { lock (Gate) { Samples.Clear(); Counts.Clear(); } }

    internal static void Count(string phase)
    {
        if (!Enabled) return;
        lock (Gate) Counts[phase] = Counts.GetValueOrDefault(phase) + 1;
    }

    internal static IReadOnlyDictionary<string, int> Calls() { lock (Gate) return new Dictionary<string, int>(Counts); }

    /// <summary>Phase totals in first-seen order, so repeated phases in one open are summed.</summary>
    internal static IReadOnlyList<(string Phase, double Milliseconds)> Breakdown()
    {
        lock (Gate)
        {
            var totals = new List<(string Phase, double Milliseconds)>();
            foreach (var (phase, milliseconds) in Samples)
            {
                var index = totals.FindIndex(entry => entry.Phase == phase);
                if (index < 0) totals.Add((phase, milliseconds));
                else totals[index] = (phase, totals[index].Milliseconds + milliseconds);
            }
            return totals;
        }
    }
}
