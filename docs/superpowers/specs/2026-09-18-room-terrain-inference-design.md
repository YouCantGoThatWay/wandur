# Room terrain inference (local classifier) — design

Date: 2026-09-18
Status: approved in discussion; implementation plan follows.

## Goal

Color auto-mapper room boxes by environment even when the game supplies no
terrain metadata, using the locally trained room-environment classifier
(model package `wundur-room-classifier` v0.1.1 from
github.com/YouCantGoThatWay/room-classifier). Inference runs fully on the
user's machine through ONNX Runtime. It only affects presentation: it never
establishes room identity, changes exits, or influences routing. Server
terrain and manual edits always win over inference.

## Decisions

- **Delivery:** the ~80 MB model package is downloaded on demand from the
  repo's GitHub release (default URL below), verified against its
  `manifest.json` sha256 hashes, and installed under
  `<AppData>/Wandur/models/room-classifier/<version>/`. An "Install from
  file…" path accepts a local zip for offline/dev use. Nothing is bundled
  into `Wandur.app`.
- **Default state:** inference turns on automatically when a package is
  installed (clicking Download is the opt-in). A checkbox in Map tools turns
  it off/on; the threshold defaults to the package's 0.80 and is a setting.
- **Visual marking:** inferred terrain paints the same palette color as
  server/manual terrain. Provenance and confidence appear only in the room
  tooltip and the room editor ("Forest · inferred 87%").
- **Palette mapping:** model classes `settlement` and `urban` map to the
  existing `city` palette key (its `Normalize` already does this). No new
  palette keys or terrain strings.
- **Contract fidelity:** the C# pipeline reproduces `preprocessing_spec.json`
  exactly: clean(name) + "\n" + clean(description) with the five strip
  patterns and whitespace collapse; WordPiece tokenization (lowercase,
  `[CLS]` … `[SEP]`, hard truncation at 256 word pieces); one text per
  ONNX call (batch=1, no padding); mean-pool over the attention mask; L2
  normalize; `head.json` logits (`emb @ coef.T + intercept`) → softmax;
  abstain below threshold. `parity_fixtures.json` is the acceptance test.

## Components

### `src/Wandur.Core/Classification/` (new)

- `RoomEnvironmentPrediction(string Environment, double Confidence, string ModelVersion)`.
- `IRoomEnvironmentClassifier`: `string ModelVersion`, `double Threshold`,
  `RoomEnvironmentPrediction? Classify(string name, string description)`
  (synchronous, CPU-bound; callers run it off the UI thread). Returns null
  when the best class is below threshold.
- `RoomTextPreprocessor`: `Clean(string)`, `BuildText(name, description)`,
  `InferenceKey(name, description, modelVersion)` (SHA-256 of
  `modelVersion + "\n" + BuildText`), all pure.
- `ModelPackage`: loads a package directory; verifies every file listed in
  `manifest.json` by sha256 before use; exposes `Version`, `Threshold`,
  `Classes`, `Coefficients` (15×384), `Intercepts`, `MaxWordPieces`,
  `VocabularyStream()` (built from `tokenizer.json` `model.vocab` ordered by
  id), `EncoderPath`. Rejects packages whose taxonomy version is not `1.0.0`,
  whose `version` is not `major.minor.patch`, or whose manifest lists
  paths outside the package. Class names are not validated against the
  Desktop palette (Core cannot see it); an unknown class simply renders
  with the neutral `unknown` style.
- `OnnxRoomEnvironmentClassifier : IRoomEnvironmentClassifier, IDisposable`:
  `Microsoft.ML.Tokenizers.BertTokenizer` (created from the vocabulary
  stream, lowercase, accents stripped) — encodes without special tokens,
  truncates to `MaxWordPieces - 2`, then wraps with `[CLS]`/`[SEP]` ids
  (matches HF truncation semantics); `Microsoft.ML.OnnxRuntime`
  `InferenceSession` over `encoder.onnx` with `input_ids`/`attention_mask`
  int64 tensors of shape [1, n]; pooling/normalize/head/softmax in plain C#.
  Thread safety: one session, calls serialized by a lock.
- `ModelPackageInstaller`: `InstalledVersion`, `Install(Stream zip)`,
  `DownloadAsync(Uri, IProgress<double>, CancellationToken)`, extraction to
  a temp directory, manifest verification, atomic rename into place, size
  cap (200 MB), zip-slip protection. Default URL:
  `https://github.com/YouCantGoThatWay/room-classifier/releases/download/v0.1.1/wundur-room-classifier-0.1.1.zip`.
- `RoomClassificationService` (Desktop-facing façade, lives in Core):
  owns the installer and the lazily loaded classifier, exposes
  `Status` (NotInstalled / Downloading(progress) / Ready(version) /
  Failed(message)), `Enabled`, `Threshold`, `Changed` event, and
  `TryGetClassifier()`.

### Map model and persistence

