# Room Terrain Inference Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Color auto-mapper rooms by environment using the locally run ONNX room-classifier when the game supplies no terrain, then finish the handoff's outstanding verification work.

**Architecture:** A new `Wandur.Core.Classification` namespace reproduces the model package's preprocessing/tokenization/pooling/head contract over ONNX Runtime and owns package download/verification. `MapRoom` gains inferred-terrain fields that persist through the existing JSON/SQLite path; `MapEnvironmentPalette` resolves server/manual terrain first and inference second. `WorkspaceController` schedules background inference for rooms lacking terrain and applies results on the UI thread; Map tools gains a small section for install/enable.

**Tech Stack:** .NET 10, Avalonia 12.1.2, CommunityToolkit.Mvvm, Microsoft.ML.OnnxRuntime 1.30.0, Microsoft.ML.Tokenizers 2.0.0, xUnit (Core: xunit 2; Desktop: xunit.v3 + Avalonia.Headless).

**Spec:** `docs/superpowers/specs/2026-09-18-room-terrain-inference-design.md` (plus `CLAUDE_HANDOFF.md` "Remaining work" for Tasks 13–16).

## Global Constraints

- Repo root `/Volumes/Extreme SSD/workspace/Wundur`; **not a git repository** — there are no commit steps; every task ends with a build + the task's tests green. Use exact `.csproj` paths. AppleDouble `._*` files exist; ignore them.
- `TreatWarningsAsErrors=true`, `Nullable=enable`, `ImplicitUsings=enable` (Directory.Build.props). New code must compile warning-free.
- `RestorePackagesWithLockFile=true`: after adding PackageReferences run `dotnet restore Wandur.sln --force-evaluate` so every `packages.lock.json` updates.
- Contract fidelity (spec): `BuildText = Clean(name) + "\n" + Clean(description)`; strip patterns exactly `\x1b\[[0-9;]*m`, `&[a-zA-Z0-9]`, `@[a-zA-Z0-9]`, `\{[a-zA-Z]`, literal `~`; whitespace runs → single space, trimmed; WordPiece lowercase; `[CLS]`(101) + ≤254 ids + `[SEP]`(102); batch=1 no padding; mean-pool over mask; L2 normalize; `logits = emb·coefᵀ + intercept`; softmax; abstain below threshold (default 0.80).
- Precedence: `MapRoom.Environment` (server/manual) always wins over `InferredEnvironment`. Inference never sets `IsManuallyEdited`.
- Model classes `settlement`/`urban` display as palette key `city` (already handled by `MapEnvironmentPalette.Normalize`).
- Default package URL: `https://github.com/YouCantGoThatWay/room-classifier/releases/download/v0.1.1/wundur-room-classifier-0.1.1.zip`. Local package for integration tests: `/Volumes/Extreme SSD/workspace/experimental_model/export/release/wundur-room-classifier-0.1.1.zip` (extract to `/private/tmp/claude-501/-Volumes-Extreme-SSD-workspace-experimental-model/64326da7-b3b9-413a-bd27-15160f6734ea/scratchpad/room-model/` and set `WANDUR_ROOM_MODEL_DIR` to that directory for gated tests).
- Localization: new UI strings go in `src/Wandur.Core/Localization/Strings.resx` **and** `Strings.de.resx`, `Strings.es.resx`, `Strings.fr.resx`, `Strings.pt-BR.resx` (one-line `<data name="Key" xml:space="preserve"><value>…</value></data>` entries), then `python3 scripts/generate-localization.py` regenerates `Strings.cs`.
- Test commands: `dotnet test tests/Wandur.Core.Tests/Wandur.Core.Tests.csproj -c Debug --nologo -v q --filter "FullyQualifiedName~<Class>"`; Desktop likewise with `tests/Wandur.Desktop.Tests/Wandur.Desktop.Tests.csproj`. Full run: `dotnet test Wandur.sln -c Release --no-restore`.
- User preferences (handoff): plain language, no em dashes in user-facing text, no repeated approval questions, never restart the user's running MUD sessions, never print `.env`.

---

### Task 1: Packages, preprocessor, and fixture parity

**Files:**
- Modify: `src/Wandur.Core/Wandur.Core.csproj`
- Create: `src/Wandur.Core/Classification/RoomTextPreprocessor.cs`
- Create: `tests/Fixtures/room-classifier-parity.json` (copy of the package's `parity_fixtures.json`)
- Test: `tests/Wandur.Core.Tests/RoomTextPreprocessorTests.cs`

**Interfaces:**
- Produces: `static class RoomTextPreprocessor { static string Clean(string? text); static string BuildText(string name, string description); static string InferenceKey(string name, string description, string modelVersion); }` — `InferenceKey` = lowercase hex SHA-256 of UTF-8 `modelVersion + "\n" + BuildText(...)`.

- [ ] **Step 1: Add packages and copy the fixture**

Edit `src/Wandur.Core/Wandur.Core.csproj` to:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><ProjectReference Include="../Wandur.Models/Wandur.Models.csproj" /></ItemGroup>
  <ItemGroup><PackageReference Include="Jint" Version="4.16.2" /></ItemGroup>
  <ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.12" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.30.0" />
    <PackageReference Include="Microsoft.ML.Tokenizers" Version="2.0.0" />
  </ItemGroup>
</Project>
```
Run:
```bash
cd "/Volumes/Extreme SSD/workspace/Wundur" && dotnet restore Wandur.sln --force-evaluate 2>&1 | tail -2
unzip -p "/Volumes/Extreme SSD/workspace/experimental_model/export/release/wundur-room-classifier-0.1.1.zip" parity_fixtures.json > tests/Fixtures/room-classifier-parity.json
mkdir -p "/private/tmp/claude-501/-Volumes-Extreme-SSD-workspace-experimental-model/64326da7-b3b9-413a-bd27-15160f6734ea/scratchpad/room-model" && unzip -o -q "/Volumes/Extreme SSD/workspace/experimental_model/export/release/wundur-room-classifier-0.1.1.zip" -d "/private/tmp/claude-501/-Volumes-Extreme-SSD-workspace-experimental-model/64326da7-b3b9-413a-bd27-15160f6734ea/scratchpad/room-model"
```
Expected: restore succeeds; fixture file ~185 KB; model dir holds 9 files.

- [ ] **Step 2: Write the failing test**

`tests/Wandur.Core.Tests/RoomTextPreprocessorTests.cs`:
```csharp
using System.Text.Json;
using Wandur.Core.Classification;

namespace Wandur.Core.Tests;

public sealed class RoomTextPreprocessorTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "room-classifier-parity.json");

    [Fact]
    public void CleanStripsColorCodesTildesAndCollapsesWhitespace()
    {
        Assert.Equal("The Forest", RoomTextPreprocessor.Clean("&RThe &GForest&x"));
        Assert.Equal("Dark cave", RoomTextPreprocessor.Clean("@rDark@n cave"));
        Assert.Equal("Misty path", RoomTextPreprocessor.Clean("{cMisty{x path"));
        Assert.Equal("Red room", RoomTextPreprocessor.Clean("[31mRed[0m room"));
        Assert.Equal("A hall. Dust hangs in the air.", RoomTextPreprocessor.Clean("A hall.~\r\n   Dust    hangs\n\nin the air.  "));
        Assert.Equal("", RoomTextPreprocessor.Clean(null));
    }

    [Fact]
    public void BuildTextJoinsCleanedNameAndDescriptionWithNewline() =>
        Assert.Equal("Temple\nA hall.", RoomTextPreprocessor.BuildText("Temple", "A hall."));

    [Fact]
    public void EveryParityFixtureReproducesItsText()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var fixtures = document.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();
        Assert.Equal(23, fixtures.Length);
        foreach (var fixture in fixtures)
            Assert.Equal(fixture.GetProperty("text").GetString(),
                RoomTextPreprocessor.BuildText(fixture.GetProperty("name").GetString()!, fixture.GetProperty("description").GetString()!));
    }

    [Fact]
    public void InferenceKeyIsStableAndModelScoped()
    {
        var a = RoomTextPreprocessor.InferenceKey("Temple", "A hall.", "0.1.1");
        Assert.Equal(a, RoomTextPreprocessor.InferenceKey("&RTemple", "A  hall.~", "0.1.1"));
        Assert.NotEqual(a, RoomTextPreprocessor.InferenceKey("Temple", "A hall.", "0.2.0"));
        Assert.Equal(64, a.Length);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Wandur.Core.Tests/Wandur.Core.Tests.csproj -c Debug --nologo -v q --filter "FullyQualifiedName~RoomTextPreprocessorTests"`
Expected: build error, `RoomTextPreprocessor` does not exist.

- [ ] **Step 4: Write the implementation**

`src/Wandur.Core/Classification/RoomTextPreprocessor.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Wandur.Core.Classification;

/// <summary>Reproduces the room-classifier package's preprocessing_spec.json exactly. Any change breaks model parity.</summary>
public static partial class RoomTextPreprocessor
{
    [GeneratedRegex(@"\x1b\[[0-9;]*m")] private static partial Regex Ansi();
    [GeneratedRegex(@"&[a-zA-Z0-9]")] private static partial Regex Smaug();
    [GeneratedRegex(@"@[a-zA-Z0-9]")] private static partial Regex Tba();
    [GeneratedRegex(@"\{[a-zA-Z]")] private static partial Regex Rom();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = Ansi().Replace(text, "");
        value = Smaug().Replace(value, "");
        value = Tba().Replace(value, "");
        value = Rom().Replace(value, "");
        value = value.Replace("~", "");
        return Whitespace().Replace(value, " ").Trim();
    }

    public static string BuildText(string name, string description) => Clean(name) + "\n" + Clean(description);

    public static string InferenceKey(string name, string description, string modelVersion) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(modelVersion + "\n" + BuildText(name, description))));
}
```

- [ ] **Step 5: Run test to verify it passes**

Run the Step 3 command. Expected: 4 passed.

- [ ] **Step 6: Build check**

Run: `dotnet build src/Wandur.Core/Wandur.Core.csproj -c Debug --nologo -v q` — Expected: 0 warnings, 0 errors.

---

### Task 2: Map model fields, validation, and tracker inference methods

**Files:**
- Modify: `src/Wandur.Core/Mapping/MapModels.cs` (MapRoom record)
- Modify: `src/Wandur.Core/Mapping/MapFileFormat.cs:46-52` (ValidRoom)
- Create: `src/Wandur.Core/Classification/RoomEnvironmentClassifier.cs`
- Create: `src/Wandur.Core/Mapping/RoomMapTracker.Inference.cs`
- Test: `tests/Wandur.Core.Tests/RoomInferenceTests.cs`

**Interfaces:**
- Produces: `MapRoom.InferredEnvironment: string?`, `MapRoom.InferredConfidence: double?`, `MapRoom.InferredKey: string?` (init-only).
- Produces: `sealed record RoomEnvironmentPrediction(string Environment, double Confidence, string ModelVersion)`; `interface IRoomEnvironmentClassifier { string ModelVersion { get; } double DefaultThreshold { get; } RoomEnvironmentPrediction? Classify(string name, string description, double threshold); }`.
- Produces: `RoomMapTracker.ApplyInference(string id, string key, string modelVersion, RoomEnvironmentPrediction? prediction) : bool`; `RoomMapTracker.RoomsNeedingInference(string modelVersion) : IReadOnlyList<MapRoom>` (rooms with empty `Environment` whose `InferredKey != InferenceKey(name, description, modelVersion)`; current room first).

- [ ] **Step 1: Write the failing test**

`tests/Wandur.Core.Tests/RoomInferenceTests.cs`:
```csharp
using Wandur.Core.Classification;
using Wandur.Core.Mapping;

