# Webcent OST Converter Free

The Windows app. Anyone can open and preview an Outlook OST or PST file; signing in with a free account (name, email and a
code we email you; no password) unlocks converting to PST, EML, MSG, MBOX, PDF, HTML and CSV with no item limits. One account
works on 2 PCs. See the [project README](../README.md) for an overview and screenshots.

Everything runs on your PC. Your mail is never uploaded; the app only talks to the account server to sign you in and keep
the sign-in fresh. See [docs/PRIVACY.md](docs/PRIVACY.md) and [docs/EULA.txt](docs/EULA.txt).

## Layout

| Path | What it is |
| --- | --- |
| `src/OstConverterFree.App` | The WPF app (MVVM). |
| `src/OstConverterFree.Accounts` | Client for the account server. Verifies the signed account token **offline** (audience `ost-free`, plan `member`) and stores it encrypted with Windows data protection. |
| `tests/OstConverterFree.Tests` | Unit tests. A live-server test runs only when `OSTFREE_LIVE_URL`, `OSTFREE_LIVE_EMAIL` and `OSTFREE_LIVE_CODE` are set. |
| `tools/release.ps1` | Builds the installer and update packages with Velopack. |
| `docs/` | [EULA](docs/EULA.txt), [Privacy policy](docs/PRIVACY.md), [Releasing](docs/RELEASING.md). |

The conversion engine (`OstConverter.Core`) lives in the sibling `OST Converter` folder, so keep the two folders side by side.

## Build and test

```powershell
dotnet test OstConverterFree.sln -c Release
dotnet run --project src\OstConverterFree.App
```

Debug builds do not need the account server to start; they behave as signed out (preview only). The full developer guide is
in [../docs/BUILDING.md](../docs/BUILDING.md).

## Licence

Copyright (c) 2026 Webcent Solutions. All rights reserved; see [../LICENSE](../LICENSE).
