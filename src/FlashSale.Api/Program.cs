using System.Text.Json.Serialization;
using FlashSale.Api.Extensions;
using FlashSale.Api.Middlewares;
using FlashSale.Api.Options;

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

builder.Services.AddApplicationDependencies(
    builder.Configuration);

// Stage 7：限流。獨立於 AddApplicationDependencies —— 它註冊的是
// ASP.NET Core 的 Framework Service（RateLimiter middleware），
// 不是應用程式自己的相依性。
builder.Services.AddApplicationRateLimiting(
    builder.Configuration);

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

app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseHttpsRedirection();

app.UseAuthorization();

// 限流放在 Authorization 之後、端點之前：
// 這樣被擋下的請求不會進到 Controller，也就不會消耗資料庫或 Redis。
// 拒絕的成本必須遠低於處理的成本，否則限流本身就會變成瓶頸。
if (app.Configuration.GetValue(
        $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.Enabled)}",
        defaultValue: true))
{
    app.UseRateLimiter();
}

app.MapControllers();

app.Run();
