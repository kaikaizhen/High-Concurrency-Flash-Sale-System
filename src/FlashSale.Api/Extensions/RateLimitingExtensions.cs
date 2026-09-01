using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using FlashSale.Api.Common.Constants;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Infrastructure.RateLimiting;
using FlashSale.Api.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FlashSale.Api.Extensions;

public static class RateLimitingExtensions
{
    public static IServiceCollection AddApplicationRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection(RateLimitOptions.SectionName)
            .Get<RateLimitOptions>() ?? new RateLimitOptions();

        if (!options.Enabled)
        {
            return services;
        }

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            ConfigureGlobalIpLimiter(limiter, options.PerIp);
            ConfigureFlashSalePolicy(limiter, options.FlashSale);

            limiter.OnRejected = WriteRejectionAsync;
        });

        return services;
    }

    /// <summary>
    /// 全域 per-IP 限制。
    ///
    /// 用固定視窗即可 —— 這一層的目的是擋住單一來源的洪水，
    /// 額度設得比正常流量高一個數量級，邊界爆發問題在這個尺度上無關緊要。
    /// 真正需要精確控制的是端點層級的限制。
    /// </summary>
    private static void ConfigureGlobalIpLimiter(
        RateLimiterOptions limiter,
        RateLimitOptions.IpLimitOptions options)
    {
        if (!options.Enabled)
        {
            return;
        }

        limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
            context => RateLimitPartition.GetFixedWindowLimiter(
                RateLimitPartitionKeys.ForIp(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = options.QueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));
    }

    /// <summary>
    /// 搶購端點政策（per-User）。演算法由設定決定，供 Stage 7 比較。
    /// </summary>
    private static void ConfigureFlashSalePolicy(
        RateLimiterOptions limiter,
        RateLimitOptions.FlashSaleLimitOptions options)
    {
        limiter.AddPolicy(
            RateLimitPolicies.FlashSale,
            context =>
            {
                if (!options.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                var key = RateLimitPartitionKeys.ForUser(context);

                return options.Algorithm switch
                {
                    RateLimitAlgorithm.FixedWindow =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            key,
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = options.PermitLimit,
                                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                                QueueLimit = options.QueueLimit,
                                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                            }),

                    RateLimitAlgorithm.SlidingWindow =>
                        RateLimitPartition.GetSlidingWindowLimiter(
                            key,
                            _ => new SlidingWindowRateLimiterOptions
                            {
                                PermitLimit = options.PermitLimit,
                                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                                SegmentsPerWindow = options.SegmentsPerWindow,
                                QueueLimit = options.QueueLimit,
                                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                            }),

                    RateLimitAlgorithm.TokenBucket =>
                        RateLimitPartition.GetTokenBucketLimiter(
                            key,
                            _ => new TokenBucketRateLimiterOptions
                            {
                                TokenLimit = options.TokenLimit,
                                TokensPerPeriod = options.TokensPerPeriod,
                                ReplenishmentPeriod =
                                    TimeSpan.FromSeconds(options.ReplenishmentPeriodSeconds),
                                AutoReplenishment = true,
                                QueueLimit = options.QueueLimit,
                                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                            }),

                    RateLimitAlgorithm.Concurrency =>
                        RateLimitPartition.GetConcurrencyLimiter(
                            key,
                            _ => new ConcurrencyLimiterOptions
                            {
                                PermitLimit = options.ConcurrencyLimit,
                                QueueLimit = options.QueueLimit,
                                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                            }),

                    _ => RateLimitPartition.GetNoLimiter(key)
                };
            });
    }

    /// <summary>
    /// 被拒絕時的回應。
    ///
    /// 格式與 GlobalExceptionMiddleware 一致，客戶端不需要因為
    /// 錯誤來源不同而處理兩種格式。
    /// </summary>
    private static ValueTask WriteRejectionAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        // Retry-After 讓客戶端知道「等多久再試」而不是立刻重試。
        // 少了它，被拒絕的客戶端往往會馬上重送，讓情況更糟。
        if (context.Lease.TryGetMetadata(
                MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(NumberFormatInfo.InvariantInfo);
        }

        context.HttpContext.Response.ContentType = "application/json";

        var payload = JsonSerializer.Serialize(new
        {
            status = StatusCodes.Status429TooManyRequests,
            message = "Too many requests, please slow down.",
            traceId = context.HttpContext.TraceIdentifier
        });

        return new ValueTask(
            context.HttpContext.Response.WriteAsync(payload, cancellationToken));
    }
}
