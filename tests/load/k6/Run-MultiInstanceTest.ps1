<#
.SYNOPSIS
    Stage 8 — 多 Instance 驗證。

.DESCRIPTION
    依序執行四項檢查：

      1. 負載平衡      請求是否真的分散到多台
      2. Stateless     同一個邏輯流程跨機器接手是否仍然正確
      3. 共用狀態      計數器 / Single Flight / 限流額度是否跨機器一致
      4. Kill Instance 殺掉一台之後，服務是否仍然可用

    第 3 項是重點：這三樣東西在 Stage 4 / 7 都是行程內狀態，
    單一 Instance 時完全正確、多 Instance 時全部失準。
    切換 SharedState__* 開關重跑，可以看到「壞掉」與「修好」的差異。

.NOTES
    本檔必須以 UTF-8 with BOM 儲存（PowerShell 5.1 的編碼陷阱）。

.EXAMPLE
    .\Run-MultiInstanceTest.ps1
    .\Run-MultiInstanceTest.ps1 -SkipKillTest
#>
param(
    [string]$BaseUrl = 'http://localhost:8080',
    [int]$LoadBalanceSamples = 30,
    [int]$SingleFlightConcurrency = 60,
    [int]$RateLimitBurstRate = 100,
    [switch]$SkipKillTest
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$resultDir = Join-Path $scriptDir 'results'

if (-not (Test-Path $resultDir)) {
    New-Item -ItemType Directory -Path $resultDir | Out-Null
}

function Get-InstanceOf {
    param([string]$Path = '/api/products')

    $response = Invoke-WebRequest -Uri "$BaseUrl$Path" -UseBasicParsing
    return $response.Headers[[string]'X-Instance-Id']
}

function New-TestProduct {
    param([int]$Stock = 1000000)

    $body = @{
        name  = "multiinstance-$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')-$(Get-Random)"
        price = 100
        stock = $Stock
    } | ConvertTo-Json -Compress

    return (Invoke-RestMethod `
            -Method Post `
            -Uri "$BaseUrl/api/products" `
            -ContentType 'application/json' `
            -Body $body).id
}

# ====================================================================
# 1. 負載平衡
# ====================================================================
Write-Host ''
Write-Host '=== 1. 負載平衡 ===' -ForegroundColor Cyan

$hits = @{}
for ($i = 0; $i -lt $LoadBalanceSamples; $i++) {
    $instance = Get-InstanceOf
    if ($instance) {
        if (-not $hits.ContainsKey($instance)) { $hits[$instance] = 0 }
        $hits[$instance]++
    }
}

$hits.GetEnumerator() | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-8} {1,4} 次" -f $_.Key, $_.Value)
}

$instanceCount = $hits.Keys.Count
Write-Host ("  參與的 Instance 數 : {0}" -f $instanceCount) `
    -ForegroundColor $(if ($instanceCount -gt 1) { 'Green' } else { 'Red' })

# ====================================================================
# 2. Stateless：跨機器接手
# ====================================================================
Write-Host ''
Write-Host '=== 2. Stateless（同一流程跨機器接手）===' -ForegroundColor Cyan

$productId = New-TestProduct -Stock 100
$key = [Guid]::NewGuid().ToString()

# 第一次搶購（帶 Idempotency-Key）
$body = @{ userId = 1; quantity = 1 } | ConvertTo-Json -Compress
$headers = @{ 'Idempotency-Key' = $key; 'X-User-Id' = 'stateless-test' }

$first = Invoke-WebRequest `
    -Method Post `
    -Uri "$BaseUrl/api/flash-sale/$productId" `
    -Headers $headers `
    -ContentType 'application/json' `
    -Body $body `
    -UseBasicParsing

$firstInstance = $first.Headers[[string]'X-Instance-Id']
Write-Host ("  第一次搶購 -> {0}  (HTTP {1})" -f $firstInstance, $first.StatusCode)

# 重送同一個 Key，很可能落到不同機器
$replayInstances = @()
$replayedCount = 0

for ($i = 0; $i -lt 6; $i++) {
    $retry = Invoke-WebRequest `
        -Method Post `
        -Uri "$BaseUrl/api/flash-sale/$productId" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $body `
        -UseBasicParsing

    $replayInstances += $retry.Headers[[string]'X-Instance-Id']

    if ($retry.Headers[[string]'Idempotency-Replayed'] -eq 'true') {
        $replayedCount++
    }
}

