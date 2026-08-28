using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Dtos.Orders;

namespace FlashSale.Api.Services.Interfaces;

public interface IFlashSaleService
{
    Task<OrderDtoModel> PurchaseAsync(CreateFlashSaleDtoModel dto);
}
