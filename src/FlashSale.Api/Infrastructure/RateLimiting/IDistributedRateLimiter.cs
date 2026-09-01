namespace FlashSale.Api.Infrastructure.RateLimiting;

public readonly record struct RateLimitDecision(
    bool IsAllowed,
    long Remaining,
    TimeSpan RetryAfter)
{
    public static RateLimitDecision Allow(long remaining) =>
        new(true, remaining, TimeSpan.Zero);

    public static RateLimitDecision Deny(TimeSpan retryAfter) =>
        new(false, 0, retryAfter);
}

/// <summary>
/// 跨 Instance 共用額度的限流器。
///
/// ASP.NET Core 內建的 RateLimiter 把計數放在行程記憶體中，
/// N 台機器各有一份獨立的額度 —— **實際限制會變成 N 倍**。
/// Stage 7 的文件已標記這個問題，這裡是它的解法。
/// </summary>
public interface IDistributedRateLimiter
{
    /// <summary>
    /// 嘗試取用一次額度。
    /// </summary>
    /// <param name="partitionKey">計數單位，例如 <c>user:42</c>。</param>
    Task<RateLimitDecision> TryAcquireAsync(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken = default);
}
