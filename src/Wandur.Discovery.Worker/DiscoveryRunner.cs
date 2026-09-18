using System.Collections.Concurrent;
using Wandur.Models;
namespace Wandur.Discovery.Worker;

public sealed class DiscoveryRunner(WorkerStore store,IWorldProbe probe,IMappingGenerator? generator=null,int concurrency=4,int dailyCallLimit=20,TimeSpan? requestInterval=null)
{
    public async Task<WorkerState> RunAsync(IEnumerable<WorldTarget> targets,DateTimeOffset now,bool force=false,CancellationToken token=default)
    {
        if(concurrency is <1 or >16 || dailyCallLimit is <0 or >1000) throw new ArgumentOutOfRangeException(nameof(concurrency));
        var state=store.Load();
        var today=DateOnly.FromDateTime(now.UtcDateTime);
        if(today>state.BudgetDay) {state.BudgetDay=today;state.ModelCalls=0;}
        var selected=targets.Where(t=>MappingValidation.Endpoint(t.Endpoint) && MappingValidation.Text(t.WorldId,256) && !string.IsNullOrWhiteSpace(t.WorldId)).DistinctBy(t=>t.WorldId).ToArray();
        var due=new List<WorldTarget>();
        foreach(var target in selected)
        {
            if(!state.Worlds.TryGetValue(target.WorldId,out var prior) || !prior.Target.Endpoint.Matches(target.Endpoint))
                state.Worlds[target.WorldId]=prior=new() {Target=target};
            if(force || now-prior.LastProbeAt>=TimeSpan.FromDays(1)) due.Add(target);
        }
        var results=new ConcurrentDictionary<string,ProbeResult>();
        await Parallel.ForEachAsync(due.GroupBy(t=>(t.Endpoint.NormalizedHost,t.Endpoint.Port,t.Endpoint.UseTls)),
            new ParallelOptions {MaxDegreeOfParallelism=concurrency,CancellationToken=token},async (group,ct)=>
        {
            var result=await probe.ProbeAsync(group.First().Endpoint,ct);
            foreach(var target in group) results[target.WorldId]=result;
        });
        foreach(var target in selected)
        {
            token.ThrowIfCancellationRequested();
            var record=state.Worlds[target.WorldId];
            if(results.TryGetValue(target.WorldId,out var result))
            {
                record=record with {LastProbeAt=now,Status=result.Status};
                if(result.Status!="observed" || result.Evidence.Limited || result.Evidence.Fields.Length==0)
                {SaveRecord(record);continue;}
                var changedIdentity=record.Evidence.ServerId is {Length:>0} oldId && result.Evidence.ServerId is {Length:>0} newId && oldId!=newId;
                var merged=changedIdentity?result.Evidence:EvidenceCollector.Merge(record.Evidence,result.Evidence);
                if(merged.Limited) {SaveRecord(record with {Status="limited"});continue;}
                record=record with {Evidence=merged,Mapping=changedIdentity?null:record.Mapping,GenerationKey=changedIdentity?"":record.GenerationKey,PendingGenerationKey=changedIdentity?"":record.PendingGenerationKey,
                    PendingBindings=changedIdentity?[]:record.PendingBindings,NextBatch=changedIdentity?0:record.NextBatch};
            }
            if(record.Evidence.Fields.Length==0 || record.Evidence.Limited) {SaveRecord(record);continue;}
            var fingerprint=EvidenceFingerprint.Compute(record.Evidence);
            var key=fingerprint+":"+DeterministicMappings.Revision+":"+(generator?.Version??"no-ai");
            if(record.GenerationKey==key) {SaveRecord(record);continue;}
            var deterministic=DeterministicMappings.Create(record.Evidence);
            var candidate=new WorldMapping {WorldId=target.WorldId,Endpoint=target.Endpoint,SchemaFingerprint=fingerprint,
                Revision=(record.Mapping?.Revision??0)+1,GeneratedAt=now,Bindings=deterministic};
            if(generator is null)
            {
                // A maintenance run without model access must not erase previously generated bindings.
                SaveRecord(record with {Mapping=record.Mapping??candidate,Status="awaiting_model"});continue;
            }
            if(record.GenerationAttemptKey==key && now-record.LastGenerationAttemptAt<TimeSpan.FromDays(1) && record.Status=="mapping_failed")
            {SaveRecord(record);continue;}
            if(record.PendingGenerationKey!=key)
                record=record with {PendingGenerationKey=key,PendingBindings=deterministic,NextBatch=0};
            var remaining=record.Evidence.Fields.Where(f=>!deterministic.Any(b=>b.Source==f.Source))
                .OrderBy(f=>f.Source.Protocol,StringComparer.Ordinal).ThenBy(f=>f.Source.Package,StringComparer.Ordinal).ThenBy(f=>f.Source.Path,StringComparer.Ordinal).ToArray();
            var batches=remaining.Chunk(128).ToArray();
            var failed=false;
            for(var index=record.NextBatch;index<batches.Length;index++)
            {
                if(record.PendingBindings.Length>=MappingValidation.MaximumBindings) {failed=true;record=record with {Status="mapping_limit"};break;}
                if(state.ModelCalls>=dailyCallLimit) {failed=true;record=record with {Status="budget_deferred"};break;}
                var wait=state.NextModelCallAt-DateTimeOffset.UtcNow;
                if(wait>TimeSpan.FromMinutes(2)) {failed=true;record=record with {Status="rate_limited"};break;}
                if(wait>TimeSpan.Zero) await Task.Delay(wait,token);
                state.ModelCalls++;state.NextModelCallAt=DateTimeOffset.UtcNow+(requestInterval??TimeSpan.FromSeconds(15));
                record=record with {LastGenerationAttemptAt=now,GenerationAttemptKey=key,Status="generating"};
                SaveRecord(record); // Persist budget reservation before every HTTP request, including resumed batches.
                try
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    var batchEvidence=record.Evidence with {Fields=batches[index],ServerId=null};
                    var generated=await generator.GenerateAsync(batchEvidence,record.PendingBindings,timeout.Token);
                    var proposed=candidate with {Bindings=[..record.PendingBindings,..generated],Provenance="azure"};
                    if(!MappingValidation.IsValid(proposed,record.Evidence)) throw new InvalidDataException("Mapping validation failed.");
                    record=record with {PendingBindings=proposed.Bindings,NextBatch=index+1,Status="partial"};
                }
                catch(MappingRateLimitException ex)
                {state.NextModelCallAt=DateTimeOffset.UtcNow+ex.Delay;record=record with {Status="rate_limited"};failed=true;}
                catch(Exception ex) when(ex is InvalidDataException or IOException or HttpRequestException || ex is OperationCanceledException && !token.IsCancellationRequested)
                {record=record with {Status="mapping_failed"};failed=true;}
                finally
                {
                    if(generator.LastUsage is { } usage) {state.InputTokens+=usage.InputTokens;state.OutputTokens+=usage.OutputTokens;state.CachedInputTokens+=usage.CachedInputTokens;}
                }
                SaveRecord(record);
                if(failed) break;
            }
            if(!failed)
                record=record with {Mapping=candidate with {Bindings=record.PendingBindings,Provenance=batches.Length>0?"azure":"deterministic"},
                    GenerationKey=key,Status="mapped",PendingBindings=[],NextBatch=0,PendingGenerationKey=""};
            else record=record with {Mapping=record.Mapping??candidate};
            SaveRecord(record);
        }
        store.Save(state);
        return state;
        void SaveRecord(WorldScan record) {state.Worlds[record.Target.WorldId]=record;store.Save(state);}
    }
}
