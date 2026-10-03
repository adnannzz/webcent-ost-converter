# Dev tool: attaches a PST, reads the first item of every folder (body length, recipient and attachment counts,
# message class) and detaches it again. Never prints text, only numbers.
param([string]$Pst)
$ErrorActionPreference = "Stop"
$ol = New-Object -ComObject Outlook.Application
$ns = $ol.GetNamespace("MAPI")
$store = $null
try {
  $ns.AddStore($Pst)
  foreach ($s in $ns.Stores) { if ($s.FilePath -eq $Pst) { $store = $s } }
  function Probe($f, $path) {
    if ($f.Items.Count -gt 0) {
      $it = $f.Items.Item(1)
      $rc = try { $it.Recipients.Count } catch { -1 }
      $ac = try { $it.Attachments.Count } catch { -1 }
      "$path : bodyLen=$(([string]$it.Body).Length) recipients=$rc attachments=$ac class=$($it.Class)"
    }
    foreach ($c in $f.Folders) { Probe $c "$path/$($c.Name)" }
  }
  Probe $store.GetRootFolder() ""
} finally {
  if ($store) { try { $ns.RemoveStore($store.GetRootFolder()) } catch { } }
}
