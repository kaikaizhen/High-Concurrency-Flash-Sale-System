using FlashSale.Api.Common.Enums;
using FlashSale.Api.Data;
using FlashSale.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Infrastructure.Idempotency;

/// <summary>
/// 以 SQL Server 實作冪等記錄。
///
/// 原子性來自**主鍵衝突**：兩個併發 INSERT 只有一個會成功，
/// 另一個會收到 duplicate key 錯誤。效果等同 Redis 的 SET NX。
///
/// 與 Redis 版的取捨見 docs/idempotency.md。
/// </summary>
public class SqlServerIdempotencyStore : IIdempotencyStore
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<SqlServerIdempotencyStore> _logger;

    public SqlServerIdempotencyStore(
        AppDbContext dbContext,
        ILogger<SqlServerIdempotencyStore> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<IdempotencyEntry?> TryAcquireAsync(string key, TimeSpan ttl)
    {
        var now = DateTime.UtcNow;

        var record = new IdempotencyRecord
        {
            Key = key,
            Status = IdempotencyStatus.InProgress,
            CreatedAt = now,
            ExpiresAt = now.Add(ttl)
        };

        try
        {
            _dbContext.IdempotencyRecords.Add(record);
            await _dbContext.SaveChangesAsync();

            return null;
        }
        catch (DbUpdateException)
        {
            // 主鍵衝突 —— 已經有人佔用了。
            // 必須把失敗的 Entity 卸離，否則後續查詢會被變更追蹤器干擾。
            _dbContext.Entry(record).State = EntityState.Detached;
        }

        var existing = await _dbContext.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == key);

        if (existing is null)
        {
            _logger.LogWarning(
                "Idempotency key vanished after duplicate key error. Key={Key}", key);

            return null;
        }

        // SQL Server 沒有自動過期，必須自己判斷。
        // 過期記錄視同不存在，直接接手（覆寫成新的 InProgress）。
        if (existing.ExpiresAt <= now)
        {
            existing.Status = IdempotencyStatus.InProgress;
            existing.StatusCode = 0;
            existing.ResponseBody = null;
            existing.CreatedAt = now;
            existing.ExpiresAt = now.Add(ttl);

            _dbContext.IdempotencyRecords.Update(existing);
            await _dbContext.SaveChangesAsync();

            return null;
        }

        return new IdempotencyEntry
        {
            Status = existing.Status,
            StatusCode = existing.StatusCode,
            ResponseBody = existing.ResponseBody
        };
    }

    public async Task CompleteAsync(
        string key,
        int statusCode,
        string? responseBody,
        TimeSpan ttl)
    {
        var record = await _dbContext.IdempotencyRecords
            .FirstOrDefaultAsync(x => x.Key == key);

        if (record is null)
        {
            return;
        }

        record.Status = IdempotencyStatus.Completed;
        record.StatusCode = statusCode;
        record.ResponseBody = responseBody;
        record.ExpiresAt = DateTime.UtcNow.Add(ttl);

        await _dbContext.SaveChangesAsync();
    }

    public async Task ReleaseAsync(string key)
    {
        await _dbContext.IdempotencyRecords
            .Where(x => x.Key == key)
            .ExecuteDeleteAsync();
    }
}
