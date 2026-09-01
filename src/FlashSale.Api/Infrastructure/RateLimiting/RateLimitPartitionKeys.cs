namespace FlashSale.Api.Infrastructure.RateLimiting;

/// <summary>
/// 決定一個請求屬於哪一個限流分區。
///
/// 「分區」就是計數的單位 —— 同一個分區的請求共用一份額度。
/// 分區選錯，限流不是形同虛設就是誤傷正常使用者：
///
///   全部算在一起  → 一個人洗版就把所有人擋住
///   每個請求一區  → 等於沒有限流
/// </summary>
public static class RateLimitPartitionKeys
{
    /// <summary>
    /// 使用者識別的來源 Header。
    ///
    /// 在有認證的系統中這應該來自 JWT claim 而不是 Header ——
    /// Header 是客戶端說了算的，換一個值就能繞過限制。
    /// 本專案尚未導入認證，因此暫時用 Header，並保留 IP 作為退路。
    /// </summary>
    public const string UserHeaderName = "X-User-Id";

    private const string AnonymousPrefix = "ip:";
    private const string UserPrefix = "user:";
    private const string UnknownIp = "unknown";

    /// <summary>
    /// per-IP 分區鍵。用於全域限制。
    /// </summary>
    public static string ForIp(HttpContext context)
    {
        return AnonymousPrefix + GetIp(context);
    }

    /// <summary>
    /// per-User 分區鍵，沒有使用者識別時退回 per-IP。
    ///
    /// 退回 IP 而不是「不限制」是刻意的：
    /// 若匿名請求完全不受限，攻擊者只要不帶 Header 就能繞過。
    /// </summary>
    public static string ForUser(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(UserHeaderName, out var values))
        {
            var userId = values.ToString().Trim();

            if (!string.IsNullOrEmpty(userId))
            {
                return UserPrefix + userId;
            }
        }

        return ForIp(context);
    }

    private static string GetIp(HttpContext context)
    {
        // RemoteIpAddress 在反向代理後面會是代理的 IP。
        // Stage 8 導入 Nginx 之後必須設定 ForwardedHeaders，
        // 否則所有流量會被算成同一個分區 —— 限流會誤傷所有人。
        return context.Connection.RemoteIpAddress?.ToString() ?? UnknownIp;
    }
}
