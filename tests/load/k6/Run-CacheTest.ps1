<#
.SYNOPSIS
    Stage 4 — 商品讀取壓測，並回報資料庫查詢次數與快取命中率。

.DESCRIPTION
    流程：

        建立商品（或使用指定的 ProductId）
            -> 重設 API 端計數器
            -> k6 壓測
            -> 讀回計數器與最終狀態

    API 端的計數器由 EF Core Interceptor 與 CacheService 累加，
    因此 DbCommands 是**實際送到資料庫的命令數**，不是估算值。

.NOTES
    本檔必須以 UTF-8 with BOM 儲存（PowerShell 5.1 的編碼陷阱）。

    Before / After 的切換靠啟動 API 時的環境變數，不是這個腳本：

        $env:Cache__Enabled = "false"   # Before
        $env:Cache__Enabled = "true"    # After

.EXAMPLE
    .\Run-CacheTest.ps1 -Label "cache-off"
    .\Run-CacheTest.ps1 -Label "penetration" -ProductId 999999
#>
param(
    [string]$BaseUrl = 'http://localhost:5080',
    [string]$Label = 'run',
    [int]$Iterations = 5000,
    [int]$Vus = 200,

    # 指定時直接壓這個 Id（例如測 Cache Penetration 用不存在的 Id）；
    # 未指定則建立一個全新商品。
    [int]$ProductId = 0
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptPath = Join-Path $scriptDir 'product-read.js'
$resultDir = Join-Path $scriptDir 'results'

if (-not (Test-Path $resultDir)) {
    New-Item -ItemType Directory -Path $resultDir | Out-Null
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$runName = "cache-$Label-$stamp"

Write-Host ''
Write-Host "=== $Label / Requests = $Iterations / VUs = $Vus ===" -ForegroundColor Cyan

if ($ProductId -eq 0) {
    $createBody = @{
        name  = $runName
        price = 100
        stock = 1000000
    } | ConvertTo-Json -Compress

    $product = Invoke-RestMethod `
        -Method Post `
        -Uri "$BaseUrl/api/products" `
        -ContentType 'application/json' `
        -Body $createBody

    $targetId = $product.id
}
else {
    $targetId = $ProductId
}

Write-Host "ProductId = $targetId"

# 重設計數器 —— 必須在建立商品之後，否則建立商品的那幾次寫入也會被算進去
Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/diagnostics/metrics/reset" | Out-Null

$summaryFile = (Join-Path $resultDir "$runName.json").Replace('\', '/')

$env:BASE_URL = $BaseUrl
$env:PRODUCT_ID = "$targetId"
$env:VUS = "$Vus"
$env:ITERATIONS = "$Iterations"
$env:SUMMARY_FILE = $summaryFile

$started = Get-Date

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& k6 run --quiet $scriptPath 2>&1 | Out-Null
$ErrorActionPreference = $previousPreference

$elapsed = (Get-Date) - $started

$summary = Get-Content $summaryFile -Raw | ConvertFrom-Json
$metrics = Invoke-RestMethod -Uri "$BaseUrl/api/diagnostics/metrics"

$row = [PSCustomObject]@{
    Label        = $Label
    Requests     = $summary.requests
    Ok           = $summary.ok
    NotFound     = $summary.notFound
    Errored      = $summary.errored
    DbCommands   = $metrics.dbCommands
    CacheHits    = $metrics.cacheHits
    CacheMisses  = $metrics.cacheMisses
    CacheErrors  = $metrics.cacheErrors
    HitRate      = [math]::Round($metrics.cacheHitRate * 100, 2)
    DurationSec  = [math]::Round($elapsed.TotalSeconds, 1)
    Rps          = [math]::Round($summary.rps, 1)
    AvgMs        = [math]::Round($summary.durationMs.avg, 1)
    P95ms        = [math]::Round($summary.durationMs.p95, 1)
    P99ms        = [math]::Round($summary.durationMs.p99, 1)
}

$row | Format-List | Out-String | Write-Host

$row
