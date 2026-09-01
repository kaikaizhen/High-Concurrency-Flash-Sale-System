using System.Diagnostics;
using FlashSale.Api.Infrastructure.RateLimiting;

namespace FlashSale.Api.Middlewares;

/// <summary>
/// 每個請求輸出一行結構化日誌。
///
/// 計畫 §15 指定要記錄：
/// RequestId / UserId / ProductId / Duration / Result / Exception
///
/// **一個請求一行**，不是每個步驟一行。
/// 散落各處的日誌無法回答「這個請求花了多久、結果是什麼」——
/// 你得先把它們串起來，而那正是 TraceId 存在的理由。
///
/// 這一行日誌的用途是「找出哪些請求有問題」；
/// 找到之後用 TraceId 到 Tracing 系統看它內部的每一段耗時。
/// 兩者分工，不重複。
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(
        RequestDelegate next,
        ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 觀測端點自己不記錄 —— 壓測期間每 2 秒輪詢一次，
        // 記下來只會稀釋真正需要看的內容。
        if (context.Request.Path.StartsWithSegments("/api/diagnostics") ||
            context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        Exception? failure = null;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            stopwatch.Stop();

            Write(context, stopwatch.Elapsed.TotalMilliseconds, failure);
        }
    }

    private void Write(HttpContext context, double durationMs, Exception? failure)
    {
        var statusCode = context.Response.StatusCode;

        // 日誌等級由結果決定，而不是一律 Information：
        //   5xx 是系統的問題        → Error
        //   4xx 是請求的問題        → Warning（庫存不足、限流、重複請求）
        //   其餘                    → Information
        //
        // 全部記成 Information 的話，出事時得自己用眼睛在幾百萬行裡找異常。
        var level = statusCode >= 500 || failure is not null
            ? LogLevel.Error
            : statusCode >= 400
                ? LogLevel.Warning
                : LogLevel.Information;

        // Serilog 的訊息範本：{} 內的名稱會成為可查詢的結構化欄位，
        // 不是單純的字串插值。之後可以直接查
        // 「DurationMs > 1000 且 ProductId = 42」而不必寫正規表示式。
        _logger.Log(
            level,
            failure,
            "HTTP {RequestMethod} {RequestPath} responded {StatusCode} "
            + "in {DurationMs:0.00} ms. "
            + "TraceId={TraceId} UserId={UserId} ProductId={ProductId} "
            + "IdempotencyKey={IdempotencyKey} Instance={InstanceId}",
            context.Request.Method,
            context.Request.Path.Value,
            statusCode,
            durationMs,
            Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            ExtractUserId(context),
            ExtractProductId(context),
            ExtractIdempotencyKey(context),
            InstanceHeaderMiddleware.ResolveInstanceId());
    }

    /// <summary>
    /// UserId 取自 Header 而不是 Request Body。
    ///
    /// Body 在這個階段已經被讀掉了（Model Binding 消費過），
    /// 要重讀必須開啟 Buffering，那對每個請求都是額外成本。
    /// 有認證的系統應該從 JWT claim 取得。
    /// </summary>
    private static string? ExtractUserId(HttpContext context)
    {
        return context.Request.Headers
            .TryGetValue(RateLimitPartitionKeys.UserHeaderName, out var values)
            ? values.ToString()
            : null;
    }

    /// <summary>
    /// ProductId 取自路由參數 —— 那是 Model Binding 之後才有的值，
    /// 因此只有在請求真的被路由到端點時才拿得到。
    /// </summary>
    private static string? ExtractProductId(HttpContext context)
    {
        return context.Request.RouteValues
            .TryGetValue("productId", out var value)
            ? value?.ToString()
            : null;
    }

    private static string? ExtractIdempotencyKey(HttpContext context)
    {
        return context.Request.Headers
            .TryGetValue(Filters.IdempotencyFilter.HeaderName, out var values)
            ? values.ToString()
            : null;
    }
}
