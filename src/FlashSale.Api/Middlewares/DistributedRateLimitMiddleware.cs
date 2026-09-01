using System.Globalization;
using System.Text.Json;
using FlashSale.Api.Infrastructure.RateLimiting;
using FlashSale.Api.Options;
using Microsoft.Extensions.Options;

namespace FlashSale.Api.Middlewares;

/// <summary>
/// 跨 Instance 共用額度的限流。
///
/// 為什麼不用 ASP.NET Core 內建的 RateLimiter：
/// 它的計數在行程記憶體中，多 Instance 下額度會變成 N 倍。
/// 而它的擴充點 <c>RateLimiter.AttemptAcquireCore</c> 是同步的，
/// 無法在裡面呼叫 Redis —— 因此改以中介軟體實作。
///
/// 兩者的 429 回應格式完全一致，客戶端不需要區分。
/// </summary>
public class DistributedRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IDistributedRateLimiter _limiter;
    private readonly RateLimitOptions _options;
    private readonly ILogger<DistributedRateLimitMiddleware> _logger;

    public DistributedRateLimitMiddleware(
        RequestDelegate next,
        IDistributedRateLimiter limiter,
        IOptions<RateLimitOptions> options,
        ILogger<DistributedRateLimitMiddleware> logger)
    {
        _next = next;
        _limiter = limiter;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldLimit(context))
        {
            await _next(context);
            return;
        }

        // 全域 per-IP
        if (_options.PerIp.Enabled)
        {
            var ipDecision = await _limiter.TryAcquireAsync(
                RateLimitPartitionKeys.ForIp(context),
                _options.PerIp.PermitLimit,
                TimeSpan.FromSeconds(_options.PerIp.WindowSeconds),
                context.RequestAborted);

            if (!ipDecision.IsAllowed)
            {
                await RejectAsync(context, ipDecision, "ip");
                return;
            }
        }

        // 搶購端點 per-User
        if (_options.FlashSale.Enabled && IsFlashSaleEndpoint(context))
        {
            var userDecision = await _limiter.TryAcquireAsync(
                RateLimitPartitionKeys.ForUser(context),
                _options.FlashSale.PermitLimit,
                TimeSpan.FromSeconds(_options.FlashSale.WindowSeconds),
                context.RequestAborted);

            if (!userDecision.IsAllowed)
            {
                await RejectAsync(context, userDecision, "user");
                return;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// 觀測端點豁免 —— 與 Stage 7 的 [DisableRateLimiting] 相同的理由：
    /// 壓測腳本會高頻輪詢它，被自己要觀測的機制擋下就量不到真實狀態。
    /// </summary>
    private static bool ShouldLimit(HttpContext context)
    {
        var path = context.Request.Path;

        return !path.StartsWithSegments("/api/diagnostics")
               && !path.StartsWithSegments("/health");
    }

    private static bool IsFlashSaleEndpoint(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments("/api/flash-sale")
               && HttpMethods.IsPost(context.Request.Method);
    }

    private async Task RejectAsync(
        HttpContext context,
        RateLimitDecision decision,
        string scope)
    {
        _logger.LogDebug(
            "Rate limited by {Scope}. Path={Path}", scope, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";

        if (decision.RetryAfter > TimeSpan.Zero)
        {
            context.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(decision.RetryAfter.TotalSeconds))
                .ToString(NumberFormatInfo.InvariantInfo);
        }

        var payload = JsonSerializer.Serialize(new
        {
            status = StatusCodes.Status429TooManyRequests,
            message = "Too many requests, please slow down.",
            traceId = context.TraceIdentifier
        });

        await context.Response.WriteAsync(payload);
    }
}
