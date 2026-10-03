# Dev tool: validates generated .msg files against real Outlook. Windows + desktop Outlook required.
#
#   1. ostcli msg-manifest <file.ost|pst> manifest.json     (expected values as hashes/counts, no mail content)
#   2. ostcli convert <file> <outDir> --format msg --unlimited
#   3. powershell -File tools\verify-msg.ps1 -Root <outDir> -Manifest manifest.json -Mode all
#
# Mismatches of the form "attachments(N vs M)" on messages that the converter reported as having missing attachment
# data are expected: those attachments are intentionally left out. Output never includes message text.
param([string]$Root, [string]$Manifest, [int]$Max = 5, [string]$Mode = "sample")
# Opens generated .msg files in the installed Outlook and compares what Outlook reads with the source (hashes/counts only).
$ErrorActionPreference = "Stop"
$sha = [Security.Cryptography.SHA256]::Create()
function Hash12([string]$s) { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($s))) -replace '-', '').Substring(0, 12) }
function Norm([string]$s) { ($s -replace "`r`n", "`n" -replace "`r", "`n").TrimEnd() }

$rows = Get-Content $Manifest -Raw | ConvertFrom-Json
$byKey = @{}; foreach ($r in $rows) { $byKey[$r.key] = $r }

$files = Get-ChildItem $Root -Recurse -Filter *.msg
$items = foreach ($f in $files) {
  $rel = $f.FullName.Substring($Root.Length).TrimStart('\') -replace '\\', '/'
  $folder = ($rel -replace '/[^/]+$', '')
  $idx = $f.Name.Substring(0, 5)
  [pscustomobject]@{ File = $f.FullName; Key = "$folder/$idx" }
}
$pick = switch ($Mode) {
  "all"         { $items }
  "attachments" { $items | Where-Object { $byKey.ContainsKey($_.Key) -and $byKey[$_.Key].attachments -gt 0 } | Select-Object -First $Max }
  default       { $items | Select-Object -First $Max }
}

$ol = New-Object -ComObject Outlook.Application
$ns = $ol.Session
$ok = 0; $bad = 0; $err = 0; $problems = New-Object System.Collections.Generic.List[string]
foreach ($it in $pick) {
  $exp = $byKey[$it.Key]
  if (-not $exp) { $problems.Add("no manifest row for $($it.Key)"); $bad++; continue }
  try {
    $m = $ns.OpenSharedItem($it.File)
    $diff = @()
    if ((Hash12 $m.Subject) -ne $exp.subject) { $diff += "subject" }
    if ($exp.hasText -and (Hash12 (Norm $m.Body)) -ne $exp.body) { $diff += "body" }
    if ($m.Recipients.Count -ne $exp.recipients) { $diff += "recipients($($m.Recipients.Count) vs $($exp.recipients))" }
    if ($m.Attachments.Count -ne $exp.attachments) { $diff += "attachments($($m.Attachments.Count) vs $($exp.attachments))" }
    elseif ($exp.attachments -gt 0 -and $exp.attachment0) {
      $a = $m.Attachments.Item(1)
      $n = if ($a.FileName) { $a.FileName } else { $a.DisplayName }
      if ((Hash12 $n) -ne $exp.attachment0) { $diff += "attachment0name" }
    }
    $m.Close(1)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($m)
    if ($diff.Count -eq 0) { $ok++ } else { $bad++; $problems.Add("$($it.Key): " + ($diff -join ", ")) }
  } catch {
    # Never print exception text: it can contain message content. Type and COM error code only.
    $hr = if ($_.Exception.HResult) { "0x{0:X8}" -f $_.Exception.HResult } else { "" }
    $err++; $problems.Add("$($it.Key): ERROR $($_.Exception.GetType().Name) $hr")
  }
}
"checked=$($pick.Count) ok=$ok mismatched=$bad errors=$err"
$problems | Select-Object -First 15

