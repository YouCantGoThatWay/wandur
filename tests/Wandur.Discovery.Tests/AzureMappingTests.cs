using System.Net;
using System.Text.Json;
using Wandur.Discovery.Worker;
using Wandur.Models;
namespace Wandur.Discovery.Tests;
public class AzureMappingTests
{
    private sealed class Provider(Func<HttpRequestMessage,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)=>send(r); }
    private static ProtocolEvidence Evidence()=>new() { Fields=[new(new("MSDP","MSDP","/JETPACKFUEL"),["string"],true)] };
    [Fact]
    public async Task SendsOnlySchemaAndAcceptsValidatedExtensionBindings()
    {
        using var client=new HttpClient(new Provider(async request=>
        {
            Assert.Equal("https://example.openai.azure.com/openai/v1/chat/completions",request.RequestUri!.ToString());
            Assert.Equal("test-key",request.Headers.GetValues("api-key").Single());
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("my-luna",json.RootElement.GetProperty("model").GetString());
            Assert.Equal("low",json.RootElement.GetProperty("reasoning_effort").GetString());
            var content="""{"bindings":[{"source":{"protocol":"MSDP","package":"MSDP","path":"/JETPACKFUEL"},"target":{"entity":"character","category":"resource","key":"jetpack_fuel","member":"current"},"label":"Jetpack fuel","conversion":"number","scale":1}]}""";
            return new(HttpStatusCode.OK) { Content=new StringContent(JsonSerializer.Serialize(new { choices=new[]{new {finish_reason="stop",message=new {content}}} })) };
        }));
        var bindings=await new AzureMappingGenerator(client,new("https://example.openai.azure.com/"),"my-luna","test-key").GenerateAsync(Evidence(),[],default);
        Assert.Equal("jetpack_fuel",Assert.Single(bindings).Target.Key);
    }
    [Theory]
    [InlineData("length","/JETPACKFUEL")]
    [InlineData("stop","/INVENTED")]
    public async Task TruncatedAndInventedMappingsAreRejected(string finish,string path)
    {
        using var http=new HttpClient(new Provider(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content=new StringContent(JsonSerializer.Serialize(new {choices=new[]{new {finish_reason=finish,message=new {content=JsonSerializer.Serialize(new {bindings=new[]{new FieldBinding { Source=new("MSDP","MSDP",path),Target=new("character","resource","fuel","current"),Label="Fuel" }}},ModelJson.Options)}}} }))
        })));
        await Assert.ThrowsAsync<InvalidDataException>(()=>new AzureMappingGenerator(http,new("https://example.openai.azure.com"),"test","key").GenerateAsync(Evidence(),[],default));
    }

    [Fact]
    public async Task ConflictingSuggestionsAreOmittedWithoutDiscardingOtherValidatedBindings()
    {
        var evidence=new ProtocolEvidence { Fields=new[]{"SERVER_TIME","WORLD_TIME","JETPACKFUEL","HP"}
            .Select(s=>new ObservedField(new("MSDP","MSDP","/"+s),["unknown"],true)).ToArray() };
        var existing=new FieldBinding { Source=new("MSDP","MSDP","/HEALTH"),Target=new("character","resource","health","current"),Label="Health" };
        var suggestions=new[]{
            new FieldBinding {Source=evidence.Fields[0].Source,Target=new("world","metric","time","value"),Label="Server time"},
            new FieldBinding {Source=evidence.Fields[1].Source,Target=new("world","metric","time","value"),Label="World time"},
            new FieldBinding {Source=evidence.Fields[2].Source,Target=new("character","resource","fuel","current"),Label="Fuel"},
            new FieldBinding {Source=evidence.Fields[3].Source,Target=existing.Target,Label="Health"}
        };
        using var http=new HttpClient(new Provider(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content=new StringContent(JsonSerializer.Serialize(new {choices=new[]{new {finish_reason="stop",message=new {content=JsonSerializer.Serialize(new {bindings=suggestions},ModelJson.Options)}}} }))
        })));
        var result=await new AzureMappingGenerator(http,new("https://example.openai.azure.com"),"test","key").GenerateAsync(evidence,[existing],default);
        Assert.Equal("fuel",Assert.Single(result).Target.Key);
    }
}
