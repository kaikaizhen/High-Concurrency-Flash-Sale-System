using FlashSale.Api.Models.ViewModels.Orders;

namespace FlashSale.Api.Models.ViewModels.FlashSales;

public class FlashSaleResultViewModel
{
    /// <summary><c>Completed</c>（訂單已建立）或 <c>Queued</c>（已受理，稍後建立）。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>追蹤碼。Queued 時客戶端可用它查詢後續狀態。</summary>
    public Guid RequestId { get; set; }

    /// <summary>Queued 時為 null —— 訂單此刻還不存在。</summary>
    public OrderViewModel? Order { get; set; }
}
