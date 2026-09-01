namespace FlashSale.Api.Common.Constants;

public static class RateLimitPolicies
{
    /// <summary>
    /// 搶購端點專屬政策（per-User）。
    ///
    /// 與全域 per-IP 限制並存 —— 兩者都會生效，任一擋下就是 429。
    /// 這是刻意的分層：per-IP 擋單一來源的洪水，
    /// per-User 擋分散在多個來源但屬於同一個人的洗版。
    /// </summary>
    public const string FlashSale = "flash-sale";
}
