namespace FlashSale.Api.Options;

public class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// 關閉時所有讀取直接走資料庫。
    /// Stage 4 的 Before / After 量測就是靠切換這個旗標。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 商品快取存活時間（秒）。
    ///
    /// 商品的 Stock 會被搶購改動而快取不會同步更新，
    /// 因此 TTL 同時也是「庫存顯示值最多可能過期多久」。
    /// 這不會造成超賣 —— 真正的庫存判斷在資料庫端的 Atomic Update。
    /// </summary>
    public int TtlSeconds { get; set; } = 10;

    /// <summary>
    /// 「查無此商品」的快取存活時間（秒）。用於防止 Cache Penetration。
    /// 必須比正常 TTL 短很多，否則新建立的商品會有一段時間查不到。
    /// </summary>
    public int NullTtlSeconds { get; set; } = 3;

    /// <summary>
    /// 快取「不存在」這件事本身。
    ///
    /// 關閉時，針對不存在的 Key 的請求每一次都會打到資料庫（Cache Penetration）。
    /// </summary>
    public bool EnableNullCaching { get; set; } = true;

    /// <summary>
    /// 同一個 Key 同時 Miss 時，只讓一個請求去查資料庫，其餘等待其結果。
    ///
    /// 關閉時，快取失效瞬間 N 個併發請求會產生 N 次資料庫查詢
    /// （Cache Stampede / Breakdown）。
    /// </summary>
    public bool EnableSingleFlight { get; set; } = true;
}
