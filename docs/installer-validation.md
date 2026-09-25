# Installer validation

The `installer` workflow checks installation while a background executable is
running, preservation of existing files, rollback, and full/light selection.
It does not build the application or use the current user's installation.

## Known shortcut limitation

The existing `tests/installer-shortcuts.tests.ps1` suite passes on a Russian
Windows installation but fails on the English Windows CI runner when assigning
the Cyrillic target path to `WScript.Shell`'s `TargetPath`. The same failure is
reproducible locally with Chinese characters and emoji outside the system ANSI
code page. It predates the host-preserving installer change and also affects the
application's `ShortcutService`, which uses the same COM API.

The original Unicode test remains in place, with improved failure diagnostics.
It is not part of the host/variant CI gate. A separate fix should use the Unicode
`IShellLinkW` interface for shortcut creation and inspection, preserving the
existing conflict checks and user customizations. Do not replace these test
paths with ASCII or rely on optional 8.3 short names to conceal the limitation.

Diagnostic CI run: https://github.com/XYphrodite/TerminalV/actions/runs/36087610803
