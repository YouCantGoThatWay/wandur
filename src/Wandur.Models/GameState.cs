namespace Wandur.Models;

public sealed record Observation<T>(T Value, DateTimeOffset ReceivedAt)
{
    public bool IsStale(DateTimeOffset now, TimeSpan age) => now - ReceivedAt > age;
}

public sealed record ResourceState(string Label, Observation<double>? Current, Observation<double>? Maximum)
{
    public double? Percentage => Current is not null && Maximum is { Value: > 0 }
        && double.IsFinite(Current.Value / Maximum.Value * 100) ? Current.Value / Maximum.Value * 100 : null;
}
public sealed record AttributeState(string Label, Observation<double>? Current, Observation<double>? Base);
public sealed record CurrencyState(string Label, Observation<double>? Carried, Observation<double>? Bank, Observation<double>? Total);
public sealed record MetricState(string Label, Observation<string>? Text, Observation<double>? Number, Observation<bool>? Boolean);

/// <summary>Character, opponent and vehicle share optional capabilities. Absent data stays absent.</summary>
public sealed record EntityState
{
    public Dictionary<string, Observation<string>> Identity { get; init; } = [];
    public Dictionary<string, ResourceState> Resources { get; init; } = [];
    public Dictionary<string, ResourceState> Progression { get; init; } = [];
    public Dictionary<string, AttributeState> Attributes { get; init; } = [];
    public Dictionary<string, CurrencyState> Currencies { get; init; } = [];
    public Dictionary<string, MetricState> Metrics { get; init; } = [];
    public Dictionary<string, Observation<string>> Location { get; init; } = [];
}

/// <summary>One connection and character lifetime. Clear on reconnect or identity change.</summary>
public sealed record GameState
{
    public EntityState Character { get; init; } = new();
    public EntityState Opponent { get; init; } = new();
    public EntityState Vehicle { get; init; } = new();
    public EntityState World { get; init; } = new();
}
