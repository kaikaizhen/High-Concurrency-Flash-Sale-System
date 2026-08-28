using System.Text.Json.Serialization;
using FlashSale.Api.Extensions;
using FlashSale.Api.Middlewares;

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

app.MapControllers();

app.Run();
