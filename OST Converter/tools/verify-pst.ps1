# Dev tool: opens a generated PST in the installed Outlook and compares what Outlook reads with the source.
# Windows + desktop Outlook required. Outlook is started if needed; the PST is attached to the profile for the
# duration of the check and always detached again.
#
#   1. ostcli pst-manifest <file.ost|pst> manifest.json      (expected fingerprints as hashes/counts, no mail content)
#   2. ostcli convert <file> <outDir> --format pst --unlimited
#   3. powershell -File tools\verify-pst.ps1 -Pst <copy of generated .pst> -Manifest manifest.json
#
# Run it on a COPY of the PST: Outlook may write to the file it opens. Output never includes message text; mismatches are
# reported as folder paths and hashes only. "attachments(N vs M)" on items the converter warned about are expected.
param([string]$Pst, [string]$Manifest, [int]$MaxReport = 12)
$ErrorActionPreference = "Stop"
$sha = [Security.Cryptography.SHA256]::Create()
function Hash12([string]$s) { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($s))) -replace '-', '').Substring(0, 12) }
function Norm([string]$s) { ($s -replace "`r`n", "`n" -replace "`r", "`n").TrimEnd() }

$expected = @{}
$meta = @{}
$expectedAtt = @{}          # "folder|fingerprint" -> attachment content hashes (sorted, ';'-joined) per expected item
$attChecked = 0; $attBad = 0
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("verify-pst-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
function HashFile12([string]$p) { ([BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($p))) -replace '-', '').Substring(0, 12) }
foreach ($e in (Get-Content $Manifest -Raw | ConvertFrom-Json)) {
  if (-not $expected.ContainsKey($e.f)) { $expected[$e.f] = New-Object System.Collections.Generic.List[string] }
  $expected[$e.f].Add($e.p)
  if ($e.c) { $meta["$($e.f)|$($e.p)"] = "class=$($e.c) subjLen=$($e.l) nonAscii=$($e.n) html=$($e.h) warnings=$($e.w)" }
  if ($e.a) {
    $k = "$($e.f)|$($e.p)"
    if (-not $expectedAtt.ContainsKey($k)) { $expectedAtt[$k] = New-Object System.Collections.Generic.List[string] }
    $expectedAtt[$k].Add([string]$e.a)
  }
}

$ol = New-Object -ComObject Outlook.Application
$ns = $ol.GetNamespace("MAPI")
$store = $null
$errors = 0; $found = 0; $matched = 0
$diff = @{ subject = 0; body = 0 }
$report = New-Object System.Collections.Generic.List[string]

