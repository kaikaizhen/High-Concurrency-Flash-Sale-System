using FlashSale.Api.Models.Dtos.Orders;

namespace FlashSale.Api.Models.Dtos.FlashSales;

public class FlashSalePurchaseDtoModel
{
    /// <summary>true = 訂單尚未建立，已排入佇列（202 Accepted）。</summary>
    public bool IsQueued { get; set; }

    public Guid RequestId { get; set; }

    /// <summary>僅同步路徑有值。</summary>
    public OrderDtoModel? Order { get; set; }
}
