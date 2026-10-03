# Building and developing

## Requirements

- Windows 10 or 11 (the app is WPF and uses Windows data protection)
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Optional: desktop Outlook, only for the Outlook validation scripts in `OST Converter/tools`

The repository has two solutions. The app references the engine in the `OST Converter` folder, so keep both folders side by
side as they are in the repository.

| Solution | Contents |
| --- | --- |
| `OST Converter/OstConverter.Engine.sln` | engine (`Core`), command-line tool, engine tests |
| `OST Converter Free/OstConverterFree.sln` | account client, WPF app, tests (uses `OstConverter.Core` from the folder above) |

## Build, run, test

```powershell
dotnet build "OST Converter Free\OstConverterFree.sln" -c Debug
dotnet run --project "OST Converter Free\src\OstConverterFree.App"
dotnet test "OST Converter Free\OstConverterFree.sln" -c Release

dotnet test "OST Converter\OstConverter.Engine.sln" -c Release
```

The tests build their own input files; they never need a real mailbox. A few take a while because they write and validate
multi-megabyte PSTs.

### Debug builds and the account server

Debug builds talk to a **development** account server and trust a development public key (`LicenseConfig.DevPublicKeyPem`,
`http://localhost:8787`). Release builds use the production server and key. You do not need a server to work on the engine or
the UI: without one the app simply behaves as signed out (preview only).

| Environment variable (Debug builds only) | Effect |
| --- | --- |
| `OSTCONV_API_URL` | Use a different account server address. |
| `OSTFREE_ACCOUNT_STORE` | Keep the signed-in account in this file instead of your real one. |

## Try it without real mail

The command-line tool writes a small mailbox of invented messages:

```powershell
dotnet run --project "OST Converter\src\OstConverter.Cli" -- demo demo.pst
dotnet run --project "OST Converter\src\OstConverter.Cli" -- demo.pst --count      # folder tree with counts
dotnet run --project "OST Converter\src\OstConverter.Cli" -- convert demo.pst out --format eml --unlimited
```

Run `OstConverter.Cli` with no arguments for the list of developer commands (`pst-validate`, `pst-dump`, ...).

## UI screenshots without a display session

Debug builds of the app can render screens to PNG files, which is how the pictures in `assets/screenshots` are made:

```powershell
$env:OST_SNAP_DIR      = "shots"        # enables the mode; PNGs are written here
$env:OST_SNAP_FILE     = "demo.pst"     # file to open
$env:OST_SNAP_DEMOLIST = "1"            # replaces "Found on this PC" with invented entries
$env:OST_SNAP_ACCOUNT  = "1"            # also capture the account window
$env:OSTFREE_ACCOUNT_STORE = "shots\throwaway.dat"   # keep your real account out of the picture
dotnet run --project "OST Converter Free\src\OstConverterFree.App"
```

Only use invented data (see `demo` above) for pictures you publish.

## Brand assets

`assets/brand/webcent-source.svg` is the logo. `OST Converter/tools/make-icons.ps1` (and `make-arrow.ps1` for the arrow's
transparency) render it into `logo-*.png` and the multi-size `app.ico`. Copy `app.ico` to the app project and `logo-256.png`
to its `Assets/logo.png`. The colour palette (blue `#1A87E2`, magenta `#E21A74`, navy `#0B2545`) is defined at the top of
`App.xaml`.

```powershell
powershell -STA -File "OST Converter\tools\make-icons.ps1"
```

## Releasing

See [OST Converter Free/docs/RELEASING.md](../OST%20Converter%20Free/docs/RELEASING.md). In short, `tools/release.ps1` runs the
tests, publishes a self-contained win-x64 build and packs it with Velopack into `WebcentOstConverterFree-win-Setup.exe` plus
the update packages.

## Conventions

- C# with nullable reference types on; implicit usings.
- UI logic goes in view models (CommunityToolkit.Mvvm), not code-behind.
- Anything that parses an untrusted file must treat damage as a per-item error, never a crash.
- Never log or print message content; use counts, folder paths and hashes in diagnostics.
- Tests may only use generated data. Do not commit OST or PST files.
