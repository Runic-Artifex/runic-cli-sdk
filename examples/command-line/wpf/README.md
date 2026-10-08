# WPF application with a CLI

See the guide: [Adding a CLI to a WPF application](../../../docs/guides/command-line/wpf.md).

| Project | Target | Role |
| --- | --- | --- |
| `ReportApp.Core` | net10.0 | Existing application services; no Runic, no UI. |
| `ReportApp.Commands` | net10.0 | Command handlers, `CommandApp` factory, launch classifier. |
| `ReportApp.Cli` | net10.0 | Console executable `reportcli` (recommended CLI entry point). |
| `ReportApp.Wpf` | net10.0-windows | WPF app; built by Windows CI only. |
| `Tests` | net10.0 | In-memory CLI and classification checks. |
