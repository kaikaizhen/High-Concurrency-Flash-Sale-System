using FlashSale.Api.Common.Enums;

namespace FlashSale.Api.Models.Entities;

public class Order
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public OrderStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 這筆訂單所對應的唯一請求識別碼。
    ///
    /// 同步路徑 = 客戶端帶來的 Idempotency-Key；
    /// 非同步路徑 = 訊息的 MessageId。
    ///
    /// 資料庫上有篩選唯一索引（見 OrderConfiguration），
    /// 因此**即使前面所有防護都失效**，同一個識別碼也不可能建立兩筆訂單。
    /// 這是最後一道防線 —— 快取會過期、Redis 會掛、程式會有 bug，
    /// 但資料庫約束不會。
    ///
    /// 允許為 null：Stage 6 之前建立的訂單、以及沒帶 Key 的請求。
    /// </summary>
    public string? IdempotencyKey { get; set; }
}
