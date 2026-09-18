# Custom themes and sectioned settings

## Goal
Give settings a persistent category sidebar and let users create, edit, select and delete named palettes without source changes.

## Design
Use General, Appearance, Terminal and Input sections. Preserve existing MVVM settings-store DI. Built-ins remain immutable starting points; duplicate a preset to create a custom palette. Persist custom definitions in the existing client_settings payload in SQLite, preserving world profiles. Include base UI colors and advanced control/map/editor colors, live preview, hex inputs and native Avalonia color pickers. Explicitly control whether supplied world themes take precedence. Save commits; Cancel discards all draft palette edits and restores appearance.

## Work
- [x] Add validated custom theme definitions and preset catalog to Core; test SQLite round trips and invalid data.
- [x] Resolve personal/world theme precedence and apply color overrides.
- [x] Build theme editing view models and sidebar settings view; localize all labels in five languages.
- [x] Test create/edit/delete/save/cancel, world precedence and rendered settings navigation.
- [x] Inspect rendered settings, run relevant suites, rebuild app and document use.
