# Gate B — Release build and publish

**Result:** PASS  
**Recorded:** 2026-08-25T15:52:02Z

## Release build

```powershell
dotnet build TerminalTranslator.sln --configuration Release --no-restore
```

- Exit code: 0
- Warnings: 0
- Errors: 0
- Duration: 5.43s
- Warning-as-error policy satisfied.

## Formal publish

```powershell
dotnet publish src/TerminalTranslator.Cli/TerminalTranslator.Cli.csproj --configuration Release
```

Project conventions remained `win-x64`, self-contained, single-file, and untrimmed.

- Exit code: 0
- Executable: `D:\Projects\Terminal Translator\src\TerminalTranslator.Cli\bin\Release\net10.0\win-x64\publish\tt.exe`
- Size: 74,657,433 bytes
- SHA-256: `C038B4887A494A13420FC52A8029F82ECF4EA8B66BCECF091718CDFEDA738AFD`
- Version: `1.0.0+414ba31171931f1743dc721ed7bf9cd8b69a2df7`

Safe non-install smoke passed: `--help` and `--version` exited 0, `last unexpected` exited 2, and
missing-question `ask` exited 2. No provider request, credential read, consent mutation, capture
installation, or real-user profile change was performed.
