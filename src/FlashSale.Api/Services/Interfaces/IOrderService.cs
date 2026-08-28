using FlashSale.Api.Models.Dtos.Orders;

namespace FlashSale.Api.Services.Interfaces;

public interface IOrderService
{
    Task<List<OrderDtoModel>> GetListByProductIdAsync(int productId);

    Task<OrderDtoModel> GetByIdAsync(int id);

    Task<OrderDtoModel> CreateAsync(CreateOrderDtoModel dto);
}
