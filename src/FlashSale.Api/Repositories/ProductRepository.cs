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

    public async Task<Product?> GetByIdWithUpdateLockAsync(int id)
    {
        // 必須列出欄位而非 SELECT *，因為 FromSql 需要對應 Entity 的所有屬性。
        return await _dbContext.Products
            .FromSqlInterpolated($@"
                SELECT Id, Name, Price, Stock, CreatedAt, RowVersion
                FROM Products WITH (UPDLOCK, ROWLOCK)
                WHERE Id = {id}")
            .FirstOrDefaultAsync();
    }

    public async Task<bool> TryUpdateWithVersionAsync(Product entity)
    {
        _dbContext.Products.Update(entity);

        try
        {
            await _dbContext.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // 版本不符，代表這一列在我們讀取之後已被其他請求改過。
            //
            // 例外發生後這個 Entity 仍留在變更追蹤器中且狀態不一致，
            // 必須先卸離，否則下一輪重試讀回來的資料會被舊快取覆蓋。
            _dbContext.Entry(entity).State = EntityState.Detached;

            return false;
        }
    }

    public async Task<int> TryDeductStockAsync(int productId, int quantity)
    {
        return await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE Products
            SET Stock = Stock - {quantity}
            WHERE Id = {productId}
              AND Stock >= {quantity}");
    }

    public async Task OverwriteStockWithoutVersionCheckAsync(
        int productId,
        int stock)
    {
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE Products
            SET Stock = {stock}
            WHERE Id = {productId}");
    }
}