$distinctReplayInstances = ($replayInstances | Select-Object -Unique).Count
Write-Host ("  重送 6 次   -> 分散到 {0} 台，其中 {1} 次為回放" `
        -f $distinctReplayInstances, $replayedCount)

Start-Sleep -Seconds 2
$orders = Invoke-RestMethod -Uri "$BaseUrl/api/orders?productId=$productId"
$orderCount = @($orders).Count
$stock = [int](Invoke-RestMethod -Uri "$BaseUrl/api/products/$productId").stock

Write-Host ("  訂單數 : {0}   (預期 1)" -f $orderCount)
Write-Host ("  庫存   : {0}   (預期 99)" -f $stock)

$statelessPass = ($orderCount -eq 1) -and ($stock -eq 99)
Write-Host ("  結果   : {0}" -f $(if ($statelessPass) { 'PASS' } else { 'FAIL' })) `
    -ForegroundColor $(if ($statelessPass) { 'Green' } else { 'Red' })

# ====================================================================
# 3. 共用狀態
# ====================================================================
Write-Host ''
Write-Host '=== 3. 共用狀態 ===' -ForegroundColor Cyan

# --- 3a. 計數器 ---
Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/diagnostics/metrics/reset" | Out-Null

$coldProductId = New-TestProduct
Start-Sleep -Milliseconds 500

# 讓多台同時讀同一個商品（冷快取 → Single Flight 該把它們收斂成 1 次查詢）
$jobs = @()
for ($i = 0; $i -lt $SingleFlightConcurrency; $i++) {
    $jobs += Start-Job -ScriptBlock {
        param($url)
        try { Invoke-RestMethod -Uri $url -TimeoutSec 20 | Out-Null } catch {}
    } -ArgumentList "$BaseUrl/api/products/$coldProductId"
}

$jobs | Wait-Job -Timeout 60 | Out-Null
$jobs | Remove-Job -Force

Start-Sleep -Seconds 1

# 從每一台分別讀計數器
$readings = @{}
for ($i = 0; $i -lt 12; $i++) {
    $response = Invoke-WebRequest -Uri "$BaseUrl/api/diagnostics/metrics" -UseBasicParsing
    $instance = $response.Headers[[string]'X-Instance-Id']
    $metrics = $response.Content | ConvertFrom-Json

    $readings[$instance] = [PSCustomObject]@{
        Scope       = $metrics.scope
        DbCommands  = $metrics.dbCommands
        CacheHits   = $metrics.cacheHits
        CacheMisses = $metrics.cacheMisses
    }
}

Write-Host ''
Write-Host ("  {0} 個併發請求讀同一個商品（冷快取）" -f $SingleFlightConcurrency)
Write-Host '  各 Instance 回報的計數：'

$readings.GetEnumerator() | Sort-Object Name | ForEach-Object {
    Write-Host ("    {0,-8} DbCommands={1,-6} CacheHits={2,-6} Scope={3}" `
            -f $_.Key, $_.Value.DbCommands, $_.Value.CacheHits, $_.Value.Scope)
}

$distinctDbCounts = ($readings.Values | ForEach-Object { $_.DbCommands } |
    Select-Object -Unique).Count

$metricsShared = ($distinctDbCounts -eq 1)
Write-Host ("  各台數字一致 : {0}" -f $(if ($metricsShared) { 'YES（共用）' } else { 'NO（各自為政）' })) `
    -ForegroundColor $(if ($metricsShared) { 'Green' } else { 'Red' })

$dbCommands = ($readings.Values | ForEach-Object { $_.DbCommands } |
    Measure-Object -Maximum).Maximum

Write-Host ("  冷快取造成的 DB 查詢數 : {0}   (Single Flight 有效時應接近 1)" -f $dbCommands) `
    -ForegroundColor $(if ($dbCommands -le 3) { 'Green' } else { 'Red' })

# --- 3b. 限流額度 ---
Write-Host ''
$rateLimitUser = "rl-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
$rlProductId = New-TestProduct

# 用 k6 而不是 PowerShell 迴圈。
#
# Start-Job 每一個都要啟動一個新的 PowerShell 行程（各數百毫秒），
# 60 個「並行」工作實際上會散布在好幾秒內、跨越多個限流視窗，
# 於是量不出「額度變成 N 倍」——第一次就是這樣量錯的。
$rlSummaryFile = (Join-Path $resultDir "mi-ratelimit-$(Get-Date -Format 'HHmmssfff').json").Replace('\', '/')

$env:BASE_URL = $BaseUrl
$env:PRODUCT_ID = "$rlProductId"
$env:RATE = "$RateLimitBurstRate"
$env:DURATION = '3s'
$env:USER_MODE = 'shared'
$env:USER_ID = $rateLimitUser
$env:SUMMARY_FILE = $rlSummaryFile

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& k6 run --quiet (Join-Path $scriptDir 'rate-limit.js') 2>&1 | Out-Null
$ErrorActionPreference = $previousPreference

$rlSummary = Get-Content $rlSummaryFile -Raw | ConvertFrom-Json

Write-Host ("  單一使用者持續 {0} req/s × 3 秒（限制 10 次/秒）" -f $RateLimitBurstRate)
Write-Host ("    請求 {0}   通過 {1}   被擋 {2}" `
        -f $rlSummary.requests, $rlSummary.allowed, $rlSummary.limited)
Write-Host ('  共用額度時通過數應接近 30（10/秒 × 3 秒）；')
Write-Host ('  各台自有額度時會接近 90（10 × 3 台 × 3 秒）。')

# ====================================================================
# 4. Kill Instance
# ====================================================================
if (-not $SkipKillTest) {
    Write-Host ''
    Write-Host '=== 4. Kill Instance ===' -ForegroundColor Cyan

    docker stop flashsale-api-2 | Out-Null
    Write-Host '  已停止 flashsale-api-2'

    Start-Sleep -Seconds 3

    $afterKill = @{}
    $failures = 0

    for ($i = 0; $i -lt 30; $i++) {
        try {
            $instance = Get-InstanceOf
            if ($instance) {
                if (-not $afterKill.ContainsKey($instance)) { $afterKill[$instance] = 0 }
                $afterKill[$instance]++
            }
        }
        catch {
            $failures++
        }
    }

    $afterKill.GetEnumerator() | Sort-Object Name | ForEach-Object {
        Write-Host ("    {0,-8} {1,4} 次" -f $_.Key, $_.Value)
    }

    Write-Host ("  失敗請求數 : {0}   (預期 0)" -f $failures) `
        -ForegroundColor $(if ($failures -eq 0) { 'Green' } else { 'Red' })

    docker start flashsale-api-2 | Out-Null
    Write-Host '  已重新啟動 flashsale-api-2'
}

Write-Host ''