namespace Wandur.Core.Tests;

public sealed class RoomInferenceTests
{
    private static RoomObservation At(string id, string name = "Room", string description = "Tall pines crowd the trail.", string? environment = null) =>
        new(id, name, description, new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp) { Environment = environment };

    [Fact]
    public void RoomsNeedingInferenceSkipsServerTerrainAndFreshKeys()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(At("1"));
        tracker.Observe(At("2", environment: "forest"), "north");
        tracker.Observe(At("3"), "north");
        var needing = tracker.RoomsNeedingInference("0.1.1");
        Assert.Equal(["s:3", "s:1"], needing.Select(r => r.Id)); // current room first
        var key = RoomTextPreprocessor.InferenceKey("Room", "Tall pines crowd the trail.", "0.1.1");
        Assert.True(tracker.ApplyInference("s:3", key, "0.1.1", new("forest", 0.91, "0.1.1")));
        Assert.Equal(["s:1"], tracker.RoomsNeedingInference("0.1.1").Select(r => r.Id));
        Assert.Equal(2, tracker.RoomsNeedingInference("0.2.0").Count); // new model version invalidates
    }

    [Fact]
    public void ApplyInferenceSetsFieldsWithoutManualFlagAndRejectsStaleKeys()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(At("1"));
        var changed = 0; tracker.Changed += () => changed++;
        var key = RoomTextPreprocessor.InferenceKey("Room", "Tall pines crowd the trail.", "0.1.1");
        Assert.True(tracker.ApplyInference("s:1", key, "0.1.1", new("forest", 0.91, "0.1.1")));
        var room = tracker.Snapshot.Rooms.Single();
        Assert.Equal("forest", room.InferredEnvironment); Assert.Equal(0.91, room.InferredConfidence); Assert.Equal(key, room.InferredKey);
        Assert.False(room.IsManuallyEdited); Assert.Null(room.Environment); Assert.Equal(1, changed);
        Assert.False(tracker.ApplyInference("s:1", "stale", "0.1.1", new("cave", 0.95, "0.1.1")));
        Assert.False(tracker.ApplyInference("missing", key, "0.1.1", new("cave", 0.95, "0.1.1")));
        Assert.Equal("forest", tracker.Snapshot.Rooms.Single().InferredEnvironment);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void AbstentionRecordsKeyAndClearsEnvironment()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(At("1"));
        var key = RoomTextPreprocessor.InferenceKey("Room", "Tall pines crowd the trail.", "0.1.1");
        tracker.ApplyInference("s:1", key, "0.1.1", new("forest", 0.91, "0.1.1"));
        Assert.True(tracker.ApplyInference("s:1", key, "0.1.1", null));
        var room = tracker.Snapshot.Rooms.Single();
        Assert.Null(room.InferredEnvironment); Assert.Null(room.InferredConfidence); Assert.Equal(key, room.InferredKey);
        Assert.Empty(tracker.RoomsNeedingInference("0.1.1"));
    }

    [Fact]
    public void ReobservationWithNewTextKeepsHintButNeedsInferenceAgain()
    {
        var tracker = new RoomMapTracker();
        tracker.Observe(At("1"));
        var key = RoomTextPreprocessor.InferenceKey("Room", "Tall pines crowd the trail.", "0.1.1");
        tracker.ApplyInference("s:1", key, "0.1.1", new("forest", 0.91, "0.1.1"));
        tracker.Observe(At("1", description: "Endless dunes roll away."));
        var room = tracker.Snapshot.Rooms.Single();
        Assert.Equal("forest", room.InferredEnvironment);
        Assert.Equal(["s:1"], tracker.RoomsNeedingInference("0.1.1").Select(r => r.Id));
    }

    [Fact]
    public void InferredFieldsSurviveSerializationAndAreValidated()
    {
        var room = new MapRoom("r", "Room", "Desc", null, 0, 0, 0, false) { InferredEnvironment = "cave", InferredConfidence = 0.5, InferredKey = "k" };
        var snapshot = new MapSnapshot([room], [], [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0);
        var back = MapFileFormat.Deserialize(MapFileFormat.Serialize(snapshot));
        Assert.Equal(room, back.Rooms.Single());
        Assert.Throws<FormatException>(() => MapFileFormat.Serialize(snapshot with { Rooms = [room with { InferredConfidence = 1.5 }] }));
        Assert.Throws<FormatException>(() => MapFileFormat.Serialize(snapshot with { Rooms = [room with { InferredKey = new string('k', 129) }] }));
        Assert.Throws<FormatException>(() => MapFileFormat.Serialize(snapshot with { Rooms = [room with { InferredEnvironment = new string('e', 129) }] }));
    }
}
```
(If `MapFileFormat.Serialize` throws a different exception type for invalid maps, read `MapFileFormat.Validate` and match it — do not weaken the assertion.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Wandur.Core.Tests/Wandur.Core.Tests.csproj -c Debug --nologo -v q --filter "FullyQualifiedName~RoomInferenceTests"`
Expected: build errors (missing members).

- [ ] **Step 3: Add the record/interface, room fields, validation, and tracker partial**

`src/Wandur.Core/Classification/RoomEnvironmentClassifier.cs`:
```csharp
namespace Wandur.Core.Classification;

/// <summary>A presentation hint only: never identity, exits or routing.</summary>
public sealed record RoomEnvironmentPrediction(string Environment, double Confidence, string ModelVersion);

public interface IRoomEnvironmentClassifier
{
    string ModelVersion { get; }
    double DefaultThreshold { get; }
    /// <summary>Returns null when the best class scores below <paramref name="threshold"/>. CPU-bound; call off the UI thread.</summary>
    RoomEnvironmentPrediction? Classify(string name, string description, double threshold);
}
```

In `src/Wandur.Core/Mapping/MapModels.cs`, add to `MapRoom` after `public string? Symbol { get; init; }`:
```csharp
    // Classifier hint; presentation only. Environment (server/manual) always wins.
    public string? InferredEnvironment { get; init; }
    public double? InferredConfidence { get; init; }
    public string? InferredKey { get; init; }
```

In `src/Wandur.Core/Mapping/MapFileFormat.cs` `ValidRoom`, append before the final `r.KnownExits` clause:
```csharp
        Text(r.InferredEnvironment, 128, optional: true) && Text(r.InferredKey, 128, optional: true) &&
        (r.InferredConfidence is null || (double.IsFinite(r.InferredConfidence.Value) && r.InferredConfidence is >= 0 and <= 1)) &&
```

`src/Wandur.Core/Mapping/RoomMapTracker.Inference.cs`:
```csharp
using Wandur.Core.Classification;

namespace Wandur.Core.Mapping;

public sealed partial class RoomMapTracker
{
    /// <summary>Rooms without server/manual terrain whose inference is missing or stale; current room first.</summary>
    public IReadOnlyList<MapRoom> RoomsNeedingInference(string modelVersion) =>
        _rooms.Values
            .Where(r => string.IsNullOrWhiteSpace(r.Environment) &&
                        r.InferredKey != RoomTextPreprocessor.InferenceKey(r.Name, r.Description, modelVersion))
            .OrderByDescending(r => r.Id == _current)
            .ToArray();

    /// <summary>Applies a classifier result; ignored when the room is gone or <paramref name="key"/> no longer matches its text.</summary>
    public bool ApplyInference(string id, string key, string modelVersion, RoomEnvironmentPrediction? prediction)
    {
        if (!_rooms.TryGetValue(id, out var room)) return false;
        if (key != RoomTextPreprocessor.InferenceKey(room.Name, room.Description, modelVersion)) return false;
        _rooms[id] = room with
        {
            InferredEnvironment = prediction?.Environment,
            InferredConfidence = prediction?.Confidence,
            InferredKey = key,
            Revision = NextRevision()
        };
        Changed?.Invoke();
        return true;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run the Step 2 command. Expected: 5 passed. Also run `--filter "FullyQualifiedName~RoomMapTests|FullyQualifiedName~SqliteMapStoreTests|FullyQualifiedName~ExpandedMapTests"` — Expected: all pass (no regressions from the record change).

- [ ] **Step 5: Build check**

`dotnet build Wandur.sln -c Debug --nologo -v q` — Expected: 0 warnings, 0 errors.

---

### Task 3: Model package loading and manifest verification

**Files:**
- Create: `src/Wandur.Core/Classification/ModelPackage.cs`
- Test: `tests/Wandur.Core.Tests/ModelPackageTests.cs`

**Interfaces:**
- Produces: `sealed class ModelPackage { string Directory; string Version; string TaxonomyVersion; double Threshold; int MaxWordPieces; IReadOnlyList<string> Classes; float[,] Coefficients /*[classes,384]*/; float[] Intercepts; string EncoderPath; string TokenizerPath; static ModelPackage Load(string directory); Stream OpenVocabulary(); }`
- `Load` throws `InvalidDataException` when: manifest missing, any listed file missing or sha256 mismatch, required files absent (`encoder.onnx`, `tokenizer.json`, `head.json`, `preprocessing_spec.json`, `taxonomy.json`), taxonomy version ≠ "1.0.0", head/classes inconsistent.
- `OpenVocabulary()` returns a UTF-8 stream with one token per line ordered by id from `tokenizer.json` → `model.vocab`.

- [ ] **Step 1: Write the failing test**

`tests/Wandur.Core.Tests/ModelPackageTests.cs`:
```csharp
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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Wandur.Core.Tests/Wandur.Core.Tests.csproj -c Debug --nologo -v q --filter "FullyQualifiedName~ModelPackageTests"` — Expected: build error.

- [ ] **Step 3: Write the implementation**

`src/Wandur.Core/Classification/ModelPackage.cs`:
```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run the Step 2 command. Expected: 2 passed.

- [ ] **Step 5: Build check** — `dotnet build src/Wandur.Core/Wandur.Core.csproj -c Debug --nologo -v q` → 0 warnings.

---

### Task 4: ONNX classifier with gated fixture parity

**Files:**
- Create: `src/Wandur.Core/Classification/OnnxRoomEnvironmentClassifier.cs`
- Test: `tests/Wandur.Core.Tests/OnnxRoomEnvironmentClassifierTests.cs`

**Interfaces:**
- Consumes: `ModelPackage`, `RoomTextPreprocessor`, `IRoomEnvironmentClassifier`.
- Produces: `sealed class OnnxRoomEnvironmentClassifier : IRoomEnvironmentClassifier, IDisposable { OnnxRoomEnvironmentClassifier(ModelPackage package); int[] Tokenize(string text) /*internal for tests: full id list incl. CLS/SEP*/; float[] Embed(string text) /*internal*/; float[] Probabilities(string text) /*internal*/; }`.

