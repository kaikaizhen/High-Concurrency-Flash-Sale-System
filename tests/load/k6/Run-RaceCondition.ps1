<#
.SYNOPSIS
    Stage 2 — Race Condition 壓測執行器。

.DESCRIPTION
    每一個併發等級都建立一個**全新商品**，確保庫存與訂單數乾淨可比對。

    流程：
        建立商品 (Stock = N)
            -> k6 送出 V 個同時請求
            -> 讀回 Stock 與 Order 數量
            -> 計算超賣量

    超賣量 = Success - StockBefore
    只要大於 0，就代表賣出的數量超過庫存。

.EXAMPLE
    .\Run-RaceCondition.ps1
    .\Run-RaceCondition.ps1 -Stock 100 -Vus 10,100,500,1000
#>
param(
    [string]$BaseUrl = 'http://localhost:5080',
    [int]$Stock = 100,
    [int[]]$Vus = @(10, 100, 500, 1000)
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptPath = Join-Path $scriptDir 'race-condition.js'
$resultDir = Join-Path $scriptDir 'results'

if (-not (Test-Path $resultDir)) {
    New-Item -ItemType Directory -Path $resultDir | Out-Null
}

$rows = @()

foreach ($vu in $Vus) {
    # 商品名稱有唯一性限制，時間戳需精確到毫秒，
    # 否則同一秒內連續執行兩個併發等級會撞名。
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $runName = "race-vu$vu-stock$Stock-$stamp"

    Write-Host ''
    Write-Host "=== Concurrent Users = $vu / Stock = $Stock ===" -ForegroundColor Cyan

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
    $env:VUS = "$vu"
    $env:SUMMARY_FILE = $summaryFile

    # 高併發下 k6 會把連線失敗寫到 stderr。
    # PowerShell 5.1 會把原生指令的 stderr 包成 ErrorRecord，
    # 在 $ErrorActionPreference = 'Stop' 下會直接中斷整個測試 ——
    # 但「連線被拒絕」正是我們要記錄的現象，不是執行失敗。
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & k6 run --quiet $scriptPath 2>&1 | Out-Null
    $ErrorActionPreference = $previousPreference

    $summary = Get-Content $summaryFile -Raw | ConvertFrom-Json

    # 3. 讀回最終狀態
    $after = Invoke-RestMethod -Uri "$BaseUrl/api/products/$targetId"
    $stockAfter = [int]$after.stock

    $orders = Invoke-RestMethod -Uri "$BaseUrl/api/orders?productId=$targetId"
    $orderCount = @($orders).Count

    # 庫存實際被扣掉幾件
    $consumed = $Stock - $stockAfter

    # Lost Update：建立了訂單，庫存卻沒被扣到
    # （A、B 讀到同一個值，B 的寫入覆蓋掉 A 的寫入）
    $lostUpdate = $orderCount - $consumed

    # Oversold：賣出的數量超過原始庫存
    $oversold = $orderCount - $Stock
    if ($oversold -lt 0) { $oversold = 0 }

    $row = [PSCustomObject]@{
        Vus          = $vu
        StockBefore  = $Stock
        Requests     = $summary.requests
        Success      = $summary.success
        Rejected     = $summary.rejected
        Errored      = $summary.errored
        Orders       = $orderCount
        StockAfter   = $stockAfter
        StockConsumed = $consumed
        LostUpdate   = $lostUpdate
        Oversold     = $oversold
        P95ms        = [math]::Round($summary.durationMs.p95, 1)
        P99ms        = [math]::Round($summary.durationMs.p99, 1)
    }

    $rows += $row
    $row | Format-List | Out-String | Write-Host
}

Write-Host ''
Write-Host '=== Summary ===' -ForegroundColor Cyan
$rows | Format-Table -AutoSize
