using FlashSale.Api.Data;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly AppDbContext _dbContext;

    public OrderRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<Order>> GetListByProductIdAsync(int productId)
    {
        return await _dbContext.Orders
            .Where(x => x.ProductId == productId)
            .OrderBy(x => x.Id)
            .ToListAsync();
    }

    public async Task<Order?> GetByIdAsync(int id)
    {
        return await _dbContext.Orders
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<int> CountByProductIdAsync(int productId)
    {
        return await _dbContext.Orders
            .CountAsync(x => x.ProductId == productId);
    }

    public async Task CreateAsync(Order entity)
    {
        await _dbContext.Orders.AddAsync(entity);
        await _dbContext.SaveChangesAsync();
    }
}
