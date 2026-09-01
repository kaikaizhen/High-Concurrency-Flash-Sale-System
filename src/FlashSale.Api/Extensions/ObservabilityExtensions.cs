using FlashSale.Api.Infrastructure.Observability;
using FlashSale.Api.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace FlashSale.Api.Extensions;

public static class ObservabilityExtensions
{
    /// <summary>
    /// 結構化日誌。
    ///
    /// 必須在 builder 建立後**最早**呼叫 —— 啟動過程本身的錯誤
    /// （設定讀不到、相依註冊失敗）也需要被記錄下來。
    /// </summary>
    public static void AddStructuredLogging(this WebApplicationBuilder builder)
    {
        // 用 Services.AddSerilog 而不是 Host.UseSerilog：
        // 後者在 Serilog.AspNetCore 10 已不再取代預設的 logging provider，
        // 設定會被靜默忽略 —— 程式照跑，但輸出仍是預設格式。
        builder.Services.AddSerilog((services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)

                // TraceId / SpanId 讓日誌與 Tracing 能互相跳轉：
                // 從一行慢請求的日誌，直接跳到它的完整 Trace。
                // 沒有這個關聯，兩套系統就是兩座孤島。
                .Enrich.FromLogContext()
                .Enrich.WithProperty(
                    "InstanceId",
                    Middlewares.InstanceHeaderMiddleware.ResolveInstanceId())

                .WriteTo.Console(
                    outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} "
                    + "{Properties:j}{NewLine}{Exception}")

                // 檔案輸出用 JSON：主控台是給人看的，檔案是給機器解析的。
                // 兩者用同一種格式，必然有一邊不好用。
                .WriteTo.File(
                    new Serilog.Formatting.Compact.CompactJsonFormatter(),
                    "logs/flashsale-.jsonl",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)

                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override(
                    "Microsoft.EntityFrameworkCore.Database.Command",
                    LogEventLevel.Warning);
        });
    }

    /// <summary>
    /// Metrics 與 Tracing。
    /// </summary>
    public static IServiceCollection AddApplicationObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection(ObservabilityOptions.SectionName)
            .Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        services.Configure<ObservabilityOptions>(
            configuration.GetSection(ObservabilityOptions.SectionName));

        services.AddSingleton<FlashSaleMetrics>();

        var instanceId = Middlewares.InstanceHeaderMiddleware.ResolveInstanceId();

        var builder = services.AddOpenTelemetry().ConfigureResource(resource =>
            resource.AddService(
                serviceName: options.ServiceName,
                serviceVersion: typeof(ObservabilityExtensions).Assembly
                    .GetName().Version?.ToString() ?? "unknown",
                // 多 Instance 下必須區分是哪一台送來的，
                // 否則三台的 Trace 混在一起無法歸因。
                serviceInstanceId: instanceId));

        if (options.MetricsEnabled)
        {
            builder.WithMetrics(metrics =>
            {
                metrics
                    // HTTP 請求數 / 延遲 / 錯誤數
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    // .NET Runtime 的 GC、執行緒池、例外數
                    .AddRuntimeInstrumentation()
                    // 業務指標
                    .AddMeter(FlashSaleMetrics.MeterName);

                if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                {
                    metrics.AddOtlpExporter(exporter =>
                    {
                        exporter.Endpoint = new Uri(options.OtlpEndpoint);
                    });
                }
            });
        }

        if (options.TracingEnabled)
        {
            builder.WithTracing(tracing =>
            {
                tracing
                    // 取樣：壓測時務必調低，否則觀測本身會成為瓶頸。
                    .SetSampler(new ParentBasedSampler(
                        new TraceIdRatioBasedSampler(options.TraceSampleRatio)))

                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        // 觀測端點不追蹤：壓測期間每 2 秒輪詢一次，
                        // 追蹤它只會在 Trace 列表裡製造噪音。
                        instrumentation.Filter = context =>
                            !context.Request.Path
                                .StartsWithSegments("/api/diagnostics")
                            && !context.Request.Path.StartsWithSegments("/health")
                            && !context.Request.Path.StartsWithSegments("/metrics");
                    })

                    .AddHttpClientInstrumentation()

                    // SQL 命令的 Span。
                    //
                    // 刻意不開啟記錄 SQL 語句文字：參數值可能含個資，
                    // 而本專案要回答的問題（哪一段慢）從 Span 的耗時
                    // 與呼叫關係就能看出來，不需要語句內容。
                    .AddSqlClientInstrumentation()

                    .AddSource(FlashSaleActivitySource.Name);

                if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                {
                    tracing.AddOtlpExporter(exporter =>
                    {
                        exporter.Endpoint = new Uri(options.OtlpEndpoint);
                    });
                }
            });
        }

        return services;
    }
}
