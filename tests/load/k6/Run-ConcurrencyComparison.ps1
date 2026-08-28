<#
.SYNOPSIS
    Stage 3 — 併發控制方案比較。

.DESCRIPTION
    對每一種策略執行相同的壓測，並檢查計畫 §8 的驗收條件：

        Successful Orders <= StockBefore
        Stock After       >= 0

    理想結果：Orders = StockBefore、Stock After = 0。

    每種策略都建立一個全新商品，彼此不互相污染。

.NOTES
    本檔必須以 UTF-8 with BOM 儲存。
    PowerShell 5.1 會用系統 ANSI codepage 讀取沒有 BOM 的 .ps1，
    中文註解變亂碼後會破壞語法解析。

.EXAMPLE
    .\Run-ConcurrencyComparison.ps1
    .\Run-ConcurrencyComparison.ps1 -Stock 100 -Iterations 5000 -Vus 200
#>
param(
    [string]$BaseUrl = 'http://localhost:5080',
    [int]$Stock = 100,
    [int]$Iterations = 5000,
    [int]$Vus = 200,
    [string[]]$Strategies = @('Baseline', 'Transaction', 'Optimistic', 'Atomic')
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptPath = Join-Path $scriptDir 'concurrency-control.js'
$resultDir = Join-Path $scriptDir 'results'

if (-not (Test-Path $resultDir)) {
    New-Item -ItemType Directory -Path $resultDir | Out-Null
}

$rows = @()

foreach ($strategy in $Strategies) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $runName = "cc-$strategy-stock$Stock-$stamp"

    Write-Host ''
    Write-Host "=== $strategy / Stock = $Stock / Requests = $Iterations / VUs = $Vus ===" -ForegroundColor Cyan

    # 1. 建立全新商品
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

    # 2. 壓測
    $summaryFile = (Join-Path $resultDir "$runName.json").Replace('\', '/')

    $env:BASE_URL = $BaseUrl
    $env:PRODUCT_ID = "$targetId"
    $env:STRATEGY = $strategy
    $env:VUS = "$Vus"
    $env:ITERATIONS = "$Iterations"
    $env:SUMMARY_FILE = $summaryFile

    $started = Get-Date

    # k6 在高併發下會把連線失敗寫到 stderr；PowerShell 5.1 會把原生指令的
    # stderr 包成 ErrorRecord，在 Stop 模式下會中斷整個測試。
    # 但那是要記錄的現象，不是執行失敗。
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & k6 run --quiet $scriptPath 2>&1 | Out-Null
    $ErrorActionPreference = $previousPreference

    $elapsed = (Get-Date) - $started

    $summary = Get-Content $summaryFile -Raw | ConvertFrom-Json

    # 3. 讀回最終狀態
    $after = Invoke-RestMethod -Uri "$BaseUrl/api/products/$targetId"
    $stockAfter = [int]$after.stock

    $orders = Invoke-RestMethod -Uri "$BaseUrl/api/orders?productId=$targetId"
    $orderCount = @($orders).Count

    $consumed = $Stock - $stockAfter
    $lostUpdate = $orderCount - $consumed

    $oversold = $orderCount - $Stock
    if ($oversold -lt 0) { $oversold = 0 }

    # 計畫 §8 的驗收條件
    $correct = ($orderCount -le $Stock) -and ($stockAfter -ge 0) -and ($lostUpdate -eq 0)

    $verdict = 'FAIL'
    if ($correct) { $verdict = 'PASS' }

    $row = [PSCustomObject]@{
        Strategy      = $strategy
        StockBefore   = $Stock
        Requests      = $summary.requests
        Success       = $summary.success
        Rejected      = $summary.rejected
        Errored       = $summary.errored
        Orders        = $orderCount
        StockAfter    = $stockAfter
        LostUpdate    = $lostUpdate
        Oversold      = $oversold
        Correct       = $verdict
        DurationSec   = [math]::Round($elapsed.TotalSeconds, 1)
        Rps           = [math]::Round($summary.rps, 1)
        AvgMs         = [math]::Round($summary.durationMs.avg, 1)
        P95ms         = [math]::Round($summary.durationMs.p95, 1)
        P99ms         = [math]::Round($summary.durationMs.p99, 1)
        MaxMs         = [math]::Round($summary.durationMs.max, 1)
    }

    $rows += $row
    $row | Format-List | Out-String | Write-Host
}

Write-Host ''
Write-Host '=== Correctness ===' -ForegroundColor Cyan
$rows | Format-Table Strategy, StockBefore, Orders, StockAfter, LostUpdate, Oversold, Correct -AutoSize

Write-Host '=== Performance ===' -ForegroundColor Cyan
$rows | Format-Table Strategy, Success, Rejected, Errored, DurationSec, Rps, AvgMs, P95ms, P99ms, MaxMs -AutoSize
