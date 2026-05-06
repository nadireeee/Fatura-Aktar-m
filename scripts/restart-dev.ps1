$ErrorActionPreference = 'SilentlyContinue'
Get-Process -Name 'DiaErpIntegration.API' | Stop-Process -Force
foreach ($p in @(5189, 5178)) {
  $conns = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
  foreach ($c in $conns) {
    if ($c.OwningProcess -gt 0) { Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue }
  }
}
Start-Sleep -Seconds 2
Write-Host 'Stopped listeners on 5189/5178 and DiaErpIntegration.API'
