<#
.SYNOPSIS
    Stage 5 — 同步 / 非同步搶購比較，並觀察佇列長度變化。

.DESCRIPTION
    與 Run-ConcurrencyComparison.ps1 的差別：非同步策略在 k6 結束時
    訂單通常還沒建立完，因此這裡會在壓測結束後**持續取樣佇列長度**，
    直到消化完畢，記錄整條曲線。

    這條曲線就是「削峰填谷」：
      - 壓測期間佇列急速上升（峰）
      - 之後由 Worker 以自己的速度慢慢下降（谷）
      - API 的回應時間完全不受 Worker 速度影響

.NOTES
    本檔必須以 UTF-8 with BOM 儲存（PowerShell 5.1 的編碼陷阱）。

.EXAMPLE
    .\Run-QueueTest.ps1 -Strategy Atomic       -Stock 5000 -Iterations 5000
    .\Run-QueueTest.ps1 -Strategy AtomicQueued -Stock 5000 -Iterations 5000
#>
param(
    [string]$BaseUrl = 'http://localhost:5080',
    [ValidateSet('Atomic', 'AtomicQueued')]
    [string]$Strategy = 'AtomicQueued',
    [int]$Stock = 5000,
    [int]$Iterations = 5000,
    [int]$Vus = 200,

    # 壓測結束後最多再等多久讓佇列消化完（秒）
    [int]$DrainTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptPath = Join-Path $scriptDir 'concurrency-control.js'
$resultDir = Join-Path $scriptDir 'results'

if (-not (Test-Path $resultDir)) {
    New-Item -ItemType Directory -Path $resultDir | Out-Null
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$runName = "queue-$Strategy-$stamp"

Write-Host ''
Write-Host "=== $Strategy / Stock = $Stock / Requests = $Iterations / VUs = $Vus ===" -ForegroundColor Cyan

$createBody = @{
    name  = $runName
    price = 100
    stock = $Stock
} | ConvertTo-Json -Compress

$product = Invoke-RestMethod `
    -Method Post `
    -Uri "$BaseUrl/api/products" `
    -ContentType 'application/json' `
    -Body $createBody

$targetId = $product.id
Write-Host "ProductId = $targetId"

Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/diagnostics/metrics/reset" | Out-Null

$summaryFile = (Join-Path $resultDir "$runName.json").Replace('\', '/')

$env:BASE_URL = $BaseUrl
$env:PRODUCT_ID = "$targetId"
$env:STRATEGY = $Strategy
$env:VUS = "$Vus"
$env:ITERATIONS = "$Iterations"
$env:SUMMARY_FILE = $summaryFile

$started = Get-Date

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& k6 run --quiet $scriptPath 2>&1 | Out-Null
$ErrorActionPreference = $previousPreference

$apiElapsed = (Get-Date) - $started

$summary = Get-Content $summaryFile -Raw | ConvertFrom-Json

# ---- 取樣佇列長度直到消化完畢 ----
Write-Host ''
Write-Host '--- 佇列消化曲線 ---' -ForegroundColor Cyan

$peakQueue = 0
$drainStart = Get-Date
$samples = @()

while ($true) {
    $queue = Invoke-RestMethod -Uri "$BaseUrl/api/diagnostics/queue"
    # 必須先賦值再用 @() 包。
    # Invoke-RestMethod 回傳陣列時是以「單一物件」寫入管線，
    # 寫成 @(Invoke-RestMethod ...).Count 會得到 1 而不是元素個數 ——
    # 而且不會報錯，只會安靜地給出錯誤的數字。
    $orderList = Invoke-RestMethod -Uri "$BaseUrl/api/orders?productId=$targetId"
    $orders = @($orderList).Count
    $elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)

    if ($queue.pendingOrders -gt $peakQueue) {
        $peakQueue = $queue.pendingOrders
    }

    $samples += [PSCustomObject]@{
        ElapsedSec = $elapsed
        Queue      = $queue.pendingOrders
        Orders     = $orders
    }

    Write-Host ("  t={0,6}s  佇列={1,6}  訂單={2,6}" -f $elapsed, $queue.pendingOrders, $orders)

    if ($queue.pendingOrders -eq 0 -and $orders -ge $summary.success) {
        break
    }

    if (((Get-Date) - $drainStart).TotalSeconds -gt $DrainTimeoutSeconds) {
        Write-Warning "等待佇列消化逾時（$DrainTimeoutSeconds 秒）。"
        break
    }

    Start-Sleep -Seconds 2
}

$totalElapsed = (Get-Date) - $started

$after = Invoke-RestMethod -Uri "$BaseUrl/api/products/$targetId"
$finalOrderList = Invoke-RestMethod -Uri "$BaseUrl/api/orders?productId=$targetId"
$finalOrders = @($finalOrderList).Count
$metrics = Invoke-RestMethod -Uri "$BaseUrl/api/diagnostics/metrics"

$row = [PSCustomObject]@{
    Strategy         = $Strategy
    StockBefore      = $Stock
    Requests         = $summary.requests
    Success          = $summary.success
    Rejected         = $summary.rejected
    Errored          = $summary.errored
    OrdersFinal      = $finalOrders
    StockAfter       = [int]$after.stock
    # API 回應完畢的時間 —— 使用者感受到的
    ApiElapsedSec    = [math]::Round($apiElapsed.TotalSeconds, 1)
    # 訂單全部落地的時間 —— 系統真正完成工作的時間
    TotalElapsedSec  = [math]::Round($totalElapsed.TotalSeconds, 1)
    PeakQueueLength  = $peakQueue
    ApiRps           = [math]::Round($summary.rps, 1)
    AvgMs            = [math]::Round($summary.durationMs.avg, 1)
    P95ms            = [math]::Round($summary.durationMs.p95, 1)
    P99ms            = [math]::Round($summary.durationMs.p99, 1)
    DbCommands       = $metrics.dbCommands
}

Write-Host ''
$row | Format-List | Out-String | Write-Host

$samples | Export-Csv -Path (Join-Path $resultDir "$runName-samples.csv") -NoTypeInformation
$row
