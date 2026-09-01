<#
.SYNOPSIS
    Stage 7 — 固定視窗的邊界爆發問題。

.DESCRIPTION
    固定視窗每 N 秒把計數歸零。攻擊者只要抓準時機：

        視窗 1 結束前  用滿額度
        視窗 2 開始後  再用滿一次

    就能在**短短一兩秒內**通過兩倍的請求量，
    而每個視窗看起來都完全符合規則。

    這個腳本刻意製造這個時機：

        T0            送 1 個請求（這一刻起算視窗 1）
        T0 + W - 1s   送滿 PermitLimit - 1 個  ← 仍在視窗 1
        T0 + W + 1s   送滿 PermitLimit 個      ← 已進入視窗 2

    FixedWindow  → 兩批都通過，2 秒內放行約 2×PermitLimit
    SlidingWindow → 第二批大部分被擋下

.NOTES
    本檔必須以 UTF-8 with BOM 儲存（PowerShell 5.1 的編碼陷阱）。

    API 端需先設定：
        RateLimit__FlashSale__Algorithm    FixedWindow 或 SlidingWindow
        RateLimit__FlashSale__PermitLimit  20
        RateLimit__FlashSale__WindowSeconds 10
        RateLimit__PerIp__Enabled          false   （避免全域限制干擾）

.EXAMPLE
    .\Test-WindowBoundary.ps1 -Label FixedWindow -PermitLimit 20 -WindowSeconds 10
#>
param(
    [string]$BaseUrl = 'http://localhost:5080',
    [string]$Label = 'current',
    [int]$PermitLimit = 20,
    [int]$WindowSeconds = 10
)

$ErrorActionPreference = 'Stop'

# 每次都用全新的使用者 Id，確保視窗是從我們的第一個請求開始算
$userId = "boundary-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"

$productBody = @{
    name  = "boundary-$Label-$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')"
    price = 100
    stock = 1000000
} | ConvertTo-Json -Compress

$productId = (Invoke-RestMethod `
        -Method Post `
        -Uri "$BaseUrl/api/products" `
        -ContentType 'application/json' `
        -Body $productBody).id

function Send-Batch {
    param([int]$Count)

    $allowed = 0
    $limited = 0

    for ($i = 0; $i -lt $Count; $i++) {
        $body = @{ userId = 1; quantity = 1 } | ConvertTo-Json -Compress

        try {
            $response = Invoke-WebRequest `
                -Method Post `
                -Uri "$BaseUrl/api/flash-sale/$productId" `
                -Headers @{ 'X-User-Id' = $userId } `
                -ContentType 'application/json' `
                -Body $body `
                -UseBasicParsing

            if ($response.StatusCode -eq 200 -or $response.StatusCode -eq 202) {
                $allowed++
            }
        }
        catch {
            if ($_.Exception.Response.StatusCode.value__ -eq 429) {
                $limited++
            }
        }
    }

    return @{ Allowed = $allowed; Limited = $limited }
}

Write-Host ''
Write-Host "=== 邊界爆發測試 / $Label / 限制 $PermitLimit 次 per $WindowSeconds 秒 ===" -ForegroundColor Cyan
Write-Host "ProductId = $productId   X-User-Id = $userId"
Write-Host ''

# --- T0：第一個請求，視窗從這一刻開始 ---
$t0 = Get-Date
$first = Send-Batch -Count 1
Write-Host ("  T+0.0s        送 1 個   -> 通過 {0}  (視窗 1 開始)" -f $first.Allowed)

# --- 視窗 1 結束前 1 秒：把剩下的額度用滿 ---
$target1 = $t0.AddSeconds($WindowSeconds - 1)
$wait1 = ($target1 - (Get-Date)).TotalSeconds
if ($wait1 -gt 0) { Start-Sleep -Seconds ([math]::Ceiling($wait1)) }

$elapsed1 = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
$batch1 = Send-Batch -Count ($PermitLimit - 1)
Write-Host ("  T+{0}s       送 {1} 個  -> 通過 {2}  被擋 {3}   (仍在視窗 1)" `
        -f $elapsed1, ($PermitLimit - 1), $batch1.Allowed, $batch1.Limited)

# --- 視窗 2 開始後 1 秒：再用滿一次 ---
$target2 = $t0.AddSeconds($WindowSeconds + 1)
$wait2 = ($target2 - (Get-Date)).TotalSeconds
if ($wait2 -gt 0) { Start-Sleep -Seconds ([math]::Ceiling($wait2)) }

$burstStart = Get-Date
$elapsed2 = [math]::Round(($burstStart - $t0).TotalSeconds, 1)
$batch2 = Send-Batch -Count $PermitLimit
Write-Host ("  T+{0}s      送 {1} 個  -> 通過 {2}  被擋 {3}   (已進入視窗 2)" `
        -f $elapsed2, $PermitLimit, $batch2.Allowed, $batch2.Limited)

$burstWindow = [math]::Round(((Get-Date) - $target1).TotalSeconds, 1)
$burstAllowed = $batch1.Allowed + $batch2.Allowed

Write-Host ''
Write-Host ("  跨越邊界的 {0} 秒內共放行 : {1} 個" -f $burstWindow, $burstAllowed)
Write-Host ("  名目限制                  : {0} 個 per {1} 秒" -f $PermitLimit, $WindowSeconds)

$ratio = [math]::Round($burstAllowed / $PermitLimit, 2)
Write-Host ("  實際倍數                  : {0}x" -f $ratio) `
    -ForegroundColor $(if ($ratio -gt 1.5) { 'Red' } else { 'Green' })

[PSCustomObject]@{
    Label            = $Label
    PermitLimit      = $PermitLimit
    WindowSeconds    = $WindowSeconds
    Batch1Allowed    = $batch1.Allowed
    Batch1Limited    = $batch1.Limited
    Batch2Allowed    = $batch2.Allowed
    Batch2Limited    = $batch2.Limited
    BurstWindowSec   = $burstWindow
    BurstAllowed     = $burstAllowed
    TimesOverLimit   = $ratio
}