function Visit($folder, [string]$path) {
  $script:found += 0
  $fp = New-Object System.Collections.Generic.List[string]
  $count = $folder.Items.Count
  for ($i = 1; $i -le $count; $i++) {
    try {
      $it = $folder.Items.Item($i)
      $subj = Hash12 ([string]$it.Subject)
      $body = [string]$it.Body
      $bodyH = if ($body.Length -gt 0) { Hash12 (Norm $body) } else { "-" }
      $rc = try { [int]$it.Recipients.Count } catch { 0 }
      $ac = try { [int]$it.Attachments.Count } catch { 0 }
      $fp.Add("$subj|$bodyH|$rc|$ac")
      if ($ac -gt 0) {
        # Save every attachment and hash the bytes: this is the data-integrity check.
        $hs = New-Object System.Collections.Generic.List[string]
        for ($j = 1; $j -le $ac; $j++) {
          $att = $it.Attachments.Item($j)
          if ($att.Type -eq 5) { $hs.Add("emb") }
          else {
            $t = Join-Path $script:tmp "a.bin"
            $att.SaveAsFile($t)
            $hs.Add((HashFile12 $t))
            [IO.File]::Delete($t)
          }
        }
        $hs.Sort([StringComparer]::Ordinal)
        $mine = $hs -join ';'
        $cands = @("$path|$subj|$bodyH|$rc|$ac", "$path|$subj|-|$rc|$ac")
        $found1 = $false
        foreach ($c in $cands) {
          if ($script:expectedAtt.ContainsKey($c)) {
            $idx = $script:expectedAtt[$c].IndexOf($mine)
            if ($idx -ge 0) { $script:expectedAtt[$c].RemoveAt($idx); $found1 = $true; break }
          }
        }
        $script:attChecked++
        if (-not $found1) { $script:attBad++; if ($script:report.Count -lt $MaxReport) { $script:report.Add("$path attachment content differs for fp $subj|$rc|$ac") } }
      }
      [void][Runtime.InteropServices.Marshal]::ReleaseComObject($it)
    } catch {
      # Never print exception text: it can contain message content. Type and COM error code only.
      $hr = if ($_.Exception.HResult) { "0x{0:X8}" -f $_.Exception.HResult } else { "" }
      $script:errors++
      if ($script:report.Count -lt $MaxReport) { $script:report.Add("$path item ${i}: ERROR $($_.Exception.GetType().Name) $hr") }
    }
  }
  $script:found += $fp.Count
  $exp = if ($script:expected.ContainsKey($path)) { $script:expected[$path] } else { New-Object System.Collections.Generic.List[string] }
  $pool = @{}
  foreach ($x in $exp) { $pool[$x] = 1 + [int]$pool[$x] }
  $miss = 0
  $unmatched = New-Object System.Collections.Generic.List[string]
  foreach ($x in $fp) {
    $p = $x.Split('|')
    # A source item with no plain-text body (HTML-only mail) is recorded with "-": Outlook derives its own text, so the body is not compared.
    $wild = "$($p[0])|-|$($p[2])|$($p[3])"
    if ($pool.ContainsKey($x) -and $pool[$x] -gt 0) { $pool[$x]--; $script:matched++ }
    elseif ($pool.ContainsKey($wild) -and $pool[$wild] -gt 0) { $pool[$wild]--; $script:matched++ }
    else { $unmatched.Add($x) }
  }
  # Classify what differs for items that match no expected fingerprint exactly: pair them up by subject.
  foreach ($x in $unmatched) {
    $p = $x.Split('|')
    $cand = $pool.Keys | Where-Object { $pool[$_] -gt 0 -and $_.StartsWith($p[0] + '|') } | Select-Object -First 1
    if (-not $cand) { $miss++; $script:diff['subject']++; continue }
    $pool[$cand]--
    $q = $cand.Split('|')
    $what = @()
    if ($p[1] -ne $q[1]) { $what += 'body' }
    if ($p[2] -ne $q[2]) { $what += "recipients($($p[2]) vs $($q[2]))" }
    if ($p[3] -ne $q[3]) { $what += "attachments($($p[3]) vs $($q[3]))" }
    foreach ($w in $what) { $script:diff[$w]++ }
    if ($what.Count -eq 0) { $script:matched++ }
  }
  # Non-content hints about whatever stayed unmatched: message class, subject length, non-ASCII count of the source item.
  foreach ($k in $pool.Keys) { if ($pool[$k] -gt 0 -and $script:meta.ContainsKey("$path|$k")) { $script:report.Add("  expected-but-unmatched in ${path}: " + $script:meta["$path|$k"] + " fp=" + $k) } }
  foreach ($x in $unmatched) { $script:report.Add("  outlook-unmatched in ${path}: fp=$x") }
  $left = ($pool.Values | Measure-Object -Sum).Sum
  if ($left -gt 0 -or $miss -gt 0 -or $fp.Count -ne $exp.Count) {
    $script:report.Add("$path : outlook=$($fp.Count) expected=$($exp.Count) unmatchedOutlook=$miss unmatchedExpected=$left")
  }
  $script:expected.Remove($path) | Out-Null
  foreach ($sub in $folder.Folders) {
    $name = [string]$sub.Name
    Visit $sub ($(if ($path.Length -gt 0) { "$path/$name" } else { $name }))
  }
}

try {
  $ns.AddStore($Pst)
  foreach ($s in $ns.Stores) { if ($s.FilePath -eq $Pst) { $store = $s } }
  if (-not $store) { throw "store not found after AddStore" }
  $root = $store.GetRootFolder()
  "store opened: $($root.Folders.Count) top-level folders"
  # Folders directly below the root map to display paths without the "Top of ..." prefix.
  foreach ($f in $root.Folders) { Visit $f ([string]$f.Name) }
} finally {
  if ($store) { try { $ns.RemoveStore($store.GetRootFolder()) } catch { "warning: could not detach the store (HResult 0x{0:X8})" -f $_.Exception.HResult } }
}

$totalExpected = ($expected.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
"found=$found matched=$matched errors=$errors expectedInFoldersNotVisited=$([int]$totalExpected)"
try { [IO.Directory]::Delete($tmp, $true) } catch { }
"items with attachments checked byte-for-byte: $attChecked, differing: $attBad"
"differences by kind: " + (($diff.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ", ")
$report | Select-Object -First ($MaxReport * 3)
