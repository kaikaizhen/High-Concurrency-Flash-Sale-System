<#
.SYNOPSIS
    發送一則無法解析的訊息，驗證 Dead Letter Queue。

.DESCRIPTION
    透過 RabbitMQ 管理 API 發布一段不是合法 JSON 的訊息本體。

    Consumer 會在反序列化階段就失敗，此時**不會**進入重試流程 ——
    無法解析的內容重試一百次也不會突然變得可以解析，
    直接送進 DLQ 才是對的。這與「暫時性錯誤」的處理方式刻意不同。

.NOTES
    本檔必須以 UTF-8 with BOM 儲存（PowerShell 5.1 的編碼陷阱）。

.EXAMPLE
    .\Send-PoisonMessage.ps1 -ManagementUrl "http://<rabbitmq-host>:15672" -User <user> -Password <password>
#>
param(
    [Parameter(Mandatory = $true)][string]$ManagementUrl,
    [Parameter(Mandatory = $true)][string]$User,
    [Parameter(Mandatory = $true)][string]$Password,
    [string]$VirtualHost = '/',
    [string]$Exchange = 'flashsale.orders',
    [string]$RoutingKey = 'order.created',
    [string]$Payload = 'this-is-not-json'
)

$ErrorActionPreference = 'Stop'

$auth = [Convert]::ToBase64String(
    [Text.Encoding]::ASCII.GetBytes("${User}:${Password}"))

# vhost "/" 在 URL 中要編碼成 %2F
$encodedVhost = [Uri]::EscapeDataString($VirtualHost)

$body = @{
    properties       = @{}
    routing_key      = $RoutingKey
    payload          = $Payload
    payload_encoding = 'string'
} | ConvertTo-Json -Compress

$response = Invoke-RestMethod `
    -Method Post `
    -Uri "$ManagementUrl/api/exchanges/$encodedVhost/$Exchange/publish" `
    -Headers @{ Authorization = "Basic $auth" } `
    -ContentType 'application/json' `
    -Body $body

if ($response.routed) {
    Write-Host "毒訊息已發布並成功路由。Payload = $Payload" -ForegroundColor Yellow
}
else {
    Write-Warning "訊息未被路由 —— 請確認 Exchange 與 Routing Key 是否正確。"
}