- [ ] **Step 1: Write the failing (gated) test**

`tests/Wandur.Core.Tests/OnnxRoomEnvironmentClassifierTests.cs`:
```csharp
using System.Text.Json;
using Wandur.Core.Classification;

namespace Wandur.Core.Tests;

/// <summary>Runs only when WANDUR_ROOM_MODEL_DIR points at an extracted model package.</summary>
public sealed class OnnxRoomEnvironmentClassifierTests
{
    private static string? ModelDirectory => Environment.GetEnvironmentVariable("WANDUR_ROOM_MODEL_DIR") is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;

    [Fact]
    public void ReproducesEveryParityFixture()
    {
        if (ModelDirectory is not { } dir) return; // gated
        using var classifier = new OnnxRoomEnvironmentClassifier(ModelPackage.Load(dir));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "parity_fixtures.json")));
        var atol = document.RootElement.GetProperty("atol_probs").GetDouble();
        foreach (var fixture in document.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            var text = fixture.GetProperty("text").GetString()!;
            var expectedIds = fixture.GetProperty("token_ids").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Assert.Equal(expectedIds, classifier.Tokenize(text));
            var expectedProbs = fixture.GetProperty("probs").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var probs = classifier.Probabilities(text);
            for (var i = 0; i < expectedProbs.Length; i++) Assert.InRange(probs[i], expectedProbs[i] - atol, expectedProbs[i] + atol);
            var prediction = classifier.Classify(fixture.GetProperty("name").GetString()!, fixture.GetProperty("description").GetString()!, threshold: 0);
            Assert.Equal(fixture.GetProperty("predicted").GetString(), prediction!.Environment);
        }
    }

    [Fact]
    public void AbstainsBelowThreshold()
    {
        if (ModelDirectory is not { } dir) return;
        using var classifier = new OnnxRoomEnvironmentClassifier(ModelPackage.Load(dir));
        Assert.Null(classifier.Classify("Void", "Nothing.", threshold: 1.0));
        Assert.NotNull(classifier.Classify("Void", "Nothing.", threshold: 0.0));
        Assert.Equal("0.1.1", classifier.ModelVersion);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `WANDUR_ROOM_MODEL_DIR="/private/tmp/claude-501/-Volumes-Extreme-SSD-workspace-experimental-model/64326da7-b3b9-413a-bd27-15160f6734ea/scratchpad/room-model" dotnet test tests/Wandur.Core.Tests/Wandur.Core.Tests.csproj -c Debug --nologo -v q --filter "FullyQualifiedName~OnnxRoomEnvironmentClassifierTests"` — Expected: build error.

- [ ] **Step 3: Write the implementation**

`src/Wandur.Core/Classification/OnnxRoomEnvironmentClassifier.cs`:
```csharp
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

    /// <summary>[CLS] + at most (MaxWordPieces - 2) word pieces + [SEP], matching Hugging Face truncation.</summary>
    internal int[] Tokenize(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, addSpecialTokens: false, considerPreTokenization: true, considerNormalization: true);
        var body = ids.Take(_package.MaxWordPieces - 2);
        return [_tokenizer.ClassificationTokenId, .. body, _tokenizer.SeparatorTokenId];
    }

    internal float[] Embed(string text)
    {
        var ids = Tokenize(text);
        var inputIds = new DenseTensor<long>(ids.Select(i => (long)i).ToArray(), [1, ids.Length]);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), [1, ids.Length]);
        float[] hidden; int width;
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
```
Adaptation note: if `Microsoft.ML.Tokenizers 2.0.0` names the `EncodeToIds` parameters differently, use the overload that encodes **without** special tokens (check `BertTokenizer` members with `dotnet` IntelliSense or the package XML docs in `~/.nuget/packages/microsoft.ml.tokenizers/2.0.0/lib/net8.0/`); the fixture test is the oracle. Do not change the `[CLS] + ≤254 + [SEP]` semantics.

- [ ] **Step 4: Run the gated test with the real model**

Run the Step 2 command (with the env var). Expected: 2 passed, every fixture's ids and probabilities match. If `token_ids` differ, inspect the first mismatching fixture (`crafted:codes:0` exercises punctuation/case; `crafted:long:0` exercises truncation to exactly 256) and fix tokenizer options — never loosen the assertions.

- [ ] **Step 5: Run without the env var** — Expected: 2 passed (gated no-ops). Build check on Core: 0 warnings.

---

### Task 5: Package installer (download, verify, extract)

**Files:**
- Create: `src/Wandur.Core/Classification/ModelPackageInstaller.cs`
- Test: `tests/Wandur.Core.Tests/ModelPackageInstallerTests.cs`

**Interfaces:**
- Produces: `sealed class ModelPackageInstaller { ModelPackageInstaller(string modelsRoot, HttpClient http); string? InstalledDirectory /*best verified version*/; Task<ModelPackage> InstallAsync(Stream zip, CancellationToken); Task<ModelPackage> DownloadAsync(Uri url, IProgress<double>? progress, CancellationToken); const long MaxPackageBytes = 200L * 1024 * 1024; }`
- Layout: `<modelsRoot>/<version>/…` with the package files; extraction to `<modelsRoot>/.staging-<guid>` then atomic `Directory.Move`.

- [ ] **Step 1: Write the failing test**

`tests/Wandur.Core.Tests/ModelPackageInstallerTests.cs`:
```csharp
using System.IO.Compression;
using System.Net;
using Wandur.Core.Classification;

namespace Wandur.Core.Tests;

public sealed class ModelPackageInstallerTests
{
    private static MemoryStream Zip(string packageDirectory, Action<ZipArchive>? extra = null)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(packageDirectory)) zip.CreateEntryFromFile(file, Path.GetFileName(file));
            extra?.Invoke(zip);
        }
        stream.Position = 0; return stream;
    }

    [Fact]
    public async Task InstallsVerifiedZipIntoVersionDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "wandur-models-" + Guid.NewGuid());
        var installer = new ModelPackageInstaller(root, new HttpClient());
        Assert.Null(installer.InstalledDirectory);
        var package = await installer.InstallAsync(Zip(ModelPackageTests.CreateFakePackage()), CancellationToken.None);
        Assert.Equal("9.9.9", package.Version);
        Assert.Equal(Path.Combine(root, "9.9.9"), installer.InstalledDirectory);
        Assert.Empty(Directory.GetDirectories(root, ".staging-*"));
    }

    [Fact]
    public async Task RejectsZipSlipAndTamperedContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "wandur-models-" + Guid.NewGuid());
        var installer = new ModelPackageInstaller(root, new HttpClient());
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(Zip(ModelPackageTests.CreateFakePackage(), zip =>
        { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("x"); }), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(Zip(ModelPackageTests.CreateFakePackage(tamper: "head.json")), CancellationToken.None));
        Assert.Null(installer.InstalledDirectory);
        Assert.Empty(Directory.Exists(root) ? Directory.GetDirectories(root, ".staging-*") : []);
    }

    [Fact]
    public async Task DownloadsThroughHttpWithProgress()
    {
        var root = Path.Combine(Path.GetTempPath(), "wandur-models-" + Guid.NewGuid());
        var bytes = Zip(ModelPackageTests.CreateFakePackage()).ToArray();
        using var listener = new HttpListener(); var port = 0;
        for (var attempt = 40000; attempt < 40100; attempt++) { try { listener.Prefixes.Clear(); listener.Prefixes.Add($"http://127.0.0.1:{attempt}/"); listener.Start(); port = attempt; break; } catch (HttpListenerException) { } }
        Assert.NotEqual(0, port);
        _ = Task.Run(async () => { var context = await listener.GetContextAsync(); context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); });
        var progress = new List<double>();
        var installer = new ModelPackageInstaller(root, new HttpClient());
        var package = await installer.DownloadAsync(new Uri($"http://127.0.0.1:{port}/model.zip"), new Progress<double>(progress.Add), CancellationToken.None);
        Assert.Equal("9.9.9", package.Version);
        await Task.Delay(50);
        Assert.Contains(progress, p => p >= 0.99);
        listener.Stop();
    }
}
```
(Desktop test runs note loopback sockets sometimes need escalated execution; Core tests already use loopback in `SessionTests`, so this is consistent.)

- [ ] **Step 2: Run test to verify it fails** — filter `ModelPackageInstallerTests`; Expected: build error.

- [ ] **Step 3: Write the implementation**

`src/Wandur.Core/Classification/ModelPackageInstaller.cs`:
```csharp
using System.IO.Compression;

namespace Wandur.Core.Classification;

/// <summary>Downloads/extracts a model package into &lt;modelsRoot&gt;/&lt;version&gt;/ after manifest verification.</summary>
public sealed class ModelPackageInstaller(string modelsRoot, HttpClient http)
{
    public const long MaxPackageBytes = 200L * 1024 * 1024;
    private const int MaxEntries = 64;

    public string ModelsRoot { get; } = modelsRoot;

