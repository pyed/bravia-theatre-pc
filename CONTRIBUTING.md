# Contributing

Thanks for helping improve BRAVIA Theatre PC. Keep changes focused, testable, and safe for users' Sony credentials.

## Development setup

- Windows 10 or Windows 11
- .NET SDK 10.0.302 or a compatible 10.0 patch selected by `global.json`
- WebView2 Runtime for exercising the sign-in UI

Restore, format, build, and test from the repository root:

```powershell
dotnet restore BraviaTheatrePC.sln
dotnet format BraviaTheatrePC.sln
dotnet build BraviaTheatrePC.sln -c Release --no-restore -warnaserror
dotnet test BraviaTheatrePC.sln -c Release --no-build
```

## Pull requests

1. Branch from `main` and keep each pull request limited to one coherent change.
2. Add regression coverage for protocol parsing, state transitions, or lifecycle behavior that changes.
3. Use only synthetic credentials and packet fixtures. Never copy values from `session_keys.json`, logs, browser traffic, or a real device into source or tests.
4. Run the Release build and full test suite before requesting review.
5. Explain compatibility or security tradeoffs in the pull-request description.

The CI workflow also verifies formatting, scans for committed secrets, audits NuGet dependencies, and smoke-tests both publish modes.

## Security reports

Do not disclose suspected vulnerabilities or exposed credentials in a public issue. Follow [SECURITY.md](SECURITY.md) for private reporting and containment guidance.
