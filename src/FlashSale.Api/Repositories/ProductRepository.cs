using FlashSale.Api.Data;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Repositories;

public class ProductRepository : IProductRepository
{
    private readonly AppDbContext _dbContext;

    public ProductRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<Product>> GetListAsync()
    {
        return await _dbContext.Products
            .OrderBy(x => x.Id)
            .ToListAsync();
    }

    public async Task<Product?> GetByIdAsync(int id)
    {
        return await _dbContext.Products
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<bool> ExistsByNameAsync(string name)
    {
        return await _dbContext.Products
            .AnyAsync(x => x.Name == name);
    }

    public async Task CreateAsync(Product entity)
    {
        await _dbContext.Products.AddAsync(entity);
        await _dbContext.SaveChangesAsync();
    }

    public async Task UpdateAsync(Product entity)
    {
        _dbContext.Products.Update(entity);
        await _dbContext.SaveChangesAsync();
    }
}
