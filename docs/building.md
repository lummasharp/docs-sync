# Building from source

Needs the .NET 8 SDK on Windows.

```powershell
dotnet build                            # debug build
dotnet run                              # run it
.\build.ps1 -Version 1.0.0              # self-contained exe + MSI in dist\
```

`build.ps1` publishes a single self-contained `DocsSync.exe` to `publish\`, then builds the MSI with WiX (`installer\`). WiX comes from NuGet, so there's nothing else to install.

## Self-check

```powershell
.\publish\DocsSync.exe --check report.txt
```

Runs the matching, stale detection, line range, diff and template checks, then a dry run against your real config. The exit code is the number of failures.

## Layout

| File | What's in it |
|---|---|
| `Scanner.cs` | Config, note ↔ code matching, stale detection, snapshots |
| `Languages.cs` | File types and function detection per language |
| `LineDiff.cs` | Line diff and changed line ranges |
| `DocUpdater.cs` | Claude API call, prompt templates, saving notes |
| `MainWindow.xaml(.cs)` | UI, Settings, tray |
| `installer/` | WiX MSI project |
