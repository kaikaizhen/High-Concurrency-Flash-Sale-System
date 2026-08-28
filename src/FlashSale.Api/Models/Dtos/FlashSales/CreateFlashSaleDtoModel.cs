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
}