- `MapRoom` gains `string? InferredEnvironment`, `double? InferredConfidence`,
  `string? InferredKey` (the inference key; also encodes model version).
  `MapFileFormat.ValidRoom` validates: environment text ≤128, confidence
  finite in [0,1], key ≤128. JSON serialization and SQLite storage pick the
  fields up unchanged; revision-based merge already replaces whole records.
- `RoomMapTracker.ApplyInference(string id, string key, RoomEnvironmentPrediction? prediction)`:
  no-op if the room is gone or its current key (recomputed from its present
  name/description and the prediction's model version) differs from `key`
  (stale result); otherwise sets the three fields (null prediction clears
  them but records the key so an abstention is not retried), bumps
  `Revision`, does **not** set `IsManuallyEdited`, marks the map changed,
  fires `Changed`. Observations that change a room's name/description keep
  the old inferred fields until re-inference replaces them (they are
  presentation hints, and the key mismatch schedules a recompute).
- `RoomMapTracker.RoomsNeedingInference(string modelVersion)`: rooms with
  no `Environment` whose `InferredKey` differs from the current key.

### Presentation precedence

- `MapEnvironmentPalette.Resolve(room)` uses `room.Environment` when
  present, else `room.InferredEnvironment`, else unknown. Explicit
  `room.Color`/`Symbol` overrides still apply.
- `MapEnvironmentPalette.Describe(room)` returns the tooltip/editor label:
  the terrain label, plus " · inferred NN%" when the shown terrain came from
  inference. Tooltip in `RoomMapControl` and the room editor use it.

### Controller wiring (`WorkspaceController.Mapping`)

- Constructor gains an optional `RoomClassificationService?` (DI singleton;
  null in tests that don't need it).
- After every accepted observation, and once after `StartMapping` loads a
  saved map, `ScheduleInference()` enqueues `Map.RoomsNeedingInference`
  (current room first, bounded queue of 512, deduplicated by id) onto a
  single background worker (`Task.Run`, `CancellationTokenSource` tied to
  the session; cancelled in `StartMapping` and `DisposeAsync`). The worker
  classifies and posts `Map.ApplyInference` back through the existing
  dispatcher path the tracker's `Changed` consumers already use (the
  tracker is only mutated on the UI thread). Each applied result marks
  `_mapDirty` so the existing 2-second save cadence persists it.
- Service disabled/absent/failed → nothing is scheduled; the mapper is
  unchanged from today.

### Settings and UI

- `ClientSettings`: `bool ClassifyRoomsLocally` (default true — only
  meaningful once a package is installed), `double RoomClassificationThreshold`
  (default 0.80, validated in [0.5, 0.99]).
- Map tools panel (`MapView.CreateMapTools`), new "Room terrain inference"
  expander below the area/grid controls: status text; a Download button
  (progress in the status text; disabled while downloading); an
  Install-from-file button (file picker, zip); the enable checkbox bound to
  the setting. Strings added to `Strings.resx` and the four translations;
  `Strings.cs` regenerated.

### Packages, notices, packaging

- `Wandur.Core.csproj`: `Microsoft.ML.OnnxRuntime` 1.30.0,
  `Microsoft.ML.Tokenizers` 2.0.0. Lock files regenerated.
- `THIRD-PARTY-NOTICES.md`: both packages (MIT) and a note that the
  downloadable model (fine-tuned all-MiniLM-L6-v2, Apache-2.0; training-data
  notices inside the package's `LICENSES.md`) is not part of the app bundle.
- `scripts/package-macos.sh` unchanged; ONNX Runtime's native library ships
  via the NuGet runtime pack for osx-arm64/osx-x64.

## Testing

- Core unit tests: preprocessor parity against all 23 `parity_fixtures.json`
  entries (`name`/`description` → `text`), inference-key stability,
  `ApplyInference` staleness/precedence/no-manual-flag semantics,
  `RoomsNeedingInference`, `MapFileFormat` validation of the new fields,
  SQLite round-trip of inferred fields, `ModelPackage` manifest verification
  (tampered file rejected), installer zip-slip/size-cap rejection, settings
  validation.
- Gated integration test (`WANDUR_ROOM_MODEL_DIR` set): full ONNX chain
  reproduces every fixture's `token_ids`, `probs` (atol 1e-3) and
  `predicted`. Run locally once against the real package during
  implementation; skipped elsewhere.
- Desktop headless tests: an inferred room paints its palette color; a room
  with server terrain paints the server color regardless of inference;
  tooltip text includes "inferred"; Map tools section renders and the
  checkbox binds to the setting; controller schedules inference for observed
  rooms with a fake classifier and applies results (existing
  `MapSessionTests` patterns).

## Out of scope

Modifier tags, feature tags, chunked long-text policy, batching, GPU
execution providers, uploading corrections. Model updates beyond v0.1.1
reuse the same installer with a changed URL/version.
