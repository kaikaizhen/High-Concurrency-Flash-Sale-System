<#
.SYNOPSIS
    Stage 9 — 壓測套件執行器。

.DESCRIPTION
    執行計畫 §14 的 Test Profile，並在壓測**期間**持續取樣系統指標。

    取樣必須與壓測同時進行 —— 壓測結束後才量，看到的是系統已經恢復
    的樣子，完全錯過瓶頸發生的那一刻。

    收集的指標（計畫 §14）：
        RPS / Average / P50 / P95 / P99 / Error Rate
        CPU / Memory / DB Connection / Redis Latency / Queue Length

.NOTES
    本檔必須以 UTF-8 with BOM 儲存（PowerShell 5.1 的編碼陷阱）。

    壓測容量時必須關閉限流，否則量到的是限流器的行為而不是系統容量：
        $env:RateLimit__Enabled = "false"

.EXAMPLE
    .\Run-LoadTestSuite.ps1 -Profile smoke
    .\Run-LoadTestSuite.ps1 -Profile stress -Scenario purchase
#>
param(
    [string]$BaseUrl = 'http://localhost:5080',

    [ValidateSet('smoke', 'normal', 'stress', 'spike')]
    [string]$Profile = 'smoke',

    [ValidateSet('read', 'purchase')]
    [string]$Scenario = 'read',

    [string]$Strategy = 'Atomic',

    # 搶購測試需要足夠庫存，否則量到的是「賣完之後的拒絕速度」
    [int]$Stock = 1000000,

    [string]$Label = ''
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptPath = Join-Path $scriptDir 'flash-sale-suite.js'
$resultDir = Join-Path $scriptDir 'results'

if (-not (Test-Path $resultDir)) {
    New-Item -ItemType Directory -Path $resultDir | Out-Null
}

if ([string]::IsNullOrWhiteSpace($Label)) {
    $Label = "$Profile-$Scenario"
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runName = "suite-$Label-$stamp"

Write-Host ''
Write-Host "=== $Profile / $Scenario / $BaseUrl ===" -ForegroundColor Cyan

# ---- 建立測試商品 ----
$productBody = @{
    name  = "$runName"
    price = 100
    stock = $Stock
} | ConvertTo-Json -Compress

$productId = (Invoke-RestMethod `
        -Method Post `
        -Uri "$BaseUrl/api/products" `
        -ContentType 'application/json' `
        -Body $productBody).id

Write-Host "ProductId = $productId  (Stock = $Stock)"

# 先暖身一次：CPU 使用率需要兩次取樣才算得出來，
# 第一次呼叫永遠是 0。
Invoke-RestMethod -Uri "$BaseUrl/api/diagnostics/system" -TimeoutSec 10 | Out-Null
Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/diagnostics/metrics/reset" | Out-Null

# ---- 背景取樣系統指標 ----
$samplerScript = {
    param($baseUrl, $outputPath)

    $samples = @()
    $started = Get-Date

    while ($true) {
        try {
            $system = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/system" -TimeoutSec 5

            $samples += [PSCustomObject]@{
                ElapsedSec    = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
                InstanceId    = $system.instanceId
                CpuPercent    = $system.process.cpuPercent
                WorkingSetMb  = $system.process.workingSetMb
                GcHeapMb      = $system.process.gcHeapMb
                ThreadCount   = $system.process.threadCount
                DbConnections = $system.database.connections
                DbLatencyMs   = $system.database.latencyMs
                RedisLatencyMs = $system.redis.latencyMs
                QueueLength   = $system.queue.pendingOrders
            }

            $samples | Export-Csv -Path $outputPath -NoTypeInformation
        }
        catch {
            # 壓測期間取樣端點本身也可能逾時 —— 那本身就是一個訊號，
            # 但不該讓取樣器整個停掉。
            $samples += [PSCustomObject]@{
                ElapsedSec    = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
                InstanceId    = 'SAMPLE_FAILED'
                CpuPercent    = -1
                WorkingSetMb  = -1
                GcHeapMb      = -1
                ThreadCount   = -1
                DbConnections = -1
                DbLatencyMs   = -1
                RedisLatencyMs = -1
                QueueLength   = -1
            }

            $samples | Export-Csv -Path $outputPath -NoTypeInformation
        }

        Start-Sleep -Seconds 2
    }
}

$samplePath = Join-Path $resultDir "$runName-system.csv"
$sampler = Start-Job -ScriptBlock $samplerScript -ArgumentList $BaseUrl, $samplePath

# ---- 壓測 ----
$summaryFile = (Join-Path $resultDir "$runName.json").Replace('\', '/')

$env:BASE_URL = $BaseUrl
$env:PROFILE = $Profile
$env:SCENARIO = $Scenario
$env:PRODUCT_ID = "$productId"
$env:STRATEGY = $Strategy
$env:SUMMARY_FILE = $summaryFile

$started = Get-Date

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& k6 run --quiet $scriptPath 2>&1 | Out-Null
$ErrorActionPreference = $previousPreference

$elapsed = (Get-Date) - $started

Stop-Job $sampler | Out-Null
Remove-Job $sampler -Force | Out-Null

$summary = Get-Content $summaryFile -Raw | ConvertFrom-Json

# ---- 系統指標彙整 ----
$peak = [PSCustomObject]@{
    CpuPercent     = 0
    WorkingSetMb   = 0
    ThreadCount    = 0
    DbConnections  = 0
    DbLatencyMs    = 0
    RedisLatencyMs = 0
    QueueLength    = 0
    SampleFailures = 0
}

if (Test-Path $samplePath) {
    $samples = @(Import-Csv $samplePath)
    $valid = @($samples | Where-Object { $_.InstanceId -ne 'SAMPLE_FAILED' })

    $peak.SampleFailures = $samples.Count - $valid.Count

    if ($valid.Count -gt 0) {
        $peak.CpuPercent = [math]::Round(
            ($valid | Measure-Object -Property CpuPercent -Maximum).Maximum, 1)
        $peak.WorkingSetMb = [math]::Round(
            ($valid | Measure-Object -Property WorkingSetMb -Maximum).Maximum, 1)
        $peak.ThreadCount = ($valid | Measure-Object -Property ThreadCount -Maximum).Maximum
        $peak.DbConnections = ($valid | Measure-Object -Property DbConnections -Maximum).Maximum
        $peak.DbLatencyMs = [math]::Round(
            ($valid | Measure-Object -Property DbLatencyMs -Maximum).Maximum, 1)
        $peak.RedisLatencyMs = [math]::Round(
            ($valid | Measure-Object -Property RedisLatencyMs -Maximum).Maximum, 1)
        $peak.QueueLength = ($valid | Measure-Object -Property QueueLength -Maximum).Maximum
    }
}

$appMetrics = Invoke-RestMethod -Uri "$BaseUrl/api/diagnostics/metrics"

$row = [PSCustomObject]@{
    Profile        = $Profile
    Scenario       = $Scenario
    DurationSec    = [math]::Round($elapsed.TotalSeconds, 1)
    Requests       = $summary.requests
    Ok             = $summary.ok
    Rejected       = $summary.rejected
    Failed         = $summary.failed
    ErrorRatePct   = $summary.errorRatePct
    Rps            = [math]::Round($summary.rps, 1)
    AvgMs          = [math]::Round($summary.durationMs.avg, 1)
    P50ms          = [math]::Round($summary.durationMs.p50, 1)
    P95ms          = [math]::Round($summary.durationMs.p95, 1)
    P99ms          = [math]::Round($summary.durationMs.p99, 1)
    MaxMs          = [math]::Round($summary.durationMs.max, 1)
    PeakCpuPct     = $peak.CpuPercent
    PeakMemMb      = $peak.WorkingSetMb
    PeakThreads    = $peak.ThreadCount
    PeakDbConns    = $peak.DbConnections
    PeakDbMs       = $peak.DbLatencyMs
    PeakRedisMs    = $peak.RedisLatencyMs
    PeakQueue      = $peak.QueueLength
    DbCommands     = $appMetrics.dbCommands
    CacheHitRatePct = [math]::Round($appMetrics.cacheHitRate * 100, 1)
    SampleFailures = $peak.SampleFailures
}

Write-Host ''
$row | Format-List | Out-String | Write-Host

Write-Host "系統指標取樣：$samplePath"
Write-Host ''

$row
