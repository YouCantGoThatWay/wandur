using Wandur.Discovery.Worker;
using Wandur.Models;
namespace Wandur.Discovery.Tests;
public class SchedulingTests
{
    private sealed class Probe : IWorldProbe
    {
        public ProbeResult Result=new("observed",new() {Fields=[new(new("MSDP","MSDP","/HEALTH"),["string"],true),new(new("MSDP","MSDP","/JETPACKFUEL"),["unknown"],true)]});
        public int Calls;
        public Task<ProbeResult> ProbeAsync(WorldEndpoint e,CancellationToken t) { Calls++; return Task.FromResult(Result); }
    }
    private sealed class Model : IMappingGenerator
    {
        public string Version=>"test-model-1"; public bool Fail;
        public Task<FieldBinding[]> GenerateAsync(ProtocolEvidence e,FieldBinding[] b,CancellationToken t)
        {
            if(Fail) throw new InvalidDataException("bad output");
            return Task.FromResult(new[]{new FieldBinding {Source=new("MSDP","MSDP","/JETPACKFUEL"),Target=new("character","resource","fuel","current"),Label="Fuel"}});
        }
    }
    private static WorldTarget Target(string id="world")=>new(id,new("example.org",4000));
    [Fact]
    public async Task DailyScanAndUnchangedSchemaSurviveProcessRestartWithoutAnotherModelCall()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());var store=new WorkerStore(path);var probe=new Probe();var model=new Model();var now=DateTimeOffset.UtcNow;
        try
        {
            var first=await new DiscoveryRunner(store,probe,model,requestInterval:TimeSpan.Zero).RunAsync([Target()],now);
            Assert.Equal("azure",first.Worlds["world"].Mapping!.Provenance);Assert.Equal(1,first.ModelCalls);
            Assert.Equal(2,first.Worlds["world"].Mapping!.Bindings.Length);
            var second=await new DiscoveryRunner(store,probe,model,requestInterval:TimeSpan.Zero).RunAsync([Target()],now.AddMinutes(1));
            Assert.Equal(1,probe.Calls);Assert.Equal(1,second.ModelCalls);
            var third=await new DiscoveryRunner(store,probe,model,requestInterval:TimeSpan.Zero).RunAsync([Target()],now.AddDays(1));
            Assert.Equal(2,probe.Calls);Assert.Equal(0,third.ModelCalls);Assert.Equal(1,third.Worlds["world"].Mapping!.Revision);
        }
        finally { if(Directory.Exists(path)) Directory.Delete(path,true); }
    }
    [Fact]
    public async Task ChangedSchemaWithFailedModelAndEmptyProbePreservesLastGoodMapping()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());var store=new WorkerStore(path);var probe=new Probe();var model=new Model();var now=DateTimeOffset.UtcNow;
        try
        {
            await new DiscoveryRunner(store,probe,model,requestInterval:TimeSpan.Zero).RunAsync([Target()],now);
            probe.Result=probe.Result with {Evidence=probe.Result.Evidence with {Fields=[..probe.Result.Evidence.Fields,new(new("MSDP","MSDP","/SHIELDENERGY"),["unknown"],true)]}};
            model.Fail=true;
            var failed=await new DiscoveryRunner(store,probe,model,requestInterval:TimeSpan.Zero).RunAsync([Target()],now.AddDays(1));
            Assert.Equal("mapping_failed",failed.Worlds["world"].Status);Assert.Equal(1,failed.Worlds["world"].Mapping!.Revision);
            Assert.Contains(failed.Worlds["world"].Mapping!.Bindings,b=>b.Target.Key=="fuel");
            probe.Result=new("no_data",new());
            var empty=await new DiscoveryRunner(store,probe,model).RunAsync([Target()],now.AddDays(2));
            Assert.Equal(1,empty.Worlds["world"].Mapping!.Revision);Assert.Equal(0,empty.ModelCalls);
        }
        finally {if(Directory.Exists(path)) Directory.Delete(path,true);}
    }
    [Fact]
    public async Task DailyBudgetPersistsAndForceDoesNotBypassIt()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());var store=new WorkerStore(path);var probe=new Probe();var now=DateTimeOffset.UtcNow;
        try
        {
            await new DiscoveryRunner(store,probe,new Model(),dailyCallLimit:1,requestInterval:TimeSpan.Zero).RunAsync([Target("a")],now);
            var result=await new DiscoveryRunner(store,probe,new Model(),dailyCallLimit:1,requestInterval:TimeSpan.Zero).RunAsync([Target("b")],now,true);
            Assert.Equal(1,result.ModelCalls);Assert.Equal("budget_deferred",result.Worlds["b"].Status);
            Assert.Equal("deterministic",result.Worlds["b"].Mapping!.Provenance);
            Assert.Contains("schema_version",File.ReadAllText(Path.Combine(path,"protocol-mappings.json")));
        }
        finally { if(Directory.Exists(path)) Directory.Delete(path,true); }
    }
    [Fact]
    public async Task DisablingModelPreservesLastGoodAzureBindings()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());var store=new WorkerStore(path);var probe=new Probe();var now=DateTimeOffset.UtcNow;
        try
        {
            await new DiscoveryRunner(store,probe,new Model(),requestInterval:TimeSpan.Zero).RunAsync([Target()],now);
            var result=await new DiscoveryRunner(store,probe).RunAsync([Target()],now.AddDays(1));
            Assert.Equal("azure",result.Worlds["world"].Mapping!.Provenance);
            Assert.Contains(result.Worlds["world"].Mapping!.Bindings,b=>b.Target.Key=="fuel");
        }
        finally {if(Directory.Exists(path)) Directory.Delete(path,true);}
    }
    private sealed class StreamFailure : IMappingGenerator
    {
        public string Version=>"stream-failure";
        public Task<FieldBinding[]> GenerateAsync(ProtocolEvidence e,FieldBinding[] b,CancellationToken t)=>throw new IOException("disconnected body");
    }
    [Fact]
    public async Task BrokenProviderBodyDoesNotAbortOtherWorlds()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());var store=new WorkerStore(path);
        try
        {
            var state=await new DiscoveryRunner(store,new Probe(),new StreamFailure(),requestInterval:TimeSpan.Zero).RunAsync([Target("a"),Target("b")],DateTimeOffset.UtcNow);
            Assert.Equal(2,state.Worlds.Count);Assert.All(state.Worlds.Values,s=>Assert.Equal("mapping_failed",s.Status));
        }
        finally {if(Directory.Exists(path)) Directory.Delete(path,true);}
    }
    private sealed class ManyFields : IMappingGenerator
    {
        public string Version=>"many-fields";
        public Task<FieldBinding[]> GenerateAsync(ProtocolEvidence e,FieldBinding[] b,CancellationToken t)=>Task.FromResult(e.Fields.Where(f=>!b.Any(n=>n.Source==f.Source)).Take(128)
            .Select(f=>new FieldBinding {Source=f.Source,Target=new("character","metric",f.Source.Path.TrimStart('/').ToLowerInvariant(),"value"),Label="Metric"}).ToArray());
    }
    [Fact]
    public async Task MoreThanOneBatchResumesAcrossDailyBudgetWithoutLosingTailFields()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());var store=new WorkerStore(path);var now=DateTimeOffset.UtcNow;
        var probe=new Probe {Result=new("observed",new(){Fields=Enumerable.Range(0,130).Select(i=>new ObservedField(new("MSDP","MSDP","/CUSTOM"+i),["string"],true)).ToArray()})};
        try
        {
            var first=await new DiscoveryRunner(store,probe,new ManyFields(),dailyCallLimit:1,requestInterval:TimeSpan.Zero).RunAsync([Target()],now);
            Assert.Equal("budget_deferred",first.Worlds["world"].Status);
            var second=await new DiscoveryRunner(store,probe,new ManyFields(),dailyCallLimit:1,requestInterval:TimeSpan.Zero).RunAsync([Target()],now.AddDays(1));
            Assert.Equal("mapped",second.Worlds["world"].Status);
            Assert.Equal(130,second.Worlds["world"].Mapping!.Bindings.Length);
            Assert.Contains(second.Worlds["world"].Mapping!.Bindings,b=>b.Source.Path=="/CUSTOM129");
        }
        finally {if(Directory.Exists(path)) Directory.Delete(path,true);}
    }
    [Fact]
    public async Task OverflowingMergedEvidenceStillRecordsTheDailyProbeAttempt()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());var store=new WorkerStore(path);var now=DateTimeOffset.UtcNow;
        var probe=new Probe {Result=new("observed",new(){Fields=Enumerable.Range(0,1000).Select(i=>new ObservedField(new("GMCP","Char.Stats","/field"+i),["number"])).ToArray()})};
        try
        {
            await new DiscoveryRunner(store,probe).RunAsync([Target()],now);
            probe.Result=new("observed",new(){Fields=Enumerable.Range(1000,50).Select(i=>new ObservedField(new("GMCP","Char.Stats","/field"+i),["number"])).ToArray()});
            await new DiscoveryRunner(store,probe).RunAsync([Target()],now.AddDays(1));
            var result=await new DiscoveryRunner(store,probe).RunAsync([Target()],now.AddDays(1).AddMinutes(5));
            Assert.Equal(2,probe.Calls);Assert.Equal(now.AddDays(1),result.Worlds["world"].LastProbeAt);
        }
        finally {if(Directory.Exists(path)) Directory.Delete(path,true);}
    }

}
