using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Wandur.Core.Classification;

/// <summary>Reference-parity implementation of the package's preprocessing_spec.json over ONNX Runtime (batch=1, no padding).</summary>
public sealed class OnnxRoomEnvironmentClassifier : IRoomEnvironmentClassifier, IDisposable
{
    private readonly ModelPackage _package;
    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly object _gate = new();

    public OnnxRoomEnvironmentClassifier(ModelPackage package)
    {
        _package = package;
        using var vocabulary = package.OpenVocabulary();
        _tokenizer = BertTokenizer.Create(vocabulary, new BertOptions { LowerCaseBeforeTokenization = true, RemoveNonSpacingMarks = true });
        _session = new InferenceSession(package.EncoderPath);
    }

    public string ModelVersion => _package.Version;
    public double DefaultThreshold => _package.Threshold;

    public RoomEnvironmentPrediction? Classify(string name, string description, double threshold)
    {
        var probabilities = Probabilities(RoomTextPreprocessor.BuildText(name, description));
        var best = 0;
        for (var i = 1; i < probabilities.Length; i++) if (probabilities[i] > probabilities[best]) best = i;
        return probabilities[best] >= threshold ? new(_package.Classes[best], probabilities[best], ModelVersion) : null;
    }

    /// <summary>
    /// Hugging Face's BertNormalizer clean_text step turns every whitespace character (including the "\n" that
    /// BuildText joins name and description with) into a plain space before word splitting; Microsoft.ML.Tokenizers
    /// does not, so "Cart\nA" would tokenize as one word. Normalizing here keeps the stored text unchanged.
    /// </summary>
    private static string CollapseToSpaces(string text)
    {
        Span<char> buffer = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        for (var i = 0; i < text.Length; i++) buffer[i] = char.IsWhiteSpace(text[i]) ? ' ' : text[i];
        return new string(buffer);
    }

    /// <summary>[CLS] + at most (MaxWordPieces - 2) word pieces + [SEP], matching Hugging Face truncation.</summary>
    internal int[] Tokenize(string text)
    {
        var ids = _tokenizer.EncodeToIds(CollapseToSpaces(text), addSpecialTokens: false, considerPreTokenization: true, considerNormalization: true);
        var body = ids.Take(_package.MaxWordPieces - 2);
        return [_tokenizer.ClassificationTokenId, .. body, _tokenizer.SeparatorTokenId];
    }

    internal float[] Embed(string text)
    {
        var ids = Tokenize(text);
        var inputIds = new DenseTensor<long>(ids.Select(i => (long)i).ToArray(), [1, ids.Length]);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), [1, ids.Length]);
        float[] hidden;
        int width;
        lock (_gate)
        {
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input_ids", inputIds), NamedOnnxValue.CreateFromTensor("attention_mask", mask)]);
            var tensor = results.First().AsTensor<float>();
            width = tensor.Dimensions[2];
            hidden = tensor.ToArray();
        }
        var embedding = new float[width];
        for (var t = 0; t < ids.Length; t++) for (var d = 0; d < width; d++) embedding[d] += hidden[t * width + d];
        var norm = 0.0;
        for (var d = 0; d < width; d++) { embedding[d] /= ids.Length; norm += embedding[d] * embedding[d]; }
        norm = Math.Sqrt(norm);
        if (norm > 0) for (var d = 0; d < width; d++) embedding[d] = (float)(embedding[d] / norm);
        return embedding;
    }

    internal float[] Probabilities(string text)
    {
        var embedding = Embed(text);
        var classes = _package.Classes.Count;
        var logits = new double[classes];
        for (var c = 0; c < classes; c++)
        {
            double sum = _package.Intercepts[c];
            for (var d = 0; d < embedding.Length; d++) sum += _package.Coefficients[c, d] * embedding[d];
            logits[c] = sum;
        }
        var max = logits.Max();
        var exp = logits.Select(l => Math.Exp(l - max)).ToArray();
        var total = exp.Sum();
        return exp.Select(e => (float)(e / total)).ToArray();
    }

    public void Dispose() => _session.Dispose();
}
