# Releasing Webcent OST Converter Free

Maintainer checklist.

## One-time setup (the script refuses to package until these are done)
1. **Identity**: `Company`, `Copyright` and `Product` in `Directory.Build.props`; no `[bracketed]` placeholders left in
   `docs/EULA.txt` and `docs/PRIVACY.md` (have a lawyer review both).
2. **Account server**: the signing public key goes in `LicenseConfig.ProductionPublicKeyPem` and the server address in
   `LicenseConfig.ProductionApiBaseUrl`. The private key stays on the server (keep an offline backup; losing it invalidates
   every issued sign-in). The server is operated privately and is not part of the public repository.
3. **Update feed**: an https folder for the release files, set as `UpdateService.FeedUrl`.
4. **PDF library**: QuestPDF's Community licence applies to organisations under US$1M annual revenue; confirm or buy a licence.
5. **Code signing** (strongly recommended): an OV/EV certificate or Azure Trusted Signing; pass `-SignParams` or
   `-AzureTrustedSignFile`. Unsigned installers trigger SmartScreen warnings.

## Each release
```powershell
powershell -File tools\release.ps1 -Version 1.1.0 -SignParams "<signtool args>"
```
The script runs the tests, publishes self-contained for win-x64 and builds `Releases\WebcentOstConverterFree-win-Setup.exe`
plus the update packages. Keep the previous release files in the output folder so Velopack can create small delta updates.
Upload the whole folder to the feed URL; installed apps download the update in the background and offer "Restart to install".

The signing password must come from your shell or a secret store, never from a file in the repository.

## Before announcing
- Install the Setup.exe on a clean Windows 11 VM, open a real OST, convert to PST and open the PST in Outlook.
- Install version N, publish N+1 and confirm the app offers the update.
- Check SmartScreen behaviour with the signed installer.
- Update `CHANGELOG.md` and tag the release in Git.
