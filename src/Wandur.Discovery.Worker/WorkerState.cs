using System.Text.Json;
using Wandur.Models;
namespace Wandur.Discovery.Worker;

public sealed record WorldTarget(string WorldId,WorldEndpoint Endpoint);
public sealed record WorldScan
{
    public required WorldTarget Target { get; init; }
    public DateTimeOffset LastProbeAt { get; init; }
    public string Status { get; init; }="pending";
    public ProtocolEvidence Evidence { get; init; }=new();
    public WorldMapping? Mapping { get; init; }
    public DateTimeOffset LastGenerationAttemptAt { get; init; }
    public string GenerationAttemptKey { get; init; }="";
    public string PendingGenerationKey { get; init; }="";
    public FieldBinding[] PendingBindings { get; init; }=[];
    public int NextBatch { get; init; }
    public string GenerationKey { get; init; }="";
}
public sealed record WorkerState
{
    public int SchemaVersion { get; init; }=1;
    public Dictionary<string,WorldScan> Worlds { get; init; }=[];
    public DateOnly BudgetDay { get; set; }
    public int ModelCalls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public DateTimeOffset NextModelCallAt { get; set; }
}
public sealed class WorkerStore(string directory)
{
    public string DirectoryPath { get; }=Path.GetFullPath(directory);
    public WorkerState Load()
    {
        var path=Path.Combine(DirectoryPath,"protocol-worker-state.json");
        if(!File.Exists(path)) return new();
        if(new FileInfo(path).Length>32*1024*1024) throw new InvalidDataException("Worker state is too large.");
        var state=JsonSerializer.Deserialize<WorkerState>(File.ReadAllText(path),ModelJson.Options);
        if(state is null || state.SchemaVersion!=1 || state.Worlds is null || state.ModelCalls<0) throw new InvalidDataException("Invalid worker state; refusing to reset the budget.");
        return state;
    }
    public void Save(WorkerState state)
    {
        Atomic("protocol-worker-state.json",state);
        Atomic("protocol-mappings.json",new MappingCatalog { Worlds=state.Worlds.Values.Select(s=>s.Mapping).Where(m=>MappingValidation.IsValid(m)).Cast<WorldMapping>().OrderBy(m=>m.WorldId,StringComparer.Ordinal).ToArray() });
    }
    private void Atomic<T>(string name,T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path=Path.Combine(DirectoryPath,name);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllText(temp,JsonSerializer.Serialize(value,ModelJson.Options));File.Move(temp,path,true); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public FileStream AcquireLease()
    {
        Directory.CreateDirectory(DirectoryPath);
        return new FileStream(Path.Combine(DirectoryPath,"protocol-worker.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
}
