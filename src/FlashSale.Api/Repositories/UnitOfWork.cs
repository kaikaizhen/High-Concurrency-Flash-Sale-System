using FlashSale.Api.Data;
using FlashSale.Api.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore.Storage;

namespace FlashSale.Api.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _dbContext;

    public UnitOfWork(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync()
    {
        var transaction = await _dbContext.Database.BeginTransactionAsync();

        return new UnitOfWorkTransaction(transaction);
    }

    private sealed class UnitOfWorkTransaction : IUnitOfWorkTransaction
    {
        private readonly IDbContextTransaction _transaction;

        public UnitOfWorkTransaction(IDbContextTransaction transaction)
        {
            _transaction = transaction;
        }

        public Task CommitAsync()
        {
            return _transaction.CommitAsync();
        }

        public Task RollbackAsync()
        {
            return _transaction.RollbackAsync();
        }

        // 未 Commit 就 Dispose 會自動 Rollback，
        // 因此 Service 只要用 await using 包住即可保證不會留下未結束的交易。
        public ValueTask DisposeAsync()
        {
            return _transaction.DisposeAsync();
        }
    }
}
