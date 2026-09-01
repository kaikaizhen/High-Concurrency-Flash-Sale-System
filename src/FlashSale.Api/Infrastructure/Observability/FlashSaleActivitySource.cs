using System.Diagnostics;

namespace FlashSale.Api.Infrastructure.Observability;

/// <summary>
/// 自訂的 Trace 來源。
///
/// ASP.NET Core 與 SqlClient 的內建 instrumentation 會產生
/// 「HTTP 請求」與「SQL 命令」兩層 Span，但中間是空的 ——
/// 看得到「請求花了 900ms」和「這個 UPDATE 花了 5ms」，
/// 卻看不出剩下的 895ms 花在哪裡。
///
/// 這裡補上業務層的 Span：搶購用了哪個策略、等鎖多久、
/// 樂觀鎖重試了幾次。那才是回答「為什麼慢」所需要的資訊。
/// </summary>
public static class FlashSaleActivitySource
{
    public const string Name = "FlashSale.Api";

    private static readonly ActivitySource Source = new(Name);

    public static Activity? StartPurchase(string strategy, int productId)
    {
        var activity = Source.StartActivity(
            $"FlashSale Purchase {strategy}",
            ActivityKind.Internal);

        // 標籤要放「之後會拿來篩選或分組」的東西。
        // ProductId 是熱點分析的關鍵 —— 秒殺時流量會集中在少數幾個商品上。
        activity?.SetTag("flashsale.strategy", strategy);
        activity?.SetTag("flashsale.product_id", productId);

        return activity;
    }

    public static Activity? StartCacheLoad(string cacheKey)
    {
        var activity = Source.StartActivity(
            "Cache Load",
            ActivityKind.Internal);

        activity?.SetTag("cache.key", cacheKey);

        return activity;
    }

    /// <summary>
    /// 發布訊息的 Span 用 Producer 類型。
    ///
    /// 這讓 Tracing 系統知道它與 Worker 端的 Consumer Span 是同一條鏈路的
    /// 兩端，而不是兩個不相干的操作。
    /// </summary>
    public static Activity? StartPublish(string exchange, string routingKey)
    {
        var activity = Source.StartActivity(
            $"publish {exchange}",
            ActivityKind.Producer);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", exchange);
        activity?.SetTag("messaging.rabbitmq.routing_key", routingKey);

        return activity;
    }

    public static Activity? StartConsume(string queue)
    {
        var activity = Source.StartActivity(
            $"consume {queue}",
            ActivityKind.Consumer);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", queue);

        return activity;
    }
}
