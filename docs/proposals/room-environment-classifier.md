# Wandur room-environment classifier proposal

Status: proposed; no model is bundled or trained yet.

## Purpose

Assign useful environment colors and feature symbols to MUD rooms when the server does not supply them. Classification enhances presentation; it must never establish room identity, change exits, or determine whether a route is safe.

## Inputs and outputs

Input is the room name plus its stable description, with ANSI codes, prompts, player lists, and transient combat output removed. Names alone are often ambiguous. Keep the raw source record separately for audit and retain source/world/area identifiers.

Use separate output dimensions:

- Environment (one primary class): indoor, settlement, road, grassland, forest, desert, mountain, cave, water, underwater, swamp, snow/ice, spacecraft, other/unknown.
- Features (multiple independent tags): shop, inn, stairs, bridge, portal, landmark. A forest shop can be forest with a shop tag.
- Provenance: server, manual override, classifier; model version and a confidence score validated on held-out examples.

Manual assignments override server metadata; explicit server metadata overrides a classifier. Low-confidence or out-of-domain inputs retain a neutral appearance. Color is a palette choice, not an output category. Position, selection, and tracking uncertainty use separate outlines/markers.

## Sources for a training corpus

1. **CircleMUD stock worlds.** `.wld` records include title, description, exits, terrain sector and flags. The [format documentation](https://www.circlemud.org/cdp/building/building-3.html) describes the sector labels. The [world parser repository](https://github.com/isms/circlemud-world-parser) includes stock data converted to JSON in `output/wld`.
2. **Merc area files.** `#ROOMS` in `.are` contains the same useful text and sector evidence. See [Merc area documentation](https://github.com/alexmchale/merc-mud/blob/master/doc/area.txt). Deduplicate shared stock areas across codebases.
3. **LIGHT.** The original dataset has 663 natural-language locations, plus objects and character descriptions. It offers fantasy-language variety; dialogue episode counts are not unique room counts. See [LIGHT](https://parl.ai/projects/light/).
4. **Additional authorized game examples.** Include science fiction, modern, horror, terse descriptions, unusual spelling and non-English text if those languages become model requirements.

Retain authorship/source/license metadata per collection; check the terms of actual world content separately from engine code. The [CircleMUD license page](https://www.circlemud.org/license.html) records its 2020 LGPL change. This proposal does not assume every contributed area or separate dataset has identical terms.

## Claude-assisted labeling and generation

Use Claude to normalize categories, suggest labels and identify ambiguous records. Existing sector labels are weak supervision, not unquestionable truth: movement-cost categories can disagree with the room's apparent environment.

Prompt outline:

> Classify the player's current location from its name and description using the supplied environment taxonomy. Distinguish the current location from places seen through windows, mentioned in stories, or accessible by an exit. Keep features separate from environment. Mark uncertainty and conflicting evidence for human review. Preserve source/world/area/original-room IDs. Label synthetic records explicitly. Generate original additional descriptions for underrepresented classes, varying genre, length, wording, and explicitness. Do not copy or lightly paraphrase held-out evaluation examples.

Begin with approximately 100–200 reviewed examples per class as an experiment target, not an accuracy guarantee. Expand the corpus based on measured confusions. Deliberately include difficult examples such as an indoor room with a painted forest, a bridge over water, a cave containing a lake, and a spacecraft greenhouse. Keep feature-negative examples as well as positive examples.

Suggested JSONL record:

```json
{"id":"example-001","world":"source-world","area":"source-area","name":"The Glass Garden","description":"Trees grow beneath the station's sealed observation dome.","environment":"spacecraft","features":[],"label_source":"human-reviewed","synthetic":true,"parent_id":null,"source_url":null,"license":null}
```

Taxonomy decisions such as whether the greenhouse is `spacecraft` or `indoor` must be explicit and applied consistently. The example illustrates that decision; the model must not be asked to learn contradictory labels.

## Starting model and baseline

Recommended candidate: **SetFit with `sentence-transformers/all-MiniLM-L6-v2` and a logistic-regression head**. MiniLM produces 384-dimensional embeddings and is an English short-text encoder. SetFit adapts the encoder from labeled examples. References: [model card](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2), [SetFit](https://huggingface.co/docs/setfit/main/index), [ONNX deployment](https://huggingface.co/docs/setfit/main/tutorials/onnx).

First measure a TF-IDF plus linear-classifier baseline. Prefer the smaller/simple option if the encoder's measured benefit is negligible. Begin with the environment task; add feature heads after useful labels exist. UI translation support does not imply multilingual model support.

MiniLM defaults to truncating beyond 256 word pieces. Define preprocessing and a long-description policy (for example chunking and combining embeddings), then reproduce that exact policy in C#. Compare Python and ONNX predictions on a fixed fixture corpus before deployment. Quantization is optional and must be evaluated for accuracy and latency.

## Evaluation

Split by world/area before augmentation; keep duplicates, shared stock areas and all descendants of one source room in one partition. Use a real, human-reviewed held-out test set. Measure macro-F1, per-class precision/recall, the confusion matrix, unknown/abstention coverage, and correctness at the selected confidence threshold. Evaluate on unseen worlds and genres, not just randomly selected rooms from familiar areas.

Compare title-only versus title-plus-description, raw versus fine-tuned embeddings, and quantized versus full precision inference. Record CPU latency, memory use and model download size on supported platforms. Set release thresholds from these results; do not interpret raw scores as calibrated probabilities.

## Client integration

Provide an injected `IRoomEnvironmentClassifier` service with an optional local ONNX implementation. Keep model loading and inference off the UI thread. Bound queue length and input size; cancel requests when sessions close and reject stale results if the room description changed.

Cache by normalized input hash, preprocessing version, taxonomy version and model version. Persist manual corrections immediately. Model failure or absence leaves the mapper usable with neutral/server/manual styles. No game transcript, login credentials, or room text needs to leave the computer for inference.

A separately versioned model package contains model weights, tokenizer assets, taxonomy, palette suggestions, preprocessing specification, license notices, evaluation report and file hashes. Training tools can use Python/uv; the desktop client uses C# inference without requiring a Python installation.

## Delivery sequence

1. Extract/deduplicate records and agree the taxonomy; establish the real held-out evaluation set.
2. Review labels, build the lightweight baseline, then train the SetFit candidate.
3. Compare quality and operational cost; export and verify ONNX parity.
4. Integrate optional local classification, provenance, confidence handling and cache invalidation.
5. Collect opt-in corrections as local training candidates; sharing/training on them is a separate explicit action.