    /// <summary>The highest version directory that loads and verifies, or null.</summary>
    public string? InstalledDirectory
    {
        get
        {
            if (!Directory.Exists(ModelsRoot)) return null;
            foreach (var dir in Directory.GetDirectories(ModelsRoot).Where(d => !Path.GetFileName(d).StartsWith('.'))
                         .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0)))
            {
                try { ModelPackage.Load(dir); return dir; }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
            }
            return null;
        }
    }

    public Task<ModelPackage> DownloadAsync(Uri url, IProgress<double>? progress, CancellationToken cancellation) =>
        DownloadAsync(url, progress, cancellation, depth: 0);

    private async Task<ModelPackage> DownloadAsync(Uri url, IProgress<double>? progress, CancellationToken cancellation, int depth)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect
            && response.Headers.Location is { } location)
        {
            if (depth >= 5) throw new HttpRequestException("Too many redirects.");
            return await DownloadAsync(location.IsAbsoluteUri ? location : new Uri(url, location), progress, cancellation, depth + 1); // the shared HttpClient disables auto-redirect
        }
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        if (total > MaxPackageBytes) throw new InvalidDataException("Package exceeds the size limit.");
        Directory.CreateDirectory(ModelsRoot);
        var temp = Path.Combine(ModelsRoot, ".download-" + Guid.NewGuid() + ".zip");
        try
        {
            await using (var file = File.Create(temp))
            await using (var body = await response.Content.ReadAsStreamAsync(cancellation))
            {
                var buffer = new byte[81920]; long read = 0; int count;
                while ((count = await body.ReadAsync(buffer, cancellation)) > 0)
                {
                    read += count;
                    if (read > MaxPackageBytes) throw new InvalidDataException("Package exceeds the size limit.");
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    if (total is > 0) progress?.Report(Math.Min(1, (double)read / total.Value));
                }
                progress?.Report(1);
            }
            await using var zip = File.OpenRead(temp);
            return await InstallAsync(zip, cancellation);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    public async Task<ModelPackage> InstallAsync(Stream zip, CancellationToken cancellation)
    {
        Directory.CreateDirectory(ModelsRoot);
        var staging = Path.Combine(ModelsRoot, ".staging-" + Guid.NewGuid());
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("Too many package entries.");
            long extracted = 0;
            foreach (var entry in archive.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry.FullName);
                if (string.IsNullOrEmpty(name) || entry.FullName.Contains("..") || entry.FullName.Contains('/') || entry.FullName.Contains('\\') || name.StartsWith('.'))
                    throw new InvalidDataException($"Unsafe package entry {entry.FullName}.");
                await using var source = entry.Open();
                await using var target = File.Create(Path.Combine(staging, name));
                var chunk = new byte[81920]; int got; // cap on ACTUAL decompressed bytes, never the header's declared Length
                while ((got = await source.ReadAsync(chunk, cancellation)) > 0)
                {
                    extracted += got;
                    if (extracted > MaxPackageBytes) throw new InvalidDataException("Package exceeds the size limit.");
                    await target.WriteAsync(chunk.AsMemory(0, got), cancellation);
                }
            }
            var package = ModelPackage.Load(staging); // verifies hashes and structure
            var destination = Path.Combine(ModelsRoot, package.Version);
            var previous = Directory.Exists(destination) ? Path.Combine(ModelsRoot, ".previous-" + Guid.NewGuid()) : null;
            if (previous is not null) Directory.Move(destination, previous); // move aside, never delete-then-move
            Directory.Move(staging, destination);
            if (previous is not null) { try { Directory.Delete(previous, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            return ModelPackage.Load(destination);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        { throw new InvalidDataException("Invalid package contents.", ex); }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
}
```

- [ ] **Step 4: Run test to verify it passes** — filter `ModelPackageInstallerTests`; Expected: 5 passed (the brief's three plus `FollowsRedirectsUpToTheCap` and a zip-bomb-shaped cap test added during review).

- [ ] **Step 5: Build check** — Core builds with 0 warnings.

---

### Task 6: RoomClassificationService (status, lifecycle) and settings

**Files:**
- Create: `src/Wandur.Core/Classification/RoomClassificationService.cs`
- Modify: `src/Wandur.Core/Settings/ClientSettings.cs` (ClientSettings record + Validate)
- Test: `tests/Wandur.Core.Tests/RoomClassificationServiceTests.cs`

**Interfaces:**
- Produces: `enum RoomClassificationState { NotInstalled, Downloading, Ready, Failed }`; `sealed record RoomClassificationStatus(RoomClassificationState State, double Progress = 0, string? Version = null, string? Message = null)`;
  `sealed class RoomClassificationService : IDisposable { RoomClassificationService(string dataDirectory, HttpClient http, Uri? packageUrl = null, Func<ModelPackage, IRoomEnvironmentClassifier>? classifierFactory = null); static Uri DefaultPackageUrl; RoomClassificationStatus Status; event Action? Changed; IRoomEnvironmentClassifier? TryGetClassifier(); Task DownloadAsync(CancellationToken); Task InstallFromFileAsync(string zipPath, CancellationToken); static RoomClassificationService ForTesting(IRoomEnvironmentClassifier classifier); }`
- Produces on `ClientSettings`: `bool ClassifyRoomsLocally { get; init; } = true;` `double RoomClassificationThreshold { get; init; } = 0.8;` validated finite in [0.5, 0.99] with `ArgumentException(L.RoomClassificationThresholdRange)`.
- `Changed` fires on the calling thread of the state change (callers marshal to UI as needed).

- [ ] **Step 1: Write the failing test**

`tests/Wandur.Core.Tests/RoomClassificationServiceTests.cs`:
```csharp
using System.IO.Compression;
using Wandur.Core.Classification;
using Wandur.Core.Settings;

namespace Wandur.Core.Tests;

public sealed class RoomClassificationServiceTests
{
    private sealed class FakeClassifier(string version) : IRoomEnvironmentClassifier
    {
        public string ModelVersion => version; public double DefaultThreshold => 0.8;
        public RoomEnvironmentPrediction? Classify(string name, string description, double threshold) => new("forest", 0.9, version);
    }

    [Fact]
    public async Task InstallFromFileMovesServiceToReadyAndLoadsClassifierLazily()
    {
        var data = Path.Combine(Path.GetTempPath(), "wandur-data-" + Guid.NewGuid());
        var zipPath = Path.Combine(Path.GetTempPath(), "wandur-pkg-" + Guid.NewGuid() + ".zip");
        ZipFile.CreateFromDirectory(ModelPackageTests.CreateFakePackage(), zipPath);
        var loaded = 0;
        using var service = new RoomClassificationService(data, new HttpClient(), classifierFactory: p => { loaded++; return new FakeClassifier(p.Version); });
        Assert.Equal(RoomClassificationState.NotInstalled, service.Status.State);
        Assert.Null(service.TryGetClassifier());
        var changes = 0; service.Changed += () => changes++;
        await service.InstallFromFileAsync(zipPath, CancellationToken.None);
        Assert.Equal(RoomClassificationState.Ready, service.Status.State); Assert.Equal("9.9.9", service.Status.Version);
        Assert.Equal(0, loaded);
        Assert.Equal("9.9.9", service.TryGetClassifier()!.ModelVersion); Assert.Same(service.TryGetClassifier(), service.TryGetClassifier());
        Assert.Equal(1, loaded); Assert.True(changes >= 1);
        Assert.Equal(Path.Combine(data, "models", "room-classifier", "9.9.9"), new ModelPackageInstaller(Path.Combine(data, "models", "room-classifier"), new HttpClient()).InstalledDirectory);
    }

    [Fact]
    public async Task FailedInstallReportsFailureAndKeepsNotInstalled()
    {
        var data = Path.Combine(Path.GetTempPath(), "wandur-data-" + Guid.NewGuid());
        using var service = new RoomClassificationService(data, new HttpClient(), classifierFactory: p => new FakeClassifier(p.Version));
        var bad = Path.Combine(Path.GetTempPath(), "wandur-bad-" + Guid.NewGuid() + ".zip");
        File.WriteAllText(bad, "not a zip");
        await service.InstallFromFileAsync(bad, CancellationToken.None);
        Assert.Equal(RoomClassificationState.Failed, service.Status.State); Assert.NotNull(service.Status.Message);
        Assert.Null(service.TryGetClassifier());
    }

    [Fact]
    public void ForTestingIsReadyImmediately()
    {
        using var service = RoomClassificationService.ForTesting(new FakeClassifier("t"));
        Assert.Equal(RoomClassificationState.Ready, service.Status.State);
        Assert.Equal("t", service.TryGetClassifier()!.ModelVersion);
    }

    [Fact]
    public void SettingsValidateThreshold()
    {
        new ClientSettings().Validate();
        Assert.True(new ClientSettings().ClassifyRoomsLocally); Assert.Equal(0.8, new ClientSettings().RoomClassificationThreshold);
        Assert.Throws<ArgumentException>(() => (new ClientSettings() with { RoomClassificationThreshold = 0.2 }).Validate());
        Assert.Throws<ArgumentException>(() => (new ClientSettings() with { RoomClassificationThreshold = double.NaN }).Validate());
        (new ClientSettings() with { RoomClassificationThreshold = 0.99 }).Validate();
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `RoomClassificationServiceTests`; Expected: build error.

- [ ] **Step 3: Implement service and settings**

`src/Wandur.Core/Classification/RoomClassificationService.cs`:
```csharp
namespace Wandur.Core.Classification;

public enum RoomClassificationState { NotInstalled, Downloading, Ready, Failed }
public sealed record RoomClassificationStatus(RoomClassificationState State, double Progress = 0, string? Version = null, string? Message = null);

/// <summary>Owns the installed room-classifier package and its lazily created classifier.</summary>
public sealed class RoomClassificationService : IDisposable
{
    public static readonly Uri DefaultPackageUrl = new("https://github.com/YouCantGoThatWay/room-classifier/releases/download/v0.1.1/wundur-room-classifier-0.1.1.zip");
    private readonly ModelPackageInstaller? _installer;
    private readonly Uri _packageUrl;
    private readonly Func<ModelPackage, IRoomEnvironmentClassifier> _factory;
    private readonly object _gate = new();
    private IRoomEnvironmentClassifier? _classifier;
    private bool _busy;

    public RoomClassificationService(string dataDirectory, HttpClient http, Uri? packageUrl = null, Func<ModelPackage, IRoomEnvironmentClassifier>? classifierFactory = null)
    {
        _installer = new ModelPackageInstaller(Path.Combine(dataDirectory, "models", "room-classifier"), http);
        _packageUrl = packageUrl ?? DefaultPackageUrl;
        _factory = classifierFactory ?? (package => new OnnxRoomEnvironmentClassifier(package));
        Status = InstalledStatus();
    }

    private RoomClassificationService(IRoomEnvironmentClassifier classifier)
    {
        _packageUrl = DefaultPackageUrl; _factory = _ => classifier; _classifier = classifier;
        Status = new(RoomClassificationState.Ready, 1, classifier.ModelVersion);
    }

    public static RoomClassificationService ForTesting(IRoomEnvironmentClassifier classifier) => new(classifier);

    public RoomClassificationStatus Status { get; private set; }
    public event Action? Changed;

    private RoomClassificationStatus InstalledStatus()
    {
        if (_installer?.InstalledDirectory is not { } dir) return new(RoomClassificationState.NotInstalled);
        try { return new(RoomClassificationState.Ready, 1, ModelPackage.Load(dir).Version); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return new(RoomClassificationState.Failed, 0, null, ex.Message); }
    }

    private void Set(RoomClassificationStatus status) { Status = status; Changed?.Invoke(); }

    /// <summary>Creates the classifier on first use; returns null unless the package is installed and loads.</summary>
    public IRoomEnvironmentClassifier? TryGetClassifier()
    {
        lock (_gate)
        {
            if (_classifier is not null) return _classifier;
            if (Status.State != RoomClassificationState.Ready || _installer?.InstalledDirectory is not { } dir) return null;
            try { return _classifier = _factory(ModelPackage.Load(dir)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            { Set(new(RoomClassificationState.Failed, 0, null, ex.Message)); return null; }
        }
    }

    public Task DownloadAsync(CancellationToken cancellation) =>
        RunInstall(progress => _installer!.DownloadAsync(_packageUrl, progress, cancellation));

    public Task InstallFromFileAsync(string zipPath, CancellationToken cancellation) =>
        RunInstall(async _ => { await using var stream = File.OpenRead(zipPath); return await _installer!.InstallAsync(stream, cancellation); });

    private async Task RunInstall(Func<IProgress<double>, Task<ModelPackage>> install)
    {
        if (_installer is null) return;
        lock (_gate) { if (_busy) return; _busy = true; }
        Set(new(RoomClassificationState.Downloading));
        try
        {
            var package = await install(new Progress<double>(p => Set(new(RoomClassificationState.Downloading, p))));
            lock (_gate) { (_classifier as IDisposable)?.Dispose(); _classifier = null; }
            Set(new(RoomClassificationState.Ready, 1, package.Version));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        { Set(new(RoomClassificationState.Failed, 0, null, ex.Message)); }
        finally { lock (_gate) _busy = false; }
    }

    public void Dispose() { lock (_gate) { (_classifier as IDisposable)?.Dispose(); _classifier = null; } }
}
```

In `src/Wandur.Core/Settings/ClientSettings.cs`, add to the `ClientSettings` record after `UseWorldThemes`:
```csharp
    public bool ClassifyRoomsLocally { get; init; } = true;
    public double RoomClassificationThreshold { get; init; } = 0.8;
```
and in `Validate()` after the font-size check:
```csharp
        if (!double.IsFinite(RoomClassificationThreshold) || RoomClassificationThreshold is < 0.5 or > 0.99) throw new ArgumentException(L.RoomClassificationThresholdRange);
```
Add the string (all five resx files; run the generator):
- `Strings.resx`: `RoomClassificationThresholdRange` → `Room classification confidence must be between 0.5 and 0.99.`
- de: `Die Konfidenz der Raumklassifizierung muss zwischen 0,5 und 0,99 liegen.`
- es: `La confianza de la clasificación de salas debe estar entre 0,5 y 0,99.`
- fr: `La confiance de la classification des salles doit être comprise entre 0,5 et 0,99.`
- pt-BR: `A confiança da classificação de salas deve ficar entre 0,5 e 0,99.`
Then: `python3 scripts/generate-localization.py`.

- [ ] **Step 4: Run tests** — filter `RoomClassificationServiceTests`; Expected: 4 passed. Also run `--filter "FullyQualifiedName~Settings"` for existing settings tests.

- [ ] **Step 5: Build + localization check** — `dotnet build Wandur.sln -c Debug --nologo -v q` (0 warnings) and `python3 scripts/generate-localization.py --check`.

---

### Task 7: Palette precedence, tooltip, editor provenance

**Files:**
- Modify: `src/Wandur.Desktop/Views/MapEnvironmentPalette.cs` (Resolve; add Describe)
- Modify: `src/Wandur.Desktop/Views/RoomMapControl.cs:297` (tooltip)
- Modify: `src/Wandur.Desktop/ViewModels/MapEditors.cs` (room editor: `TerrainProvenance` property, set in `Load`)
- Modify: `src/Wandur.Desktop/Views/MapEditorView.cs` (muted label under the terrain field)
- Modify: localization resx ×5 + regenerate
- Test: `tests/Wandur.Desktop.Tests/RoomInferenceRenderingTests.cs`

**Interfaces:**
- Produces: `MapEnvironmentPalette.Resolve(MapRoom room, palette?)` now falls back to `room.InferredEnvironment`; `static string Describe(MapRoom room)` → terrain label, or `L.Format(L.MapTerrainInferred, label, percent)` when displayed terrain is inferred; `static bool UsesInference(MapRoom room)`.
- Strings: `MapTerrainInferred` = `{0} · inferred {1}%` (de `{0} · abgeleitet {1}%`, es `{0} · inferido {1}%`, fr `{0} · déduit {1}%`, pt-BR `{0} · inferido {1}%`).

- [ ] **Step 1: Write the failing test**

`tests/Wandur.Desktop.Tests/RoomInferenceRenderingTests.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Mapping;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class RoomInferenceRenderingTests
{
    private static MapRoom Room(string id, double x, string? environment, string? inferred) =>
        new(id, id, "", null, x, 0, 0, false) { Environment = environment, InferredEnvironment = inferred, InferredConfidence = inferred is null ? null : 0.87 };

    [Fact]
    public void ResolvePrefersServerTerrainAndFallsBackToInference()
    {
        Assert.Equal("cave", MapEnvironmentPalette.Resolve(Room("a", 0, "cave", "forest")).Key);
        Assert.Equal("forest", MapEnvironmentPalette.Resolve(Room("b", 0, null, "forest")).Key);
        Assert.Equal("city", MapEnvironmentPalette.Resolve(Room("c", 0, null, "urban")).Key);
        Assert.Equal("unknown", MapEnvironmentPalette.Resolve(Room("d", 0, null, null)).Key);
        Assert.Equal("#123456", MapEnvironmentPalette.Resolve(Room("e", 0, null, "forest") with { Color = "#123456" }).Color);
    }

    [Fact]
    public void DescribeMarksInferredTerrainOnly()
    {
        Assert.DoesNotContain("inferred", MapEnvironmentPalette.Describe(Room("a", 0, "cave", "forest")));
        Assert.Contains("87%", MapEnvironmentPalette.Describe(Room("b", 0, null, "forest")));
        Assert.True(MapEnvironmentPalette.UsesInference(Room("b", 0, null, "forest")));
        Assert.False(MapEnvironmentPalette.UsesInference(Room("a", 0, "cave", "forest")));
    }

    [AvaloniaFact]
    public void InferredRoomPaintsPaletteColorWhileServerTerrainWins()
    {
        var tracker = new RoomMapTracker(new MapSnapshot([Room("inferred", -2, null, "forest"), Room("server", 2, "water", "forest")], [], [], null, MapTrackingState.Unknown, RoomDataSource.Gmcp, 0));
        var model = new MapViewModel(tracker); model.Attach(); model.IsGridMode = true;
        var canvas = new RoomMapControl { Model = model };
        var window = new Window { Content = canvas, Width = 500, Height = 400 };
        try
        {
            window.Show(); model.Zoom = 1; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            using var stream = new MemoryStream(); frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using var pixels = SkiaSharp.SKBitmap.Decode(stream.ToArray());
            var viewport = model.CreateViewport(canvas.Bounds.Width, canvas.Bounds.Height);
            var forest = viewport.Project(-2, 0); var water = viewport.Project(2, 0);
            var forestPixel = pixels.GetPixel((int)forest.X, (int)forest.Y); var waterPixel = pixels.GetPixel((int)water.X, (int)water.Y);
            Assert.True(forestPixel.Green > forestPixel.Red && forestPixel.Green > forestPixel.Blue, $"forest pixel {forestPixel}");
            Assert.True(waterPixel.Blue > waterPixel.Red && waterPixel.Blue > waterPixel.Green, $"water pixel {waterPixel}");
        }
        finally { window.Close(); model.Detach(); }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Wandur.Desktop.Tests/Wandur.Desktop.Tests.csproj -c Debug --nologo -v q --filter "FullyQualifiedName~RoomInferenceRenderingTests"`; Expected: build error (`Describe`/`UsesInference` missing).

- [ ] **Step 3: Implement**

In `MapEnvironmentPalette.cs` replace `Resolve` and add helpers:
```csharp
    public static bool UsesInference(MapRoom room) =>
        string.IsNullOrWhiteSpace(room.Environment) && !string.IsNullOrWhiteSpace(room.InferredEnvironment);

    private static string? DisplayedEnvironment(MapRoom room) => UsesInference(room) ? room.InferredEnvironment : room.Environment;

    public static MapEnvironmentStyle Resolve(MapRoom room, IReadOnlyList<MapEnvironmentStyle>? palette = null)
    {
        palette ??= Styles;
        var style = palette.FirstOrDefault(s => s.Key == Normalize(DisplayedEnvironment(room))) ?? palette[0];
        return style with { Color = room.Color ?? style.Color, Symbol = room.Symbol ?? style.Symbol };
    }

    /// <summary>Tooltip/editor text: the terrain label, marked when it came from the local classifier.</summary>
    public static string Describe(MapRoom room)
    {
        var label = LabelFor(DisplayedEnvironment(room));
        return UsesInference(room) ? L.Format(L.MapTerrainInferred, label, Math.Round((room.InferredConfidence ?? 0) * 100)) : label;
    }
```
Update the class summary comment to: `/// <summary>Presentation palette. Server or manual terrain wins; the local classifier's hint fills gaps and is labeled as inferred.</summary>`.

`RoomMapControl.cs:297`: replace `MapEnvironmentPalette.LabelFor(room.Environment)` with `MapEnvironmentPalette.Describe(room)`.

`MapEditors.cs` room editor: add `[ObservableProperty] private string _terrainProvenance = "";` (or a plain property with `OnPropertyChanged`, matching the file's style) and in `Load(MapRoom room)` set `TerrainProvenance = MapEnvironmentPalette.UsesInference(room) ? MapEnvironmentPalette.Describe(room) : "";`. In `MapEditorView.cs`, directly below the terrain (`Environment`) input, add a muted 11-pt `TextBlock` bound to `TerrainProvenance`, hidden when empty (`IsVisible` via a converter-free approach: bind `Text` and set `IsVisible` in a `PropertyChanged` handler, or use `Ui.Text` + `Bind(IsVisibleProperty, new Binding(nameof(editor.HasTerrainProvenance)))` with a computed `HasTerrainProvenance` property).

Add `MapTerrainInferred` to the five resx files (values above) and regenerate `Strings.cs`.

- [ ] **Step 4: Run tests** — Expected: 3 passed. Also `--filter "FullyQualifiedName~MapRenderingTests|FullyQualifiedName~MapEditorTests"` still green.

- [ ] **Step 5: Build + localization check** — solution builds with 0 warnings; `generate-localization.py --check` passes.

---

### Task 8: Controller inference scheduling

**Files:**
- Modify: `src/Wandur.Desktop/WorkspaceController.cs:37-45` (constructor: add `RoomClassificationService? classification = null`, expose `public RoomClassificationService? Classification { get; }`), `:374` (DisposeAsync cancels inference)
- Modify: `src/Wandur.Desktop/WorkspaceController.Mapping.cs` (`StartMapping` → reset + `ScheduleInference()`; end of `ObserveRoom` → `ScheduleInference()`)
- Create: `src/Wandur.Desktop/WorkspaceController.Inference.cs`
- Modify: `src/Wandur.Desktop/SessionWorkspace.cs:63-77`, `src/Wandur.Desktop/MainWindow.cs:51` (thread the optional service through), `src/Wandur.Desktop/App.axaml.cs` (DI registration)
- Test: `tests/Wandur.Desktop.Tests/RoomInferenceSchedulingTests.cs`

**Interfaces:**
- Consumes: `RoomClassificationService`, `IRoomEnvironmentClassifier`, `RoomMapTracker.RoomsNeedingInference/ApplyInference`, `RoomTextPreprocessor.InferenceKey`, `Settings.ClassifyRoomsLocally/RoomClassificationThreshold`.
- Produces: `WorkspaceController.Classification` property; `internal void ScheduleInference()`; `internal Task? InferenceWorkerForTests` (the running worker task, for deterministic tests).

- [ ] **Step 1: Write the failing test**

`tests/Wandur.Desktop.Tests/RoomInferenceSchedulingTests.cs`:
```csharp
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Wandur.Core.Classification;
using Wandur.Core.Mapping;
using Wandur.Core.Settings;
using Wandur.Desktop;

namespace Wandur.Desktop.Tests;

public sealed class RoomInferenceSchedulingTests
{
    private sealed class FakeClassifier : IRoomEnvironmentClassifier
    {
        public readonly List<string> Seen = [];
        public string ModelVersion => "t"; public double DefaultThreshold => 0.8;
        public RoomEnvironmentPrediction? Classify(string name, string description, double threshold)
        { lock (Seen) Seen.Add(name); return name.Contains("Pine") ? new("forest", 0.95, "t") : null; }
    }

    private static async Task Pump(WorkspaceController controller)
    {
        for (var i = 0; i < 40; i++)
        {
            var worker = controller.InferenceWorkerForTests;
            if (worker is not null) await Task.WhenAny(worker, Task.Delay(50));
            Dispatcher.UIThread.RunJobs();
            if (worker is null || worker.IsCompleted) { Dispatcher.UIThread.RunJobs(); if (controller.InferenceWorkerForTests is null or { IsCompleted: true }) return; }
        }
    }

    [AvaloniaFact]
    public async Task ObservedRoomsWithoutTerrainAreClassifiedAndAppliedOnTheUiThread()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-infer-" + Guid.NewGuid());
        var classifier = new FakeClassifier();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore(), classification: RoomClassificationService.ForTesting(classifier));
        controller.Map.Observe(new("1", "Pine Trail", "Tall pines crowd the narrow trail.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        controller.Map.Observe(new("2", "Harbor", "Ships creak at their moorings.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp) { Environment = "water" }, "east");
        controller.Map.Observe(new("3", "Dunes", "Endless dunes roll away.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp), "east");
        controller.ScheduleInference();
        await Pump(controller);
        var rooms = controller.Map.Snapshot.Rooms.ToDictionary(r => r.Id);
        Assert.Equal("forest", rooms["s:1"].InferredEnvironment); Assert.Equal(0.95, rooms["s:1"].InferredConfidence);
        Assert.Null(rooms["s:2"].InferredEnvironment); Assert.DoesNotContain("Harbor", classifier.Seen); // server terrain never classified
        Assert.Null(rooms["s:3"].InferredEnvironment); Assert.NotNull(rooms["s:3"].InferredKey); // abstained, recorded
        Assert.All(rooms.Values, r => Assert.False(r.IsManuallyEdited));
        var seen = classifier.Seen.Count;
        controller.ScheduleInference(); await Pump(controller);
        Assert.Equal(seen, classifier.Seen.Count); // nothing stale, nothing re-run
    }

    [AvaloniaFact]
    public async Task DisabledSettingSchedulesNothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-infer-" + Guid.NewGuid());
        var classifier = new FakeClassifier();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore(), classification: RoomClassificationService.ForTesting(classifier));
        controller.SaveSettings(controller.Settings with { ClassifyRoomsLocally = false });
        controller.Map.Observe(new("1", "Pine Trail", "Tall pines crowd the narrow trail.", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp));
        controller.ScheduleInference(); await Pump(controller);
        Assert.Empty(classifier.Seen);
        Assert.Null(controller.Map.Snapshot.Rooms.Single().InferredEnvironment);
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `RoomInferenceSchedulingTests`; Expected: build error (constructor parameter, `ScheduleInference`, `InferenceWorkerForTests`).

- [ ] **Step 3: Implement**

`src/Wandur.Desktop/WorkspaceController.cs`: change the constructor signature to
```csharp
public WorkspaceController(Wandur.Desktop.Terminal.ITranscriptDisplayFactory displays, ISettingsStore store, IPasswordVault passwords, IRoomMapStore maps, IScriptRuntimeFactory scriptRuntimes, IWorldScriptLibraryStore scriptLibraryStore, IWorldKnowledgeStore? knowledge = null, IAgentClientServices? agents = null, RoomClassificationService? classification = null)
```
and in the body add `Classification = classification;`. Add `public RoomClassificationService? Classification { get; }` near `Settings`. In `DisposeAsync`, call `CancelInference();` right after `_disposed = true;`.

`src/Wandur.Desktop/WorkspaceController.Inference.cs`:
```csharp
using Avalonia.Threading;
using Wandur.Core.Classification;
using Wandur.Core.Mapping;

namespace Wandur.Desktop;

public sealed partial class WorkspaceController
{
    private const int InferenceQueueLimit = 512;
    private readonly object _inferenceGate = new();
    private readonly Queue<(string Id, string Name, string Description)> _inferenceQueue = new();
    private readonly HashSet<string> _inferenceQueued = [];
    private CancellationTokenSource? _inferenceCancellation;
    private Task? _inferenceWorker;
    private RoomMapTracker? _inferenceMap;

    internal Task? InferenceWorkerForTests => _inferenceWorker;

    /// <summary>Queues rooms lacking terrain whose inference is missing or stale. UI thread only.</summary>
    internal void ScheduleInference()
    {
        if (_disposed || Classification is null || !Settings.ClassifyRoomsLocally) return;
        if (Classification.TryGetClassifier() is not { } classifier) return;
        var map = Map;
        var candidates = map.RoomsNeedingInference(classifier.ModelVersion);
        if (candidates.Count == 0) return;
        lock (_inferenceGate)
        {
            if (!ReferenceEquals(_inferenceMap, map)) { _inferenceQueue.Clear(); _inferenceQueued.Clear(); _inferenceMap = map; }
            foreach (var room in candidates)
            {
                if (_inferenceQueue.Count >= InferenceQueueLimit) break;
                if (_inferenceQueued.Add(room.Id)) _inferenceQueue.Enqueue((room.Id, room.Name, room.Description));
            }
            if (_inferenceWorker is { IsCompleted: false }) return;
            _inferenceCancellation ??= new CancellationTokenSource();
            var token = _inferenceCancellation.Token;
            var threshold = Settings.RoomClassificationThreshold;
            _inferenceWorker = Task.Run(() => RunInference(classifier, map, threshold, token), token);
        }
    }

    private void RunInference(IRoomEnvironmentClassifier classifier, RoomMapTracker map, double threshold, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            (string Id, string Name, string Description) item;
            lock (_inferenceGate)
            {
                if (_inferenceQueue.Count == 0 || !ReferenceEquals(_inferenceMap, map)) return;
                item = _inferenceQueue.Dequeue();
            }
            var key = RoomTextPreprocessor.InferenceKey(item.Name, item.Description, classifier.ModelVersion);
            RoomEnvironmentPrediction? prediction;
            try { prediction = classifier.Classify(item.Name, item.Description, threshold); }
            catch (Exception ex) when (ex is InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            { lock (_inferenceGate) _inferenceQueued.Remove(item.Id); continue; }
            Dispatcher.UIThread.Post(() =>
            {
                lock (_inferenceGate) _inferenceQueued.Remove(item.Id);
                if (_disposed || token.IsCancellationRequested || !ReferenceEquals(Map, map)) return;
                if (map.ApplyInference(item.Id, key, classifier.ModelVersion, prediction)) _mapDirty = true;
            });
        }
    }

    private void CancelInference()
    {
        lock (_inferenceGate)
        {
            _inferenceCancellation?.Cancel();
            _inferenceCancellation?.Dispose();
            _inferenceCancellation = null;
            _inferenceQueue.Clear(); _inferenceQueued.Clear();
            _inferenceMap = null;
        }
    }
}
```
Check how `_mapDirty` is set today in `WorkspaceController.Mapping.cs` (search `_mapDirty = true`) — if it is set inside a `Map.Changed` handler, the `ApplyInference` call already covers it and the explicit assignment is redundant but harmless; keep whichever matches existing mechanics.

`WorkspaceController.Mapping.cs`: in `StartMapping`, immediately after `_mapDirty = false;` add `CancelInference(); ScheduleInference();`. At the end of `ObserveRoom` (after the `if (!repeatsOrigin) {...}` block) add `ScheduleInference();`.

Plumbing: `SessionWorkspace` constructor gains `RoomClassificationService? classification = null` (store in `_classification`, pass to `new WorkspaceController(..., _agents, _classification)`); `MainWindow` constructor gains the same optional parameter and forwards it to `new SessionWorkspace(...)`; `App.axaml.cs` `BuildServices` registers
```csharp
        services.AddSingleton(provider => new RoomClassificationService(directory, provider.GetRequiredService<HttpClient>()));
```
before `services.AddSingleton<MainWindow>();`. (`ValidateOnBuild` requires the constructor parameters to be resolvable — optional parameters with registered services resolve; unregistered optional ones fall back to null.)

- [ ] **Step 4: Run tests** — filter `RoomInferenceSchedulingTests`; Expected: 2 passed. Then `--filter "FullyQualifiedName~MapSessionTests|FullyQualifiedName~ProtocolMappingSessionTests"` still green.

- [ ] **Step 5: Build check** — solution 0 warnings.

---

### Task 9: Map tools section and view-model bindings

**Files:**
- Modify: `src/Wandur.Desktop/ViewModels/MapViewModel.cs` (status text, commands, enable property)
- Modify: `src/Wandur.Desktop/Views/MapView.cs` (new expander in the tools footer)
- Modify: localization resx ×5 + regenerate
- Test: `tests/Wandur.Desktop.Tests/RoomInferenceToolsTests.cs`

**Interfaces:**
- Produces on `MapViewModel`: `bool HasClassification`, `string ClassificationStatus`, `bool CanDownloadModel`, `bool ClassifyRoomsLocally { get; set; }` (saves settings through the controller), `IRelayCommand DownloadModelCommand`, `IRelayCommand InstallModelFromFileCommand` (opens a file picker through `TopLevel.GetTopLevel` of the view; the view model exposes `Func<Task<string?>>? PickModelFile` the view sets).
- Strings: `MapInferenceSection` = `Room terrain inference`; `MapInferenceNotInstalled` = `Local model not installed (80 MB download).`; `MapInferenceDownloading` = `Downloading model: {0}%`; `MapInferenceReady` = `Local model {0} ready.`; `MapInferenceFailed` = `Local model unavailable: {0}`; `MapInferenceDownload` = `Download model`; `MapInferenceInstallFile` = `Install from file`; `MapInferenceEnable` = `Color rooms with the local model`.
  - de: `Geländeableitung für Räume` / `Lokales Modell nicht installiert (80 MB Download).` / `Modell wird geladen: {0}%` / `Lokales Modell {0} bereit.` / `Lokales Modell nicht verfügbar: {0}` / `Modell herunterladen` / `Aus Datei installieren` / `Räume mit dem lokalen Modell einfärben`
  - es: `Inferencia de terreno de salas` / `Modelo local no instalado (descarga de 80 MB).` / `Descargando modelo: {0}%` / `Modelo local {0} listo.` / `Modelo local no disponible: {0}` / `Descargar modelo` / `Instalar desde archivo` / `Colorear salas con el modelo local`
  - fr: `Inférence du terrain des salles` / `Modèle local non installé (téléchargement de 80 Mo).` / `Téléchargement du modèle : {0}%` / `Modèle local {0} prêt.` / `Modèle local indisponible : {0}` / `Télécharger le modèle` / `Installer depuis un fichier` / `Colorer les salles avec le modèle local`
  - pt-BR: `Inferência de terreno das salas` / `Modelo local não instalado (download de 80 MB).` / `Baixando modelo: {0}%` / `Modelo local {0} pronto.` / `Modelo local indisponível: {0}` / `Baixar modelo` / `Instalar de arquivo` / `Colorir salas com o modelo local`

- [ ] **Step 1: Write the failing test**

`tests/Wandur.Desktop.Tests/RoomInferenceToolsTests.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Classification;
using Wandur.Core.Settings;
using Wandur.Desktop;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class RoomInferenceToolsTests
{
    private sealed class FakeClassifier : IRoomEnvironmentClassifier
    {
        public string ModelVersion => "9.9.9"; public double DefaultThreshold => 0.8;
        public RoomEnvironmentPrediction? Classify(string name, string description, double threshold) => null;
    }

    [AvaloniaFact]
    public async Task ToolsSectionShowsStatusAndBindsEnableSetting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-tools-" + Guid.NewGuid());
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore(), classification: RoomClassificationService.ForTesting(new FakeClassifier()));
        var view = new MapView(controller);
        var window = new Window { Content = view, Width = 700, Height = 500 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var toggle = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "MapToolsToggle");
            toggle.IsChecked = true; Dispatcher.UIThread.RunJobs();
            var status = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MapInferenceStatus");
            Assert.Contains("9.9.9", status.Text);
            var enable = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "MapInferenceEnable");
            Assert.True(enable.IsChecked);
            enable.IsChecked = false; Dispatcher.UIThread.RunJobs();
            Assert.False(controller.Settings.ClassifyRoomsLocally);
            Assert.False(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MapInferenceDownload").IsEnabled); // already installed
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WithoutServiceTheSectionIsHidden()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-tools-" + Guid.NewGuid());
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
            new SettingsStore(Path.Combine(directory, "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(),
            new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var view = new MapView(controller);
        var window = new Window { Content = view, Width = 700, Height = 500 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Empty(view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "MapInferenceStatus"));
        }
        finally { window.Close(); }
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `RoomInferenceToolsTests`; Expected: assertion/build failure (no such controls).

- [ ] **Step 3: Implement view-model members**

In `MapViewModel.cs` (partial `ObservableObject`, CommunityToolkit style used by the file) add:
```csharp
    public RoomClassificationService? Classification => _controller?.Classification;
    public bool HasClassification => Classification is not null && IsLive;
    public Func<Task<string?>>? PickModelFile { get; set; }
    public string ClassificationStatus => Classification?.Status switch
    {
        { State: RoomClassificationState.Ready, Version: var version } => L.Format(L.MapInferenceReady, version ?? ""),
        { State: RoomClassificationState.Downloading, Progress: var progress } => L.Format(L.MapInferenceDownloading, Math.Round(progress * 100)),
        { State: RoomClassificationState.Failed, Message: var message } => L.Format(L.MapInferenceFailed, message ?? ""),
        _ => L.MapInferenceNotInstalled
    };
    public bool CanDownloadModel => Classification is { Status.State: RoomClassificationState.NotInstalled or RoomClassificationState.Failed };
    public bool ClassifyRoomsLocally
    {
        get => _controller?.Settings.ClassifyRoomsLocally ?? false;
        set { if (_controller is null || value == _controller.Settings.ClassifyRoomsLocally) return; _controller.SaveSettings(_controller.Settings with { ClassifyRoomsLocally = value }); OnPropertyChanged(); if (value) _controller.ScheduleInference(); }
    }
    [RelayCommand(CanExecute = nameof(CanDownloadModel))]
    private async Task DownloadModel() { if (Classification is { } service) { await service.DownloadAsync(CancellationToken.None); _controller?.ScheduleInference(); } }
    [RelayCommand]
    private async Task InstallModelFromFile()
    {
        if (Classification is not { } service || PickModelFile is null) return;
        if (await PickModelFile() is { } path) { await service.InstallFromFileAsync(path, CancellationToken.None); _controller?.ScheduleInference(); }
    }
    private void ClassificationChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(ClassificationStatus)); OnPropertyChanged(nameof(CanDownloadModel)); DownloadModelCommand.NotifyCanExecuteChanged();
    });
```
Subscribe `Classification.Changed += ClassificationChanged` in `Attach()` and unsubscribe in `Detach()`. If `MapViewModel` is not `partial` or lacks `using CommunityToolkit.Mvvm.Input;`, add them (the file already uses `[ObservableProperty]`, so the toolkit generator is active). Also raise `OnPropertyChanged(nameof(ClassifyRoomsLocally))` inside the existing `RebindSession`/settings refresh path so an external settings change updates the checkbox.

- [ ] **Step 4: Implement the view section**

In `MapView.cs`, after `footer.Children.Add(CreateRouteTools());` (non-editor branch only, i.e. inside `else if (editMap is not null)` is wrong — put it right before `footer.Children.Add(CreateRouteTools());` and guard with `if (!editingWorkspace)`):
```csharp
        if (!editingWorkspace && model.HasClassification)
        {
            var statusText = Ui.Text("", 11, "muted"); statusText.Name = "MapInferenceStatus";
            statusText.Bind(TextBlock.TextProperty, new Binding(nameof(model.ClassificationStatus)));
            var download = Action(nameof(L.MapInferenceDownload), "MapInferenceDownload", model.DownloadModelCommand);
            var install = Action(nameof(L.MapInferenceInstallFile), "MapInferenceInstallFile", model.InstallModelFromFileCommand);
            model.PickModelFile = async () =>
            {
                var top = TopLevel.GetTopLevel(this);
                if (top is null) return null;
                var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                { AllowMultiple = false, FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Model package") { Patterns = ["*.zip"] }] });
                return files.Count == 1 ? files[0].TryGetLocalPath() : null;
            };
            var section = new Expander
            {
                Name = "MapInferenceSection", IsExpanded = false,
                Content = new StackPanel { Spacing = 6, Children = { statusText, Check(nameof(L.MapInferenceEnable), "MapInferenceEnable", nameof(model.ClassifyRoomsLocally)),
                    new WrapPanel { Orientation = Orientation.Horizontal, Children = { download, install } } } }
            };
            section.Bind(HeaderedContentControl.HeaderProperty, LocalizedText.Binding(nameof(L.MapInferenceSection)));
            footer.Children.Add(section);
        }
```
Confirm the local `Check(...)` helper's signature in `MapView.cs` (it takes a label key, a control name and a bound property name; the grid-mode checkbox is the example) and match it exactly. Add the eight strings to the five resx files and regenerate `Strings.cs`.

- [ ] **Step 5: Run tests** — filter `RoomInferenceToolsTests`; Expected: 2 passed. Then `--filter "FullyQualifiedName~MapViewTests|FullyQualifiedName~MapEditorTests"` still green; `python3 scripts/generate-localization.py --check` passes.

---

### Task 9b: Auto-center toolbar toggle (user request, 2026-09-18)

**Files:**
- Modify: `src/Wandur.Core/Settings/ClientSettings.cs` (add `public bool MapAutoCenter { get; init; } = true;` to `ClientSettings`)
- Modify: `src/Wandur.Desktop/ViewModels/MapViewModel.cs` (`AutoCenter` property; `Refresh` re-centers on room change)
- Modify: `src/Wandur.Desktop/Views/MapView.cs` (toolbar `ToggleButton` named `MapAutoCenterToggle`, placed right after the existing `center` button in the `buttons` WrapPanel)
- Modify: localization resx ×5 (`MapAutoCenter` = `Keep current room centered`; de `Aktuellen Raum zentriert halten`; es `Mantener centrada la sala actual`; fr `Garder la salle actuelle centrée`; pt-BR `Manter a sala atual centralizada`) + regenerate
- Test: `tests/Wandur.Desktop.Tests/MapAutoCenterTests.cs`

**Interfaces:**
- Produces: `MapViewModel.AutoCenter : bool` (get/set; persisted through `_controller.SaveSettings(Settings with { MapAutoCenter = value })` when a controller exists, else in-memory; default `true`). Behavior: on every `Refresh()`, if `AutoCenter` and the current room exists and its id differs from the last centered id, call `_fitFloor = false; CenterOnFloor(current)`; setting `AutoCenter = true` centers immediately on the current room. Manual `Pan`/zoom does not turn it off.

- [ ] **Step 1: Write the failing test**

`tests/Wandur.Desktop.Tests/MapAutoCenterTests.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Mapping;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class MapAutoCenterTests
{
    private static RoomObservation At(string id, double x, double y) =>
        new(id, "Room " + id, "", new Dictionary<string, string?>(), Source: RoomDataSource.Gmcp) { X = x, Y = y };

    [Fact]
    public void AutoCenterFollowsTheCurrentRoomAndCanBeTurnedOff()
    {
        var tracker = new RoomMapTracker();
        var model = new MapViewModel(tracker); model.Attach();
        Assert.True(model.AutoCenter);
        tracker.Observe(At("1", 0, 0));
        tracker.Observe(At("2", 5, 3), "east");
        Assert.Equal(5, model.CenterX); Assert.Equal(3, model.CenterY);
        model.Pan(40, 40);
        tracker.Observe(At("3", 9, 9), "east");
        Assert.Equal(9, model.CenterX); Assert.Equal(9, model.CenterY); Assert.Equal(0, model.PanX);
        model.AutoCenter = false;
        tracker.Observe(At("4", 20, 20), "east");
        Assert.Equal(9, model.CenterX);
        model.AutoCenter = true;
        Assert.Equal(20, model.CenterX); Assert.Equal(20, model.CenterY);
        model.Detach();
    }

    [AvaloniaFact]
    public void ToolbarToggleBindsAutoCenter()
    {
        var model = new MapViewModel(new RoomMapTracker()); model.Attach();
        var view = new MapView(model);
        var window = new Window { Content = view, Width = 600, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var toggle = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "MapAutoCenterToggle");
            Assert.True(toggle.IsChecked);
            toggle.IsChecked = false; Dispatcher.UIThread.RunJobs();
            Assert.False(model.AutoCenter);
        }
        finally { window.Close(); model.Detach(); }
    }
}
```
(If `RoomObservation` coordinates are not honored for room placement in the tracker, place rooms via a saved `MapSnapshot` with explicit X/Y instead, keeping the assertions on `CenterX/CenterY`.)

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Wandur.Desktop.Tests/Wandur.Desktop.Tests.csproj -c Debug --nologo -v q --filter "FullyQualifiedName~MapAutoCenterTests"`; Expected: build error (`AutoCenter` missing).

- [ ] **Step 3: Implement**

`MapViewModel.cs`: add fields/properties
```csharp
    private string? _lastCenteredRoomId;
    private bool _autoCenter = true;
    public bool AutoCenter
    {
        get => _controller?.Settings.MapAutoCenter ?? _autoCenter;
        set
        {
            if (value == AutoCenter) return;
            _autoCenter = value;
            if (_controller is not null) _controller.SaveSettings(_controller.Settings with { MapAutoCenter = value });
            OnPropertyChanged();
            if (value && Snapshot.Rooms.FirstOrDefault(r => r.Id == Snapshot.CurrentRoomId) is { } current)
            { _fitFloor = false; CenterOnFloor(current); _lastCenteredRoomId = current.Id; }
        }
    }
```
In `Refresh()`, after the existing `if/else if/else if` centering chain and before `_lastCurrentFloor = ...`, add:
```csharp
        if (AutoCenter && current is not null && current.Id != _lastCenteredRoomId) { _fitFloor = false; CenterOnFloor(current); }
        _lastCenteredRoomId = current?.Id;
```
(When the existing chain already centered on `current` this re-centers harmlessly; when the user had panned, this resets `PanX/PanY` to 0 by design.) Also raise `OnPropertyChanged(nameof(AutoCenter))` where the view model refreshes settings-driven properties on `RebindSession`.

`MapView.cs`: after `var center = Ui.ToolbarIconKey(...)` add
```csharp
        var autoCenter = Ui.ToolbarIconKey(new ToggleButton { Name = "MapAutoCenterToggle" },
            "M 8,2 V 5 M 8,11 V 14 M 2,8 H 5 M 11,8 H 14 M 8,6 A 2,2 0 1 0 8,10 A 2,2 0 1 0 8,6", nameof(L.MapAutoCenter));
        autoCenter.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(model.AutoCenter)) { Mode = BindingMode.TwoWay });
```
and insert `autoCenter` into the `buttons` WrapPanel's `Children` immediately after `center`. Add the string to the five resx files; regenerate `Strings.cs`. `ClientSettings`: add `public bool MapAutoCenter { get; init; } = true;`.

- [ ] **Step 4: Run tests** — filter `MapAutoCenterTests` (2 pass) and `MapViewTests|MapNavigationTests` (still green); `python3 scripts/generate-localization.py --check`; solution build 0 warnings.

---

### Task 10: Notices, docs, and third-party attribution

**Files:**
- Modify: `THIRD-PARTY-NOTICES.md`
- Modify: `docs/mapper.md` ("Reading the map" bullet on terrain)
- Modify: `docs/client-architecture.md` (short Classification subsection)

- [ ] **Step 1: Notices**

Append to the list in `THIRD-PARTY-NOTICES.md`:
```markdown
- [ONNX Runtime](https://github.com/microsoft/onnxruntime): MIT; local inference for the optional room-terrain classifier.
- [Microsoft.ML.Tokenizers](https://github.com/dotnet/machinelearning): MIT; WordPiece tokenization for that classifier.
- Room-terrain model package (downloaded on demand, not bundled): fine-tuned [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2), Apache-2.0, from [room-classifier](https://github.com/YouCantGoThatWay/room-classifier); training-data notices ship inside the package's `LICENSES.md`. `tests/Fixtures/room-classifier-parity.json` contains 20 tbaMUD room descriptions (LGPL, CircleMUD/DikuMUD 2020 relicense) used as parity test vectors.
```

- [ ] **Step 2: Mapper doc**

In `docs/mapper.md`, replace the bullet beginning `- Terrain comes from room metadata or your edits.` with:
```markdown
- Terrain comes from room metadata or your edits. When a game sends no terrain, an optional local model can infer it from the room's name and description; inferred terrain uses the same colors and is marked "inferred" with its confidence in the room tooltip and editor. Server terrain and your edits always take precedence, and inference never changes exits, identity or routes. Enable it under **Map tools → Room terrain inference** (one 80 MB download, kept in the app data folder; nothing leaves your computer during inference).
```

- [ ] **Step 3: Architecture doc**

Add to `docs/client-architecture.md` (near the mapping section):
```markdown
### Room terrain inference

`Wandur.Core.Classification` runs the room-classifier model package locally: `RoomTextPreprocessor` reproduces the package's `preprocessing_spec.json`, `OnnxRoomEnvironmentClassifier` runs the encoder through ONNX Runtime one room at a time and applies the logistic head, and `ModelPackageInstaller` downloads/verifies packages by manifest hash into `models/room-classifier/<version>/`. `WorkspaceController.Inference` queues rooms without terrain onto a background worker and applies results on the UI thread through `RoomMapTracker.ApplyInference`, which stores `InferredEnvironment`/`InferredConfidence`/`InferredKey` on `MapRoom`. Presentation precedence lives in `MapEnvironmentPalette`: server or manual terrain first, inference second.
```

- [ ] **Step 4: Verify** — `dotnet build Wandur.sln -c Debug --nologo -v q` still clean (docs only, sanity).

---

### Task 11: Handoff fix — SessionTests fixture labels

**Files:**
- Modify: `tests/Wandur.Core.Tests/SessionTests.cs` (test `NativeMsdpRefreshReportsAndRequestsMappedVariablesWithoutNegotiatingGmcp`, the two `new FieldBinding` fixtures near lines 60–75)

- [ ] **Step 1: Reproduce** — `dotnet test tests/Wandur.Core.Tests/Wandur.Core.Tests.csproj -c Debug --nologo -v q --filter "FullyQualifiedName~SessionTests"`; Expected: 1 failed (mapping rejected for missing `Label`).

- [ ] **Step 2: Fix** — add `Label = "Health"` to both `FieldBinding` object initializers in that test (read the record definition in `src/Wandur.Models/MappingContracts.cs` to confirm the property name). Do not change production code.

- [ ] **Step 3: Verify** — rerun the filter; Expected: all SessionTests pass. Then run `--filter "FullyQualifiedName~WorldCatalogTests|FullyQualifiedName~ProtocolBindingTests"` and Desktop `--filter "FullyQualifiedName~ProtocolMappingSessionTests"`; Expected: green.

---

### Task 12: Full verification run

- [ ] **Step 1: Full Release test run** — `cd "/Volumes/Extreme SSD/workspace/Wundur" && dotnet test Wandur.sln -c Release 2>&1 | tail -15` (drop `--no-restore` the first time so lock files are honored). Expected: 0 failed across Core, Desktop and Discovery; record the counts (baseline was 681 = 390 Core + 266 Desktop + 25 worker, plus the new tests).
- [ ] **Step 2: Gated parity run** — `WANDUR_ROOM_MODEL_DIR=<scratchpad>/room-model dotnet test tests/Wandur.Core.Tests/Wandur.Core.Tests.csproj -c Release --filter "FullyQualifiedName~OnnxRoomEnvironmentClassifierTests"`; Expected: 2 passed (real ONNX parity).
- [ ] **Step 3: Localization check** — `python3 scripts/generate-localization.py --check`; Expected: exit 0.
- [ ] **Step 4: Backend suite (unchanged, sanity)** — `cd directory-server && .venv/bin/python -m unittest discover -s tests -v 2>&1 | tail -3`; Expected: 44 passed (no backend changes in this plan).
- [ ] **Step 5: Report** — write the counts into the handoff summary for Task 13.

---

### Task 13: docs/verification.md entries and app rebuild

**Files:**
- Modify: `docs/verification.md` (append two dated sections)
- Run: `bash scripts/package-macos.sh`

- [ ] **Step 1: Verification doc** — append:
```markdown
## Icesus curated mapping and automatic directory refresh (2026-09-18)

- Fixed the `SessionTests` MSDP refresh fixture (missing `Label`) and reran the Core, Desktop and Discovery suites: <counts from Task 12>. Localization facade check passed. Backend suite unchanged at 44 passed.
- Verified `/directory` on port 8765 still publishes Icesus's nine bindings and the cached client catalog retains them (Task 14).
- Live Icesus login was not exercised: verification used the official Mudlet package definitions plus simulated protocol packets. One app restart is required to load the rebuilt executable; afterwards directory data refreshes periodically and validated mappings update active sessions.

## Room terrain inference (2026-09-18)

- Added optional local room-terrain inference: the room-classifier model package (fine-tuned MiniLM over ONNX Runtime, downloaded on demand, verified by manifest hash) classifies rooms that arrive without terrain; results persist on the map as inferred fields and paint the same palette colors, with provenance and confidence in tooltips and the editor. Server terrain and manual edits take precedence; inference never touches exits, identity or routes.
- Parity: the C# preprocessing reproduces all 23 package fixtures' texts (unit test) and the full ONNX chain reproduces every fixture's token ids, probabilities and predictions (gated test, run locally against package 0.1.1).
- Controller scheduling verified with a fake classifier: rooms with server terrain are never classified, abstentions are recorded so they are not retried, stale results are rejected, the setting disables scheduling, and results apply on the UI thread without setting the manual-edit flag.
- Model download requires the `v0.1.1` release assets to be published on the room-classifier repository; until then, use "Install from file" with the locally built package zip.
```
Fill in the real counts.

- [ ] **Step 2: Rebuild the app bundle** — `bash scripts/package-macos.sh 2>&1 | tail -5`; Expected: `artifacts/macos/Wandur.app` rebuilt. Do **not** restart the user's running Wandur or MUD sessions.

- [ ] **Step 3: Sanity** — `ls -la artifacts/macos/Wandur.app/Contents/MacOS/ | head -3` and confirm the ONNX Runtime native library is present in the bundle (`find artifacts/macos/Wandur.app -name "libonnxruntime*" | head -2`). If missing, add `<RuntimeIdentifier>`-appropriate handling per the publish command in `package-macos.sh` and rebuild.

---

### Task 14: Directory/catalog verification (handoff)

- [ ] **Step 1: API** — `curl -s http://127.0.0.1:8765/directory | python3 -c "import sys,json; d=json.load(sys.stdin); w=[x for x in d.get('worlds',d if isinstance(d,list) else []) if 'icesus' in json.dumps(x).lower()]; print(len(w), 'icesus entries'); print(sum(len(x.get('mapping',{}).get('bindings',[])) for x in w), 'bindings')"` — adapt the JSON path to the actual response shape (read `directory-server/app.py` `/directory` handler if needed). Expected: Icesus present with 9 bindings. If the API is not running, start it the way the handoff describes (`directory-server/.venv/bin/python app.py` on port 8765 in the background) and note that in the report.
- [ ] **Step 2: Client cache** — verify the cached catalog has Icesus mappings without dumping profile payloads: `sqlite3 ~/Library/Application\ Support/Wandur/wandur.db "select count(*) from sqlite_master"` to confirm access, then a targeted query against the catalog table only (find its name via `.tables`), counting rows whose payload contains `play.icesus.org`. Print counts only.
- [ ] **Step 3: Worker republish** — do not reinstall the discovery worker in this plan; note in the final report that `scripts/install-discovery-worker-macos.py` can be rerun to pick up the changed protocol support advertisement (it respects existing budget/timestamps).

---

## Self-review notes

- Spec coverage: preprocessor/contract (T1), map fields + validation + tracker (T2), package verification (T3), ONNX classifier + parity (T4), installer with zip-slip/size caps (T5), service + settings (T6), palette precedence/tooltip/editor (T7), controller queue/worker/cancellation + DI plumbing (T8), Map tools UI + strings (T9), notices/docs (T10), handoff items (T11–T14). Out of scope per spec: modifiers, chunking, batching, GPU.
- Type consistency: `IRoomEnvironmentClassifier.Classify(name, description, threshold)` used identically in T4/T6/T8; `RoomMapTracker.ApplyInference(id, key, modelVersion, prediction)` in T2/T8; `RoomClassificationService.ForTesting` in T6/T8/T9; `MapEnvironmentPalette.Describe/UsesInference` in T7 only.
- Adaptation points flagged: `Microsoft.ML.Tokenizers 2.0.0` `EncodeToIds` overload names (T4), `MapView.Check` helper signature (T9), `_mapDirty` mechanics (T8), directory API response shape (T14).
