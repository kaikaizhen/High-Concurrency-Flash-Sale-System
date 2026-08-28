using FlashSale.Api.Common.Enums;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;

namespace FlashSale.Api.Services.Interfaces;

/// <summary>
/// 一種併發控制做法。Stage 3 讓四個實作並存以便互相比較。
/// </summary>
public interface IFlashSalePurchaseStrategy
{
    FlashSaleStrategy Strategy { get; }

    /// <summary>
    /// 扣減庫存並建立訂單。
    /// </summary>
    /// <exception cref="Common.Exceptions.NotFoundException">商品不存在。</exception>
    /// <exception cref="Common.Exceptions.BusinessException">庫存不足或衝突重試次數用盡。</exception>
    Task<Order> PurchaseAsync(CreateFlashSaleDtoModel dto);
}
