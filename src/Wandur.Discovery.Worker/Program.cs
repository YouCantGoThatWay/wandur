using System.Text.Json;
using Wandur.Models;

namespace Wandur.Discovery.Worker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if(args.Contains("--help"))
        {
            Console.WriteLine("Wandur.Discovery.Worker [--cache-dir PATH] [--once] [--world ID] [--limit N] [--force] [--no-ai]\nReads directory.json; writes protocol-worker-state.json and protocol-mappings.json.\nDefault: probe each endpoint once per 24 hours. Never logs in. Azure configuration comes from the environment or directory-server/.env.");
            return 0;
        }
        try
        {
            var allowed=new[]{"--cache-dir","--once","--world","--limit","--force","--no-ai"};
            for(var i=0;i<args.Length;i++)
            {
                if(!allowed.Contains(args[i])) throw new ArgumentException("Unknown worker option.");
                if(args[i] is "--cache-dir" or "--world" or "--limit")
                    if(++i>=args.Length || args[i].StartsWith("--")) throw new ArgumentException("Missing worker option value.");
            }
            string? Option(string name) { var i=Array.IndexOf(args,name);return i<0?null:args[i+1]; }
            var cache=Path.GetFullPath(Option("--cache-dir")??Path.Combine("directory-server","cache"));
            LoadEnvironment(Path.Combine(Path.GetDirectoryName(cache)!,".env"));
            var concurrency=Setting("WANDUR_DISCOVERY_CONCURRENCY",8,1,16);
            var daily=Setting("WANDUR_MAPPING_DAILY_CALL_LIMIT",20,0,1000);
            var interval=Setting("WANDUR_MAPPING_REQUEST_INTERVAL_SECONDS",15,1,3600);
            var seconds=Setting("WANDUR_DISCOVERY_PROBE_SECONDS",15,2,60);
            var limit=Option("--limit") is { } count ? int.Parse(count,System.Globalization.CultureInfo.InvariantCulture) : int.MaxValue;
            if(limit<1) throw new ArgumentException("Limit must be positive.");
            using var http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false}) {Timeout=TimeSpan.FromSeconds(90)};
            IMappingGenerator? generator=null;
            var deployment=Environment.GetEnvironmentVariable("WANDUR_MAPPING_MODEL");
            if(!args.Contains("--no-ai") && !string.IsNullOrWhiteSpace(deployment))
            {
                var endpoint=Environment.GetEnvironmentVariable("WANDUR_MAPPING_ENDPOINT")??Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
                var key=Environment.GetEnvironmentVariable("WANDUR_MAPPING_API_KEY")??Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
                if(string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Mapping deployment needs an Azure endpoint and key.");
                var uri=new Uri(endpoint);AzureMappingGenerator.RequestUri(uri);
                generator=new AzureMappingGenerator(http,uri,deployment,key,Environment.GetEnvironmentVariable("WANDUR_MAPPING_REASONING_EFFORT")??"low");
            }
            var store=new WorkerStore(cache);
            using var lease=store.AcquireLease();
            using var stop=new CancellationTokenSource();
            Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
            Console.WriteLine(generator is null?"Discovery active; Azure mapping disabled until WANDUR_MAPPING_MODEL is configured.":"Discovery and Azure mapping active.");
            var runner=new DiscoveryRunner(store,new WorldProbe(TimeSpan.FromSeconds(seconds)),generator,concurrency,daily,TimeSpan.FromSeconds(interval));
            do
            {
                var targets=ReadTargets(Path.Combine(cache,"directory.json"));
                if(Option("--world") is { } world) targets=targets.Where(t=>t.WorldId==world).ToArray();
                targets=targets.Take(limit).ToArray();
                if(targets.Length==0 && Option("--world") is not null) throw new ArgumentException("No matching connectable world.");
                var state=await runner.RunAsync(targets,DateTimeOffset.UtcNow,args.Contains("--force"),stop.Token);
                var selected=targets.Select(t=>state.Worlds.GetValueOrDefault(t.WorldId)).Where(s=>s is not null).ToArray();
                Console.WriteLine($"{DateTimeOffset.UtcNow:O} Worlds: {selected.Length}; mappings: {selected.Count(s=>s!.Mapping is not null)}; today's model calls: {state.ModelCalls}/{daily}.");
                foreach(var group in selected.GroupBy(s=>s!.Status).OrderBy(g=>g.Key)) Console.WriteLine($"  {group.Key}: {group.Count()}");
                if(args.Contains("--once")) break;
                // Persisted per-world timestamps, not process uptime, determine daily eligibility.
                await Task.Delay(TimeSpan.FromMinutes(5),stop.Token);
            } while(!stop.IsCancellationRequested);
            return 0;
        }
        catch(OperationCanceledException) {return 0;}
        catch(Exception ex) when(ex is IOException or JsonException or ArgumentException or FormatException or OverflowException)
        {
            // Never print provider response bodies, configuration values or exception messages containing URLs/secrets.
            Console.Error.WriteLine("Discovery worker could not complete: "+ex.GetType().Name+". Check configuration, cache integrity and whether another worker holds the lock.");
            return 1;
        }
    }
    public static WorldTarget[] ReadTargets(string path)
    {
        if(new FileInfo(path).Length>32*1024*1024) throw new InvalidDataException("Directory too large.");
        using var doc=JsonDocument.Parse(File.ReadAllText(path),new JsonDocumentOptions {MaxDepth=32});
        if(doc.RootElement.GetProperty("schema_version").GetInt32()!=2 || doc.RootElement.GetProperty("format").GetString()!="wandur.directory") throw new InvalidDataException("Unsupported directory schema.");
        var targets=new List<WorldTarget>();
        foreach(var world in doc.RootElement.GetProperty("worlds").EnumerateArray().Take(5000))
        {
            if(world.TryGetProperty("web_only",out var web) && web.ValueKind==JsonValueKind.True) continue;
            if(!world.TryGetProperty("id",out var id) || id.ValueKind!=JsonValueKind.String || !world.TryGetProperty("host",out var host) || host.ValueKind!=JsonValueKind.String) continue;
            var tls=false;int port;
            if(world.TryGetProperty("port",out var regular) && regular.TryGetInt32Safe(out port)) { }
            else if(world.TryGetProperty("tls_port",out var secure) && secure.TryGetInt32Safe(out port)) tls=true;
            else continue;
            var endpoint=new WorldEndpoint(host.GetString()!,port,tls);
            if(MappingValidation.Endpoint(endpoint) && MappingValidation.Text(id.GetString(),256)) targets.Add(new(id.GetString()!,endpoint));
        }
        return targets.DistinctBy(t=>t.WorldId).ToArray();
    }
    private static bool TryGetInt32Safe(this JsonElement value,out int number) {number=0;return value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out number);}
    private static int Setting(string name,int fallback,int min,int max)
    {
        var text=Environment.GetEnvironmentVariable(name);var value=text is null?fallback:int.Parse(text,System.Globalization.CultureInfo.InvariantCulture);
        return value>=min&&value<=max?value:throw new ArgumentException("Worker setting out of range.");
    }
    private static void LoadEnvironment(string path)
    {
        if(!File.Exists(path)) return;
        foreach(var raw in File.ReadLines(path))
        {
            var line=raw.Trim();if(line.StartsWith('#') || !line.Contains('=')) continue;
            var at=line.IndexOf('=');var key=line[..at].Trim();
            if(!(key.StartsWith("WANDUR_MAPPING_",StringComparison.Ordinal)||key.StartsWith("WANDUR_DISCOVERY_",StringComparison.Ordinal)||key is "AZURE_OPENAI_ENDPOINT" or "AZURE_OPENAI_API_KEY") || Environment.GetEnvironmentVariable(key) is not null) continue;
            var value=line[(at+1)..].Trim();
            if(value.Length>=2 && value[0]==value[^1] && value[0] is '\'' or '"') value=value[1..^1];
            Environment.SetEnvironmentVariable(key,value);
        }
    }
}
