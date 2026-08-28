namespace FlashSale.Api.Models.Messages;

/// <summary>
/// 「庫存已扣減，請建立訂單」事件。
///
/// 這是第五種 Model —— 訊息契約。它與 DtoModel 的差別在於**跨行程邊界**：
/// API 發布、Worker 消費，兩邊各自部署、各自升級。
/// 因此它的欄位一旦上線就不能隨意刪改，必須向後相容
/// （新增欄位可以，改名或改型別不行）。
///
/// 也因為這個原因，這裡不重用 CreateFlashSaleDtoModel ——
/// 內部傳遞用的 DTO 可以隨時重構，訊息契約不行。
/// </summary>
public class OrderCreatedMessage
{
    /// <summary>
    /// 訊息唯一識別碼。
    ///
    /// RabbitMQ 保證的是 at-least-once，重試與網路重送都可能讓同一則訊息
    /// 被消費兩次。Stage 6 會用這個 Id 做去重，在那之前
    /// **本階段確實可能產生重複訂單** —— 這是刻意留給下一階段的問題。
    /// </summary>
    public Guid MessageId { get; set; }

    public int UserId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    /// <summary>庫存實際被扣減的時間，不是訊息被消費的時間。</summary>
    public DateTime OccurredAt { get; set; }
}
