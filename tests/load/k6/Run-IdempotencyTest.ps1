<#
.SYNOPSIS
    Stage 6 — Idempotency 驗證。

.DESCRIPTION
    執行計畫 §11 要求的兩項測試：

    1. Retry Test
       依序送出 N 次帶相同 Idempotency-Key 的請求。
       模擬「Server 成功但 Response Timeout，客戶端重試」。

    2. Concurrent Duplicate Test
       N 個 VU 同時送出帶相同 Key 的請求。
       模擬客戶端在超時後立刻重試、而原請求其實還在處理中。

    兩者的正確結果都是：**恰好 1 筆訂單、庫存恰好減少 1**。

.NOTES
    本檔必須以 UTF-8 with BOM 儲存（PowerShell 5.1 的編碼陷阱）。

.EXAMPLE
    .\Run-IdempotencyTest.ps1
    .\Run-IdempotencyTest.ps1 -Strategy AtomicQueued
#>
param(
    [string]$BaseUrl = 'http://localhost:5080',
    [ValidateSet('Atomic', 'AtomicQueued', 'Transaction', 'Optimistic')]
    [string]$Strategy = 'Atomic',
    [int]$SequentialRetries = 5,
    [int]$ConcurrentVus = 50,
    [int]$Stock = 100
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptPath = Join-Path $scriptDir 'idempotency.js'
$resultDir = Join-Path $scriptDir 'results'

if (-not (Test-Path $resultDir)) {
    New-Item -ItemType Directory -Path $resultDir | Out-Null
}

function New-TestProduct {
    param([string]$Label)

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'

    $body = @{
        name  = "idem-$Label-$stamp"
        price = 100
        stock = $Stock
    } | ConvertTo-Json -Compress

    $product = Invoke-RestMethod `
        -Method Post `
        -Uri "$BaseUrl/api/products" `
        -ContentType 'application/json' `
        -Body $body

    return $product.id
}

function Get-OrderCount {
    param([int]$ProductId)

    # 先賦值再 @() 包 —— Invoke-RestMethod 回傳陣列時是以單一物件
    # 寫入管線，寫成 @(Invoke-RestMethod ...).Count 會得到 1。
    $list = Invoke-RestMethod -Uri "$BaseUrl/api/orders?productId=$ProductId"
    return @($list).Count
}

function Get-Stock {
    param([int]$ProductId)

    return [int](Invoke-RestMethod -Uri "$BaseUrl/api/products/$ProductId").stock
}

# ====================================================================
# 測試 1：Retry Test（依序重送）
# ====================================================================

Write-Host ''
Write-Host "=== 測試 1：Retry Test / $Strategy / 依序重送 $SequentialRetries 次 ===" -ForegroundColor Cyan

$productId = New-TestProduct -Label "retry-$Strategy"
$key = [Guid]::NewGuid().ToString()

Write-Host "ProductId = $productId  (Stock = $Stock)"
Write-Host "Idempotency-Key = $key"
Write-Host ''

for ($i = 1; $i -le $SequentialRetries; $i++) {
    $headers = @{ 'Idempotency-Key' = $key }

    $body = @{
        userId   = 1
        quantity = 1
        strategy = $Strategy
    } | ConvertTo-Json -Compress

    try {
        $response = Invoke-WebRequest `
            -Method Post `
            -Uri "$BaseUrl/api/flash-sale/$productId" `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body $body `
            -UseBasicParsing

        $replayed = $response.Headers['Idempotency-Replayed']
        $marker = if ($replayed -eq 'true') { '  <- 回放' } else { '' }

        Write-Host ("  #{0} -> HTTP {1}{2}" -f $i, $response.StatusCode, $marker)
    }
    catch {
        Write-Host ("  #{0} -> HTTP {1}" -f $i, $_.Exception.Response.StatusCode.value__)
    }
}

Start-Sleep -Seconds 3   # 非同步策略需要等 Worker 建立訂單

$retryOrders = Get-OrderCount -ProductId $productId
$retryStock = Get-Stock -ProductId $productId

Write-Host ''
Write-Host ("  訂單數     : {0}   (預期 1)" -f $retryOrders)
Write-Host ("  庫存       : {0}   (預期 {1})" -f $retryStock, ($Stock - 1))

$retryPass = ($retryOrders -eq 1) -and ($retryStock -eq ($Stock - 1))
Write-Host ("  結果       : {0}" -f $(if ($retryPass) { 'PASS' } else { 'FAIL' })) `
    -ForegroundColor $(if ($retryPass) { 'Green' } else { 'Red' })

# ====================================================================
# 測試 2：Concurrent Duplicate Test（同時重送）
# ====================================================================

Write-Host ''
Write-Host "=== 測試 2：Concurrent Duplicate / $Strategy / $ConcurrentVus 個同時請求 ===" -ForegroundColor Cyan

$productId2 = New-TestProduct -Label "concurrent-$Strategy"
$key2 = [Guid]::NewGuid().ToString()

Write-Host "ProductId = $productId2  (Stock = $Stock)"
Write-Host "Idempotency-Key = $key2"

$summaryFile = (Join-Path $resultDir "idem-$Strategy-$(Get-Date -Format 'yyyyMMdd-HHmmss-fff').json").Replace('\', '/')

$env:BASE_URL = $BaseUrl
$env:PRODUCT_ID = "$productId2"
$env:STRATEGY = $Strategy
$env:IDEMPOTENCY_KEY = $key2
$env:VUS = "$ConcurrentVus"
$env:SUMMARY_FILE = $summaryFile

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& k6 run --quiet $scriptPath 2>&1 | Out-Null
$ErrorActionPreference = $previousPreference

$summary = Get-Content $summaryFile -Raw | ConvertFrom-Json

Start-Sleep -Seconds 3

$concurrentOrders = Get-OrderCount -ProductId $productId2
$concurrentStock = Get-Stock -ProductId $productId2

Write-Host ''
Write-Host ("  請求數     : {0}" -f $summary.requests)
Write-Host ("  受理 (2xx) : {0}   其中回放 {1}" -f $summary.accepted, $summary.replayed)
Write-Host ("  409 處理中 : {0}" -f $summary.conflict)
Write-Host ("  其他錯誤   : {0}" -f $summary.errored)
Write-Host ("  訂單數     : {0}   (預期 1)" -f $concurrentOrders)
Write-Host ("  庫存       : {0}   (預期 {1})" -f $concurrentStock, ($Stock - 1))

$concurrentPass = ($concurrentOrders -eq 1) -and
                  ($concurrentStock -eq ($Stock - 1)) -and
                  ($summary.errored -eq 0)

Write-Host ("  結果       : {0}" -f $(if ($concurrentPass) { 'PASS' } else { 'FAIL' })) `
    -ForegroundColor $(if ($concurrentPass) { 'Green' } else { 'Red' })

Write-Host ''
[PSCustomObject]@{
    Strategy         = $Strategy
    RetryOrders      = $retryOrders
    RetryStock       = $retryStock
    RetryPass        = $retryPass
    ConcurrentReqs   = $summary.requests
    ConcurrentOrders = $concurrentOrders
    ConcurrentStock  = $concurrentStock
    Accepted         = $summary.accepted
    Replayed         = $summary.replayed
    Conflict         = $summary.conflict
    ConcurrentPass   = $concurrentPass
}
