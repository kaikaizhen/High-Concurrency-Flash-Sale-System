using FlashSale.Api.Common.Enums;

namespace FlashSale.Api.Models.Dtos.FlashSales;

public class CreateFlashSaleDtoModel
{
    public int ProductId { get; set; }

    public int UserId { get; set; }

    public int Quantity { get; set; }

    /// <summary>
    /// 併發控制策略。Stage 3 比較用；未指定時為專案選定的主要方案。
    /// </summary>
    public FlashSaleStrategy Strategy { get; set; } = FlashSaleStrategy.Atomic;

    /// <summary>
    /// 客戶端帶來的 Idempotency-Key（來自 HTTP Header，由 Controller 填入）。
    ///
    /// 會被寫進 <see cref="Models.Entities.Order.IdempotencyKey"/>，
    /// 讓資料庫的唯一索引成為重複訂單的最後一道防線 ——
    /// 即使 IdempotencyFilter 因為 Redis 故障而失效，也不會建立第二筆。
    ///
    /// 非同步路徑不使用這個值，改用訊息的 MessageId（見
    /// QueuedAtomicFlashSalePurchaseStrategy）。
    /// </summary>
    public string? IdempotencyKey { get; set; }
}
