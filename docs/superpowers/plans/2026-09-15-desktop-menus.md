# Desktop menus and session tabs

Approved design: replace the branded header with native macOS menus (in-window menus elsewhere), a compact raised toolbar, and independent session tabs with close buttons and activity indicators.

Implementation sequence:
1. Add regression coverage for opening two sessions, preserving input/history/private state while switching, routing menu actions to the active session, and closing only one connection.
2. Add SessionWorkspace to own per-tab WorkspaceControllers and synchronize saved preferences/profiles. Keep network state in the existing controllers. Tabs retain their TerminalView while switching; tool panels follow the active controller.
3. Add shared command-backed native and fallback menus: File, Edit, View, Session, Window, Help. Put Preferences in the application menu on macOS. Offer only implemented actions. Move restore panels and transcript export into menus. Use standard platform keyboard shortcuts.
4. Replace the large brand/header with a compact toolbar, connection status, and existing raised button styling. Preserve docking and restore behavior.
5. Run core and desktop regression tests, package macOS, inspect native menus and tabs in a separate test instance before updating the user's idle app. Document tested behavior and platform limits.

Constraints: Avalonia 11.3.22, Dock 11.3.12.1, .NET 10; retain user settings and active real sessions; no new package dependencies. Native macOS QA plus headless integration tests; Windows/Linux menu rendering needs platform QA. No repository exists here, so there is no commit step.
