# Builds a release: tests -> self-contained publish -> Velopack installer and update packages.
#
#   powershell -File tools\release.ps1                      # packages 1.0.0 (version from Directory.Build.props)
#   powershell -File tools\release.ps1 -Version 1.1.0       # new version; keep the previous release files in -OutDir
#                                                           #   so Velopack can build small delta updates
#   -SignParams "/f cert.pfx /p <password> /fd sha256 /tr http://timestamp.digicert.com /td sha256"
#   -AzureTrustedSignFile metadata.json                      # alternative: Azure Trusted Signing
#
# Without a signing option the installer is unsigned and Windows SmartScreen will warn users. The password in
# -SignParams must come from your own shell or secret store, never from a file in the repo.
#
# Output (default .\Releases): WebcentOstConverterFree-win-Setup.exe (what customers download), WebcentOstConverterFree-<version>-full.nupkg,
# optional -delta.nupkg and RELEASES/releases.*.json: upload the whole folder to the update URL in UpdateService.cs.
param(
  [string]$Version,
  [string]$OutDir = "Releases",
  [string]$SignParams,
  [string]$AzureTrustedSignFile,
  [switch]$SkipTests,
  [switch]$AllowPlaceholders   # packaging dry run only: lets placeholder names/keys/URLs through
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Prop([string]$name) { (dotnet msbuild src/OstConverterFree.App -getProperty:$name -nologo).Trim() }
if (-not $Version) { $Version = Prop "Version" }
$product = Prop "Product"; $company = Prop "Company"

# --- Refuse to ship placeholders -------------------------------------------------------------------------------------
$problems = @()
if ($company -match "Your Company") { $problems += "Company/Copyright in Directory.Build.props are still placeholders." }
if ((Get-Content src/OstConverterFree.App/UpdateService.cs -Raw) -match 'FeedUrl = "[^"]*example\.com') { $problems += "UpdateService.FeedUrl is still the example.com placeholder (updates would be off)." }
$lic = Get-Content src/OstConverterFree.Accounts/LicenseConfig.cs -Raw
# (the account server shares the production licence server and key)
if ($lic -match 'ProductionApiBaseUrl = "[^"]*example\.com') { $problems += "LicenseConfig.ProductionApiBaseUrl is still the placeholder." }
if ($lic -match 'ProductionPublicKeyPem = DevPublicKeyPem') { $problems += "LicenseConfig.ProductionPublicKeyPem is still the development key." }
if ((Get-Content docs/EULA.txt -Raw) -match "\[Company Name\]|\[date\]|\[country|\[place|\[postal address") { $problems += "docs/EULA.txt still has [bracketed] placeholders." }
if ((Get-Content docs/PRIVACY.md -Raw) -match "\[Company Name\]|\[date\]|\[support email\]") { $problems += "docs/PRIVACY.md still has [bracketed] placeholders." }
if ($problems.Count -gt 0) {
  $problems | ForEach-Object { Write-Warning $_ }
  if (-not $AllowPlaceholders) { throw "Release blocked: fix the items above (or use -AllowPlaceholders for a packaging dry run)." }
}
if (-not $SignParams -and -not $AzureTrustedSignFile) { Write-Warning "No signing option given: the installer will be UNSIGNED (SmartScreen warning)." }

# --- Tests -------------------------------------------------------------------------------------------------------------
if (-not $SkipTests) {
  dotnet test -c Release --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

# --- Publish -----------------------------------------------------------------------------------------------------------
$publish = Join-Path $root "artifacts\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force -Confirm:$false }
dotnet publish src/OstConverterFree.App -c Release -r win-x64 --self-contained true -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
Copy-Item docs/EULA.txt, THIRD-PARTY-NOTICES.txt, docs/PRIVACY.md $publish

# --- Package -----------------------------------------------------------------------------------------------------------
$notes = Join-Path $root "artifacts\release-notes.md"
New-Item -ItemType Directory -Force (Split-Path $notes) | Out-Null
if (-not (Test-Path $notes)) { "Release $Version" | Set-Content $notes }
$args2 = @("vpk", "pack", "--packId", "WebcentOstConverterFree", "--packVersion", $Version, "--packDir", $publish, "--mainExe", "WebcentOstConverterFree.exe",
  "--packTitle", $product, "--packAuthors", $company, "--outputDir", $OutDir, "--instLicense", "docs/EULA.txt",
  "--releaseNotes", $notes, "--shortcuts", "Desktop,StartMenuRoot", "--runtime", "win-x64")
if (Test-Path "src/OstConverterFree.App/app.ico") { $args2 += @("--icon", "src/OstConverterFree.App/app.ico") }
if ($SignParams) { $args2 += @("--signParams", $SignParams) }
if ($AzureTrustedSignFile) { $args2 += @("--azureTrustedSignFile", $AzureTrustedSignFile) }
dotnet @args2
if ($LASTEXITCODE -ne 0) { throw "Packaging failed." }

"`nDone. Upload the contents of '$OutDir' to the update URL; give customers WebcentOstConverterFree-win-Setup.exe."

