param(
  [string]$BaseUrl = "http://localhost:5189",
  [int]$SourceFirmaKodu = 4,
  [int]$SourceDonemKodu = 3,
  [int]$TargetFirmaKodu = 4,
  [int]$TargetDonemKodu = 3,
  [long]$TargetSubeKey = 1,
  [long]$TargetDepoKey = 1,
  [int]$Count = 2000,
  [int]$ParallelUsers = 2
)

$ErrorActionPreference = "Stop"

function Invoke-UserRun([int]$userIndex) {
  Write-Host "User#$userIndex: fetching invoice list..."
  $listReq = @{
    firmaKodu = $SourceFirmaKodu
    donemKodu = $SourceDonemKodu
    sourceSubeKey = $null
    sourceDepoKey = $null
    onlyDistributable = $false
    onlyNonDistributable = $false
    filters = ""
    limit = $Count
    offset = 0
  } | ConvertTo-Json

  $rows = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/invoices/list" -ContentType "application/json" -Body $listReq
  if (-not $rows) { throw "Invoice list returned empty." }

  $keys = @()
  foreach ($r in $rows) {
    $k = $r.key
    if ($null -ne $k) {
      $parsed = 0L
      if ([Int64]::TryParse([string]$k, [ref]$parsed) -and $parsed -gt 0) { $keys += $parsed }
    }
    if ($keys.Count -ge $Count) { break }
  }
  if ($keys.Count -eq 0) { throw "No numeric invoice keys found in list payload." }

  $payload = @{
    sourceFirmaKodu = $SourceFirmaKodu
    sourceDonemKodu = $SourceDonemKodu
    sourceSubeKey = $null
    sourceDepoKey = $null
    targetFirmaKodu = $TargetFirmaKodu
    targetDonemKodu = $TargetDonemKodu
    targetSubeKey = $TargetSubeKey
    targetDepoKey = $TargetDepoKey
    invoices = @($keys | ForEach-Object { @{ sourceInvoiceKey = $_; selectedKalemKeys = @(); selectedLineSnapshots = @() } })
  } | ConvertTo-Json -Depth 6

  Write-Host "User#$userIndex: sending transfer request invoices=$($keys.Count)..."
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $res = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/fatura-aktar" -ContentType "application/json" -Body $payload
  $sw.Stop()

  Write-Host "User#$userIndex DONE in $($sw.ElapsedMilliseconds)ms -> success=$($res.success) total=$($res.total) ok=$($res.successCount) fail=$($res.failedCount) durationMs=$($res.durationMs)"
}

Write-Host "Starting load test. base=$BaseUrl parallelUsers=$ParallelUsers count=$Count"

$jobs = @()
for ($i=1; $i -le $ParallelUsers; $i++) {
  $jobs += Start-Job -ScriptBlock ${function:Invoke-UserRun} -ArgumentList $i
}

$jobs | Wait-Job | Out-Null
$jobs | Receive-Job
$jobs | Remove-Job | Out-Null

