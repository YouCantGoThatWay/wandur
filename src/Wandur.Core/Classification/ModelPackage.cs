using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wandur.Core.Classification;

/// <summary>A verified on-disk room-classifier package (manifest.json + model assets).</summary>
public sealed class ModelPackage
{
    public const string SupportedTaxonomyVersion = "1.0.0";
    private static readonly string[] Required = ["encoder.onnx", "tokenizer.json", "head.json", "preprocessing_spec.json", "taxonomy.json"];

    public required string Directory { get; init; }
    public required string Version { get; init; }
    public required string TaxonomyVersion { get; init; }
    public required double Threshold { get; init; }
    public required int MaxWordPieces { get; init; }
    public required IReadOnlyList<string> Classes { get; init; }
    public required float[,] Coefficients { get; init; }
    public required float[] Intercepts { get; init; }
    public string EncoderPath => Path.Combine(Directory, "encoder.onnx");
    public string TokenizerPath => Path.Combine(Directory, "tokenizer.json");

    public static ModelPackage Load(string directory)
    {
        try { return LoadCore(directory); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        { throw new InvalidDataException("Malformed package metadata.", ex); }
    }

    private static ModelPackage LoadCore(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("Missing manifest.json.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var version = manifest.RootElement.GetProperty("version").GetString() ?? throw new InvalidDataException("Missing package version.");
        var files = manifest.RootElement.GetProperty("files");
        foreach (var required in Required)
            if (!files.TryGetProperty(required, out _)) throw new InvalidDataException($"Manifest does not list {required}.");
        foreach (var entry in files.EnumerateObject())
        {
            var path = Path.Combine(directory, entry.Name);
            if (!File.Exists(path)) throw new InvalidDataException($"Missing {entry.Name}.");
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actual, entry.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{entry.Name} failed hash verification.");
        }
        using var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "preprocessing_spec.json")));
        using var taxonomy = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "taxonomy.json")));
        var taxonomyVersion = taxonomy.RootElement.GetProperty("version").GetString() ?? "";
        if (taxonomyVersion != SupportedTaxonomyVersion) throw new InvalidDataException($"Unsupported taxonomy {taxonomyVersion}.");
        using var head = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "head.json")));
        var classes = head.RootElement.GetProperty("classes").EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
        var coefRows = head.RootElement.GetProperty("coef").EnumerateArray().Select(r => r.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray()).ToArray();
        var intercepts = head.RootElement.GetProperty("intercept").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
        if (classes.Length == 0 || coefRows.Length != classes.Length || intercepts.Length != classes.Length || coefRows.Any(r => r.Length != coefRows[0].Length))
            throw new InvalidDataException("Inconsistent classifier head.");
        var coefficients = new float[classes.Length, coefRows[0].Length];
        for (var i = 0; i < classes.Length; i++) for (var j = 0; j < coefRows[0].Length; j++) coefficients[i, j] = coefRows[i][j];
        return new ModelPackage
        {
            Directory = directory, Version = version, TaxonomyVersion = taxonomyVersion,
            Threshold = spec.RootElement.TryGetProperty("threshold", out var threshold) ? threshold.GetDouble() : 0.8,
            MaxWordPieces = spec.RootElement.TryGetProperty("max_word_pieces", out var max) ? max.GetInt32() : 256,
            Classes = classes, Coefficients = coefficients, Intercepts = intercepts
        };
    }

    /// <summary>One token per line ordered by id, as Microsoft.ML.Tokenizers' BertTokenizer expects.</summary>
    public Stream OpenVocabulary()
    {
        using var tokenizer = JsonDocument.Parse(File.ReadAllText(TokenizerPath));
        var vocab = tokenizer.RootElement.GetProperty("model").GetProperty("vocab").EnumerateObject()
            .Select(p => (Token: p.Name, Id: p.Value.GetInt32())).OrderBy(p => p.Id).ToArray();
        for (var i = 0; i < vocab.Length; i++)
            if (vocab[i].Id != i) throw new InvalidDataException("Vocabulary ids are not contiguous.");
        var builder = new StringBuilder();
        foreach (var (token, _) in vocab) builder.Append(token).Append('\n');
        return new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString()));
    }
}
