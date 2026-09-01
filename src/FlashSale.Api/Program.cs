using System.Text.Json.Serialization;
using FlashSale.Api.Extensions;
using FlashSale.Api.Middlewares;
using FlashSale.Api.Options;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers(options =>
    {
        // Action 一律以 Async 結尾（Guideline §21）。
        // 保留後綴，nameof(XxxAsync) 才能正確產生路由。
        options.SuppressAsyncSuffixInActionNames = false;
    })
    .AddJsonOptions(options =>
    {
        // 讓列舉以名稱而非數字傳遞，例如 "strategy": "Atomic"。
        // Stage 3 的策略選擇與訂單狀態都靠這個才好讀。
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Stage 8：Nginx 用它判斷這個 Instance 能不能接流量。
// 刻意不檢查資料庫或 Redis —— 那些是共用相依，
// 一掛就會讓「所有」Instance 同時被判定為不健康而全部下線，
// 反而製造出比原本更嚴重的故障。
builder.Services.AddHealthChecks();

builder.Services.AddApplicationDependencies(
    builder.Configuration);

// Stage 7：限流。獨立於 AddApplicationDependencies —— 它註冊的是
// ASP.NET Core 的 Framework Service（RateLimiter middleware），
// 不是應用程式自己的相依性。
//
// Stage 8：內建的限流器是行程內的，多 Instance 下額度會變成 N 倍。
// 開啟 SharedState:DistributedRateLimit 時改用 Redis 版中介軟體，
// 此時就不註冊內建的那一個，避免兩層限流疊加。
var useDistributedRateLimit = builder.Configuration.GetValue(
    $"{SharedStateOptions.SectionName}:{nameof(SharedStateOptions.DistributedRateLimit)}",
    defaultValue: true);

if (!useDistributedRateLimit)
{
    builder.Services.AddApplicationRateLimiting(builder.Configuration);
}

// Stage 8：反向代理後面必須還原真實的客戶端 IP。
//
// 不做的話 RemoteIpAddress 會是 Nginx 的 IP，per-IP 限流會把
// **所有人**算成同一個分區 —— 一個人超量就把全站擋住。
// Stage 7 的文件已經標記這個陷阱，這裡是它的解法。
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // 預設只信任 loopback。容器網路裡 Nginx 不是 loopback，
    // 清空代表信任所有上游 —— 只有在「API 不會被直接對外暴露」
    // 的前提下才安全，也就是必須確保外部只能經由 Nginx 進來。
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Stage 5：宣告 RabbitMQ 拓撲。
// AMQP 的宣告是冪等的，API 與 Worker 兩邊都會做一次，誰先啟動都不影響。
// Broker 不可用時只記錄錯誤，不阻擋啟動 —— 同步路徑不需要它。
await app.Services.DeclareMessagingTopologyAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 必須在所有會用到客戶端 IP 的中介軟體之前。
app.UseForwardedHeaders();

// 標記處理這個請求的 Instance，多 Instance 下的一切觀察都靠它。
app.UseMiddleware<InstanceHeaderMiddleware>();

app.UseMiddleware<GlobalExceptionMiddleware>();

// 容器內以 HTTP 對外，TLS 由 Nginx 終結。
// 保留 HTTPS 轉址會讓所有經由 Nginx 進來的請求被 307 導向一個
// 容器裡並不存在的 HTTPS 埠。
if (!app.Environment.IsEnvironment("Container"))
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

// 限流放在 Authorization 之後、端點之前：
// 這樣被擋下的請求不會進到 Controller，也就不會消耗資料庫或 Redis。
// 拒絕的成本必須遠低於處理的成本，否則限流本身就會變成瓶頸。
if (app.Configuration.GetValue(
        $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.Enabled)}",
        defaultValue: true))
{
    if (useDistributedRateLimit)
    {
        app.UseMiddleware<DistributedRateLimitMiddleware>();
    }
    else
    {
        app.UseRateLimiter();
    }
}

app.MapHealthChecks("/health");

app.MapControllers();

app.Run();
