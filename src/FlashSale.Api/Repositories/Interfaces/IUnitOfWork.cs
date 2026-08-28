namespace FlashSale.Api.Repositories.Interfaces;

/// <summary>
/// 交易邊界。
///
/// Guideline §19：Transaction 的流程邊界由 Service 決定。
/// 但 EF Core 的交易 API 屬於資料存取層，因此包一層抽象，
/// 讓 Service 能夠控制邊界而不必碰到 DbContext。
/// </summary>
public interface IUnitOfWork
{
    Task<IUnitOfWorkTransaction> BeginTransactionAsync();
}

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync();

    Task RollbackAsync();
}
