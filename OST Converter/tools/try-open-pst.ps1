# Dev tool: attaches one PST to the Outlook profile, counts its folders and items, and detaches it again.
# Prints OK, or the HRESULT plus the reason Outlook logged (Application event log, source "Outlook"), which for a
# rejected file names the exact structural rule that failed. Run it on a COPY; Outlook may write to the file.
param([string]$Pst)
$ErrorActionPreference = "Stop"
$started = Get-Date
$store = $null
$ns = $null
try {
  $ol = New-Object -ComObject Outlook.Application
  $ns = $ol.GetNamespace("MAPI")
  $ns.AddStore($Pst)
  foreach ($s in $ns.Stores) { if ($s.FilePath -eq $Pst) { $store = $s } }
  function Count($f) { $n = $f.Items.Count; foreach ($c in $f.Folders) { $n += Count $c }; $n }
  $root = $store.GetRootFolder()
  "OK folders=$($root.Folders.Count) items=$(Count $root)"
} catch {
  "FAILED 0x{0:X8}" -f $_.Exception.HResult
  Start-Sleep -Seconds 2
  Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Outlook'; Id=2000; StartTime=$started} -ErrorAction SilentlyContinue |
    Select-Object -First 1 | ForEach-Object { "Outlook says: " + ($_.Message -replace '(?s).*Details:\s*', '') }
} finally {
  if ($store -and $ns) { try { $ns.RemoveStore($store.GetRootFolder()) } catch { } }
}
