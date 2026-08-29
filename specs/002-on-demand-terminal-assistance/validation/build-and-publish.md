# Gate B — Release build and publish

**Result:** PASS  
**Recorded:** 2026-08-27 (real-WT bootstrap remediation refresh)

## Release build

```powershell
dotnet build TerminalTranslator.sln --configuration Release --no-restore
```

- Exit code: 0
- Warnings: 0
- Errors: 0
- Duration: 2.73s
- Warning-as-error policy satisfied.

## Formal publish

```powershell
dotnet publish src/TerminalTranslator.Cli/TerminalTranslator.Cli.csproj --configuration Release
```

Project conventions remained `win-x64`, self-contained, single-file, and untrimmed.

- Exit code: 0
- Executable: `D:\Projects\Terminal Translator\src\TerminalTranslator.Cli\bin\Release\net10.0\win-x64\publish\tt.exe`
- Size: 74,661,017 bytes
- SHA-256: `39A3AD3542E9054CBDBCC5011C0074AD97D668F844715AF9119B76CDC780E2E3`
- Version: `1.0.0+5f28d445f0b0e4ffeddb28df8b822c166e942bb1`

Safe non-install smoke passed: `--help` and `--version` exited 0, `last unexpected` exited 2, and
missing-question `ask` exited 2. No provider request, credential read, consent mutation, capture
installation, or real-user profile change was performed.

## 2026-08-27 coexistence remediation rebuild

The solution was rebuilt because the production ConPTY assembly gained a testable controlled-profile
hosted-launch entry point; the default production `StartPowerShell` behavior and hosted guard were
not changed.

- Release build: exit 0, 0 warnings, 0 errors, 2.81s
- Publish: exit 0
- Executable: `D:\Projects\Terminal Translator\src\TerminalTranslator.Cli\bin\Release\net10.0\win-x64\publish\tt.exe`
- Size: 74,661,529 bytes
- SHA-256: `4F0A5EE0A6DDFC594754D1DF16C0A830C475BA642CFD49724A9B1504B5276DDF`
