namespace FlashSale.Api.Common.Constants;

public static class GlobalConstants
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>
    /// Optimistic Concurrency 的最大重試次數。
    ///
    /// 秒殺是高衝突場景，重試次數不足會讓大量請求被誤判為失敗；
    /// 設太高則會在高併發下把 Latency 拉長。這個數值本身就是
    /// Stage 3 要觀察的取捨之一。
    /// </summary>
    public const int MaxConcurrencyRetryCount = 10;
}
