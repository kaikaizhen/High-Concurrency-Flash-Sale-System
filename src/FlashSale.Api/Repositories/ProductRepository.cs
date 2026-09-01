using System.Data;
using System.Data.Common;
using FlashSale.Api.Common.Enums;
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

    public async Task RestoreStockAsync(int productId, int quantity)
    {
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE Products
            SET Stock = Stock + {quantity}
            WHERE Id = {productId}");
    }

    public async Task<int> TryPurchaseInSingleRoundTripAsync(
        int productId,
        int quantity,
        int userId,
        string? idempotencyKey,
        DateTime createdAt)
    {
        // 整段交易在**一次網路往返**內完成。
        //
        // BEGIN TRAN 與 COMMIT 都在同一個命令文字裡，因此庫存那一列的
        // 排他鎖只在伺服器端執行期間持有，不再橫跨三次往返延遲。
        //
        // XACT_ABORT ON：任何錯誤（例如 IdempotencyKey 撞唯一索引）
        // 都會自動回滾整筆交易。少了它，INSERT 失敗時庫存已扣的部分
        // 會留在資料庫裡 —— 憑空少一件庫存卻沒有訂單。
        //
        // 用原生 ADO.NET 而不是 EF 的 SqlQuery：後者會試圖在這段 SQL
        // 外面再包一層 SELECT 來組合查詢，多語句批次無法被這樣包裝
        // （"non-composable SQL" 錯誤）。
        const string sql = @"
            SET NOCOUNT ON;
            SET XACT_ABORT ON;

            BEGIN TRANSACTION;

            UPDATE Products
            SET Stock = Stock - @quantity
            WHERE Id = @productId
              AND Stock >= @quantity;

            IF @@ROWCOUNT = 1
            BEGIN
                INSERT INTO Orders
                    (UserId, ProductId, Quantity, Status, CreatedAt, IdempotencyKey)
                VALUES
                    (@userId, @productId, @quantity, @status,
                     @createdAt, @idempotencyKey);

                SELECT CAST(SCOPE_IDENTITY() AS int);
            END
            ELSE
            BEGIN
                SELECT CAST(0 AS int);
            END

            COMMIT TRANSACTION;";

        var connection = _dbContext.Database.GetDbConnection();

        // DbContext 的連線可能尚未開啟；由我們開啟的就由我們關閉。
        var openedHere = connection.State != ConnectionState.Open;

        if (openedHere)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();

            command.CommandText = sql;

            AddParameter(command, "@productId", productId);
            AddParameter(command, "@quantity", quantity);
            AddParameter(command, "@userId", userId);
            AddParameter(command, "@status", (int)OrderStatus.Completed);
            AddParameter(command, "@createdAt", createdAt);
            AddParameter(command, "@idempotencyKey",
                (object?)idempotencyKey ?? DBNull.Value);

            var result = await command.ExecuteScalarAsync();

            return result is null or DBNull ? 0 : Convert.ToInt32(result);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = name;
        parameter.Value = value;

        command.Parameters.Add(parameter);
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
