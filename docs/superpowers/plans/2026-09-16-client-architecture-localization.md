# Client architecture and localization implementation plan

**Goal:** Deliver world editing and secure automatic login across desktop platforms, then separate presentation state from views and localize all client-owned user-facing text.

**Architecture:** Register shared services at the Avalonia application composition root with Microsoft.Extensions.DependencyInjection. Inject credential storage through IPasswordVault; session-specific controllers retain explicit ownership of connection lifetimes. Observable view models expose properties and commands for world editing, library selection, and preferences. Views bind controls and own window, focus, and rendering operations. Core services contain no Avalonia controls. Use .NET resx resources and satellite assemblies for client-owned UI text; server transcripts and directory descriptions remain original content.

**Tech stack:** .NET 10, Avalonia 11, Microsoft.Extensions.DependencyInjection, CommunityToolkit.Mvvm, ResourceManager/resx.

**Requirements:** Preserve open sessions; no implicit restart. No plaintext password persistence, password command-line arguments, or credential logging. Credential keys bind profile identity, endpoint, TLS choice, and username. Initial UI languages: English, Spanish, French, German, Brazilian Portuguese, subject to user steering. System language fallback to English. Language changes may take effect on next launch, communicated in preferences.

## 1. Complete credential workflow

- [x] Add native macOS and Windows vaults and Linux Secret Service adapter.
- [x] Add one-shot prompt-driven login with custom patterns, timeout, manual takeover, and private writes.
- [x] Add edit action and context menu targeting the selected/clicked profile.
- [x] Register IPasswordVault once in the application DI container; remove constructor fallbacks.
- [x] Verify native vault round-trip with temporary test credentials; test rollback, removal, and endpoint binding.

## 2. Move view behavior into view models

- [x] Add CommunityToolkit.Mvvm and view-model infrastructure.
- [x] Extract profile selection, address parsing, directory lookup, validation, save/remove and login fields into ProfileEditorViewModel. Bind ProfileDialog to properties and commands; retain only window lifecycle/paste/focus glue in the view.
- [x] Extract saved-world selection and edit/connect commands into WorldLibraryViewModel.
- [x] Extract appearance/language settings and preview/save behavior into PreferencesViewModel.
- [x] Audit other views, isolate nonvisual filtering/persistence and presentation state, and document connection controller/rendering boundaries.
- [x] Run desktop tests for independent sessions, editing, and directory behavior.

## 3. Resource migration and languages

- [x] Create strongly typed resx resources for all client-owned UI, statuses, validation, accessibility, and help text. Keep protocol tokens, IDs, regexes, external content and user data separate.
- [x] Provide English fallback plus Spanish, French, German and Brazilian Portuguese satellites.
- [x] Persist Language in ClientSettings; apply chosen/system UI culture before creating views. Display language names in their native form.
- [x] Replace concatenated English sentences with complete format resources and separate singular/plural forms.
- [x] Validate resource key and placeholder parity, locale fallback, saved preference behavior, and localized dialog rendering.

## 4. Verification and delivery

- [x] Run core and desktop tests, including native-vault test where supported.
- [x] Inspect screenshots for long labels, scrolling, and fixed dialog action bars.
- [x] Build macOS app; verify resource satellites packaged.
- [x] Document architecture, resource contribution conventions, platform requirements and testing limits.

## Verification results

- Full Release suite: 82 core tests and 37 desktop tests passed.
- Additional credential rollback/removal test passed after the full suite (120 distinct tests total).
- Native macOS vault round-trip passed with a temporary credential; Windows/Linux native-store execution remains pending on those platforms.
- Localized profile/preferences screenshots rendered for all four additional languages; German profile and French preferences inspected after layout corrections.
- macOS app published successfully. All four resource satellite assemblies included.
- Resource facade generation check passed. Documentation and coding conventions added.
- Existing connected diagnostic client was left running; rebuilt app is available for the next launch.
