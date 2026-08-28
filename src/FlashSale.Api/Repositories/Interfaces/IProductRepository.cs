using FlashSale.Api.Models.Entities;

namespace FlashSale.Api.Repositories.Interfaces;

public interface IProductRepository
{
    Task<List<Product>> GetListAsync();

    Task<Product?> GetByIdAsync(int id);

    Task<bool> ExistsByNameAsync(string name);

    Task CreateAsync(Product entity);

    Task UpdateAsync(Product entity);
}
