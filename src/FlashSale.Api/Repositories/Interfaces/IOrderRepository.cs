using FlashSale.Api.Models.Entities;

namespace FlashSale.Api.Repositories.Interfaces;

public interface IOrderRepository
{
    Task<List<Order>> GetListByProductIdAsync(int productId);

    Task<Order?> GetByIdAsync(int id);

    Task<int> CountByProductIdAsync(int productId);

    Task CreateAsync(Order entity);
}
