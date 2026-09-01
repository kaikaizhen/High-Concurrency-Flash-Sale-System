using FlashSale.Api.Models.Dtos.FlashSales;

namespace FlashSale.Api.Services.Interfaces;

public interface IFlashSaleService
{
    Task<FlashSalePurchaseDtoModel> PurchaseAsync(CreateFlashSaleDtoModel dto);
}
