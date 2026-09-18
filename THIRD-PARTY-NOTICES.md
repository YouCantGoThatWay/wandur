# Third-party components

Wandur is MIT licensed. Dependencies keep their own copyright and license terms.

- [Avalonia](https://github.com/AvaloniaUI/Avalonia): MIT; desktop UI, Fluent theme and headless testing.
- [Iciclecreek.Avalonia.Terminal](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal): MIT; dedicated terminal control.
- [XTerm.NET](https://github.com/tomlm/XTerm.NET): MIT; terminal emulation and screen buffer.
- [Porta.Pty](https://github.com/tomlm/Porta.Pty): MIT; transitive terminal dependency (Wandur does not launch a PTY).
- [Dock](https://github.com/wieslawsoltes/Dock): MIT; docking models, controls and theme.
- [Inter](https://github.com/rsms/inter): SIL Open Font License 1.1; bundled through Avalonia.Fonts.Inter.
- [SkiaSharp](https://github.com/mono/SkiaSharp) and [HarfBuzzSharp](https://github.com/mono/SkiaSharp): rendering/text-shaping bindings and native dependencies, with their included notices.
- [xUnit.net](https://github.com/xunit/xunit): Apache-2.0; test framework.
- [Microsoft VSTest](https://github.com/microsoft/vstest): MIT; test infrastructure.
- [ONNX Runtime](https://github.com/microsoft/onnxruntime): MIT; local inference for the optional room-terrain classifier.
- [Microsoft.ML.Tokenizers](https://github.com/dotnet/machinelearning): MIT; WordPiece tokenization for that classifier.
- Room-terrain model package (downloaded on demand, not bundled): fine-tuned [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2), Apache-2.0, from [room-classifier](https://github.com/YouCantGoThatWay/room-classifier); training-data notices ship inside the package's `LICENSES.md`. `tests/Fixtures/room-classifier-parity.json` contains 20 tbaMUD room descriptions (LGPL, CircleMUD/DikuMUD 2020 relicense) used as parity test vectors.

This is an attribution index, not a replacement for dependency license files. Full transitive versions and hashes are recorded in each project's `packages.lock.json`. Redistribution should include the upstream license notices shipped with those packages.
