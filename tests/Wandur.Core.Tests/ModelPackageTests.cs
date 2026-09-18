using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wandur.Core.Classification;

namespace Wandur.Core.Tests;

public sealed class ModelPackageTests
{
    internal static string CreateFakePackage(string? tamper = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wandur-model-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var files = new Dictionary<string, string>
        {
            ["encoder.onnx"] = "not-a-real-model",
            ["tokenizer.json"] = JsonSerializer.Serialize(new { model = new { type = "WordPiece", vocab = new Dictionary<string, int> { ["[PAD]"] = 0, ["[UNK]"] = 1, ["[CLS]"] = 2, ["[SEP]"] = 3, ["hall"] = 4, ["##s"] = 5 } } }),
            ["head.json"] = JsonSerializer.Serialize(new { classes = new[] { "cave", "forest" }, coef = new[] { Enumerable.Repeat(0.5, 384).ToArray(), Enumerable.Repeat(-0.5, 384).ToArray() }, intercept = new[] { 0.1, -0.1 } }),
            ["preprocessing_spec.json"] = JsonSerializer.Serialize(new { max_word_pieces = 256, threshold = 0.8, taxonomy_version = "1.0.0" }),
            ["taxonomy.json"] = JsonSerializer.Serialize(new { version = "1.0.0", bases = new[] { "cave", "forest" } }),
        };
        foreach (var (name, content) in files) File.WriteAllText(Path.Combine(dir, name), content);
        var manifest = new { version = "9.9.9", files = files.ToDictionary(f => f.Key, f => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(f.Value)))) };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
        if (tamper is not null) File.WriteAllText(Path.Combine(dir, tamper), "tampered");
        return dir;
    }

    [Fact]
    public void LoadsAVerifiedPackage()
    {
        var package = ModelPackage.Load(CreateFakePackage());
        Assert.Equal("9.9.9", package.Version); Assert.Equal(0.8, package.Threshold); Assert.Equal(256, package.MaxWordPieces);
        Assert.Equal(["cave", "forest"], package.Classes);
        Assert.Equal(2, package.Coefficients.GetLength(0)); Assert.Equal(384, package.Coefficients.GetLength(1));
        Assert.Equal(0.1f, package.Intercepts[0]);
        using var reader = new StreamReader(package.OpenVocabulary());
        Assert.Equal(["[PAD]", "[UNK]", "[CLS]", "[SEP]", "hall", "##s"], reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void RejectsTamperedAndIncompletePackages()
    {
        Assert.Throws<InvalidDataException>(() => ModelPackage.Load(CreateFakePackage(tamper: "head.json")));
        var missing = CreateFakePackage(); File.Delete(Path.Combine(missing, "encoder.onnx"));
        Assert.Throws<InvalidDataException>(() => ModelPackage.Load(missing));
        var noManifest = CreateFakePackage(); File.Delete(Path.Combine(noManifest, "manifest.json"));
        Assert.Throws<InvalidDataException>(() => ModelPackage.Load(noManifest));
    }

    [Fact]
    public void MalformedManifestOrHeadSurfacesAsInvalidData()
    {
        var corruptManifest = CreateFakePackage(); File.WriteAllText(Path.Combine(corruptManifest, "manifest.json"), "{ not json");
        Assert.Throws<InvalidDataException>(() => ModelPackage.Load(corruptManifest));
        var missingKey = CreateFakePackage(); File.WriteAllText(Path.Combine(missingKey, "manifest.json"), "{\"version\":\"1\"}");
        Assert.Throws<InvalidDataException>(() => ModelPackage.Load(missingKey));
    }
}
