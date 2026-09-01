<#
.SYNOPSIS
    Stage 7 — 限流驗證。

.DESCRIPTION
    執行計畫 §12 指定的兩個情境：

        正常   10 req/s          → 應該全部通過
        異常   1000 req/s 同一人 → 大部分應該被 429 擋下

    演算法由 API 端的設定決定（RateLimit:FlashSale:Algorithm），
    要比較四種就分別重啟 API 後各跑一次。

.NOTES
    本檔必須以 UTF-8 with BOM 儲存（PowerShell 5.1 的編碼陷阱）。

.EXAMPLE
    .\Run-RateLimitTest.ps1 -Label SlidingWindow
#>
param(
    [string]$BaseUrl = 'http://localhost:5080',

    # 只是輸出用的標籤，實際演算法由 API 設定決定
    [string]$Label = 'current',

    [int]$NormalRate = 10,
    [int]$AbuseRate = 1000,
    [string]$Duration = '10s',
    [int]$Stock = 1000000
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptPath = Join-Path $scriptDir 'rate-limit.js'
$resultDir = Join-Path $scriptDir 'results'

if (-not (Test-Path $resultDir)) {
    New-Item -ItemType Directory -Path $resultDir | Out-Null
}

function New-TestProduct {
    param([string]$Suffix)

    $body = @{
        name  = "ratelimit-$Label-$Suffix-$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')"
        price = 100
        # 庫存開很大：這一階段要量的是限流，不是庫存耗盡。
        # 庫存不足會回 409，混進來就分不清誰擋下了請求。
        stock = $Stock
    } | ConvertTo-Json -Compress

    return (Invoke-RestMethod `
            -Method Post `
            -Uri "$BaseUrl/api/products" `
            -ContentType 'application/json' `
            -Body $body).id
}

function Invoke-Scenario {
    param(
        [string]$Name,
        [int]$Rate,
        [string]$UserMode
    )

    Write-Host ''
    Write-Host "--- $Name : $Rate req/s, $Duration, user=$UserMode ---" -ForegroundColor Cyan

    $productId = New-TestProduct -Suffix $Name
    $summaryFile = (Join-Path $resultDir "rl-$Label-$Name-$(Get-Date -Format 'HHmmssfff').json").Replace('\', '/')

    $env:BASE_URL = $BaseUrl
    $env:PRODUCT_ID = "$productId"
    $env:RATE = "$Rate"
    $env:DURATION = $Duration
    $env:USER_MODE = $UserMode
    $env:SUMMARY_FILE = $summaryFile

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & k6 run --quiet $scriptPath 2>&1 | Out-Null
    $ErrorActionPreference = $previousPreference

    $summary = Get-Content $summaryFile -Raw | ConvertFrom-Json

    $allowedPct = 0
    if ($summary.requests -gt 0) {
        $allowedPct = [math]::Round(100 * $summary.allowed / $summary.requests, 1)
    }

    [PSCustomObject]@{
        Scenario        = $Name
        TargetRps       = $Rate
        ActualRps       = [math]::Round($summary.actualRps, 1)
        Requests        = $summary.requests
        Allowed         = $summary.allowed
        Limited429      = $summary.limited
        Errored         = $summary.errored
        AllowedPct      = $allowedPct
        AvgMs           = [math]::Round($summary.durationMs.avg, 1)
        P95ms           = [math]::Round($summary.durationMs.p95, 1)
        Limited429AvgMs = if ($null -eq $summary.limitedLatencyMs.avg) { 0 }
                          else { [math]::Round($summary.limitedLatencyMs.avg, 1) }
    }
}

Write-Host ''
Write-Host "=== Rate Limit / $Label ===" -ForegroundColor Cyan

$results = @()
$results += Invoke-Scenario -Name 'normal' -Rate $NormalRate -UserMode 'unique'
$results += Invoke-Scenario -Name 'abuse' -Rate $AbuseRate -UserMode 'shared'

Write-Host ''
Write-Host '=== Summary ===' -ForegroundColor Cyan
$results | Format-Table -AutoSize

$results
