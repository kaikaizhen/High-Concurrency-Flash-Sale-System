using FlashSale.Api.Data;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using Microsoft.Data.SqlClient;
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

    public async Task<bool> TryCreateAsync(Order entity)
    {
        try
        {
            await _dbContext.Orders.AddAsync(entity);
            await _dbContext.SaveChangesAsync();

            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // 篩選唯一索引擋下了重複的 IdempotencyKey。
            // 失敗的 Entity 必須卸離，否則同一個 DbContext 的後續操作
            // 會再次嘗試送出它。
            _dbContext.Entry(entity).State = EntityState.Detached;

            return false;
        }
    }

    /// <summary>
    /// 2601 = 唯一索引重複鍵；2627 = 唯一/主鍵約束違反。
    ///
    /// 必須明確區分，不能吞掉所有 DbUpdateException ——
    /// 逾時、連線中斷這些都該往上拋讓重試機制處理，
    /// 當成「已存在」會靜默地遺失訂單。
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqlException sql &&
               sql.Number is 2601 or 2627;
    }
}
