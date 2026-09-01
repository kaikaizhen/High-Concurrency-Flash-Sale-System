using System.Diagnostics.Metrics;
using FlashSale.Api.Common.Enums;

namespace FlashSale.Api.Infrastructure.Observability;

/// <summary>
/// 業務層級的指標。
///
/// ASP.NET Core 與 SqlClient 的內建 instrumentation 已經提供
/// HTTP 請求數／延遲／錯誤數、DB 命令耗時這些**技術指標**。
/// 這裡補的是它們看不到的**業務指標**：
///
///   賣掉幾件？因為什麼原因被拒絕？快取命中率多少？
///
/// 「HTTP 200 的比例」回答不了「有沒有人買到東西」——
/// 秒殺賣完之後全部回 409，技術指標看起來一切正常，
/// 但業務上已經結束了。
/// </summary>
public class FlashSaleMetrics
{
    public const string MeterName = "FlashSale.Api";

    private readonly Counter<long> _purchaseAttempts;
    private readonly Counter<long> _ordersCreated;
    private readonly Counter<long> _purchaseRejected;
    private readonly Counter<long> _cacheLookups;
    private readonly Histogram<double> _purchaseDuration;

    public FlashSaleMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _purchaseAttempts = meter.CreateCounter<long>(
            "flashsale.purchase.attempts",
            unit: "{request}",
            description: "搶購請求總數，含成功與被拒絕的。");

        // 這個計數器的變化率就是計畫 §15 要的 Orders/sec。
        // 用 Counter 而不是 Gauge：Counter 只增不減，
        // 任何時間區間的差值都是那段期間的成交量，
        // 重啟或取樣遺漏都不會讓數字失真。
        _ordersCreated = meter.CreateCounter<long>(
            "flashsale.orders.created",
            unit: "{order}",
            description: "實際成交的訂單數。");

        _purchaseRejected = meter.CreateCounter<long>(
            "flashsale.purchase.rejected",
            unit: "{request}",
            description: "被拒絕的搶購，以原因分類。");

        _cacheLookups = meter.CreateCounter<long>(
            "flashsale.cache.lookups",
            unit: "{lookup}",
            description: "快取查詢，以命中與否分類。");

        _purchaseDuration = meter.CreateHistogram<double>(
            "flashsale.purchase.duration",
            unit: "ms",
            description: "搶購處理耗時，不含 HTTP 管線。");
    }

    public void RecordAttempt(FlashSaleStrategy strategy)
    {
        _purchaseAttempts.Add(1, new KeyValuePair<string, object?>(
            "strategy", strategy.ToString()));
    }

    /// <summary>
    /// <paramref name="queued"/> 區分同步成交與排入佇列 ——
    /// 後者此刻訂單還不存在，兩者不能混為一談。
    /// </summary>
    public void RecordOrderCreated(FlashSaleStrategy strategy, bool queued)
    {
        _ordersCreated.Add(
            1,
            new KeyValuePair<string, object?>("strategy", strategy.ToString()),
            new KeyValuePair<string, object?>("queued", queued));
    }

    /// <summary>
    /// <paramref name="reason"/> 是這個指標的價值所在。
    ///
    /// 「拒絕數上升」本身沒有資訊量：庫存賣完是正常的，
    /// 樂觀鎖重試用盡代表系統過載，兩者的處置完全不同。
    /// </summary>
    public void RecordRejected(FlashSaleStrategy strategy, string reason)
    {
        _purchaseRejected.Add(
            1,
            new KeyValuePair<string, object?>("strategy", strategy.ToString()),
            new KeyValuePair<string, object?>("reason", reason));
    }

    public void RecordCacheLookup(bool hit)
    {
        _cacheLookups.Add(1, new KeyValuePair<string, object?>(
            "result", hit ? "hit" : "miss"));
    }

    public void RecordPurchaseDuration(
        FlashSaleStrategy strategy,
        double milliseconds)
    {
        _purchaseDuration.Record(milliseconds, new KeyValuePair<string, object?>(
            "strategy", strategy.ToString()));
    }
}
