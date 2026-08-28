using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.FlashSaleStrategies;
using FlashSale.Api.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FlashSale.UnitTests.Services;

/// <summary>
/// Stage 3 三種併發控制版本的行為測試。
///
/// 這裡驗證的是**流程正確性**（有沒有用對存取方式、失敗時有沒有回滾、
/// 衝突時有沒有重試），不是併發正確性 ——
/// 真正的併發行為只能在真實 SQL Server 上用 k6 驗證，
/// 見 docs/concurrency-comparison.md。
/// </summary>
public class FlashSaleStrategyTests
{
    private readonly Mock<IProductRepository> _productRepository = new();
    private readonly Mock<IOrderRepository> _orderRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IUnitOfWorkTransaction> _transaction = new();

    public FlashSaleStrategyTests()
    {
        _unitOfWork
            .Setup(x => x.BeginTransactionAsync())
            .ReturnsAsync(_transaction.Object);

        _transaction.Setup(x => x.CommitAsync()).Returns(Task.CompletedTask);
        _transaction.Setup(x => x.RollbackAsync()).Returns(Task.CompletedTask);
        _transaction.Setup(x => x.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _orderRepository
            .Setup(x => x.CreateAsync(It.IsAny<Order>()))
            .Returns(Task.CompletedTask);
    }

    private static CreateFlashSaleDtoModel Dto(int quantity = 1)
    {
        return new CreateFlashSaleDtoModel
        {
            ProductId = 1,
            UserId = 99,
            Quantity = quantity
        };
    }

    // ------------------------------------------------------------------
    // Version A — Transaction + 悲觀鎖
    // ------------------------------------------------------------------

    private TransactionFlashSalePurchaseStrategy CreateTransactionSut()
    {
        return new TransactionFlashSalePurchaseStrategy(
            _unitOfWork.Object,
            _productRepository.Object,
            _orderRepository.Object);
    }

    [Fact]
    public void Transaction_ShouldDeclareTransactionStrategy()
    {
        Assert.Equal(
            FlashSaleStrategy.Transaction,
            CreateTransactionSut().Strategy);
    }

    [Fact]
    public async Task Transaction_ShouldReadWithUpdateLock_NotPlainRead()
    {
        _productRepository
            .Setup(x => x.GetByIdWithUpdateLockAsync(1))
            .ReturnsAsync(new Product { Id = 1, Stock = 5 });

        await CreateTransactionSut().PurchaseAsync(Dto());

        // 關鍵：必須走帶 UPDLOCK 的讀取，否則交易本身擋不住超賣
        _productRepository.Verify(
            x => x.GetByIdWithUpdateLockAsync(1),
            Times.Once);

        _productRepository.Verify(
            x => x.GetByIdAsync(It.IsAny<int>()),
            Times.Never);

        _transaction.Verify(x => x.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task Transaction_WhenStockIsInsufficient_ShouldNotCommit()
    {
        _productRepository
            .Setup(x => x.GetByIdWithUpdateLockAsync(1))
            .ReturnsAsync(new Product { Id = 1, Stock = 0 });

        await Assert.ThrowsAsync<BusinessException>(
            () => CreateTransactionSut().PurchaseAsync(Dto()));

        _orderRepository.Verify(
            x => x.CreateAsync(It.IsAny<Order>()),
            Times.Never);

        // 未 Commit 就離開，DisposeAsync 會自動 Rollback
        _transaction.Verify(x => x.CommitAsync(), Times.Never);
        _transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Transaction_WhenProductNotFound_ShouldThrowNotFound()
    {
        _productRepository
            .Setup(x => x.GetByIdWithUpdateLockAsync(It.IsAny<int>()))
            .ReturnsAsync((Product?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateTransactionSut().PurchaseAsync(Dto()));

        _transaction.Verify(x => x.CommitAsync(), Times.Never);
    }

    // ------------------------------------------------------------------
    // Version B — Optimistic Concurrency
    // ------------------------------------------------------------------

    private OptimisticFlashSalePurchaseStrategy CreateOptimisticSut()
    {
        return new OptimisticFlashSalePurchaseStrategy(
            _unitOfWork.Object,
            _productRepository.Object,
            _orderRepository.Object,
            NullLogger<OptimisticFlashSalePurchaseStrategy>.Instance);
    }

    [Fact]
    public async Task Optimistic_WhenNoConflict_ShouldSucceedOnFirstAttempt()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Product { Id = 1, Stock = 5 });

        _productRepository
            .Setup(x => x.TryUpdateWithVersionAsync(It.IsAny<Product>()))
            .ReturnsAsync(true);

        var order = await CreateOptimisticSut().PurchaseAsync(Dto());

        Assert.Equal(OrderStatus.Completed, order.Status);

        _productRepository.Verify(
            x => x.TryUpdateWithVersionAsync(It.IsAny<Product>()),
            Times.Once);

        _transaction.Verify(x => x.CommitAsync(), Times.Once);
        _transaction.Verify(x => x.RollbackAsync(), Times.Never);
    }

    [Fact]
    public async Task Optimistic_WhenConflictThenSuccess_ShouldRetryAndRollbackFailedAttempt()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(() => new Product { Id = 1, Stock = 5 });

        _productRepository
            .SetupSequence(x => x.TryUpdateWithVersionAsync(It.IsAny<Product>()))
            .ReturnsAsync(false)   // 第一次版本衝突
            .ReturnsAsync(false)   // 第二次還是衝突
            .ReturnsAsync(true);   // 第三次成功

        await CreateOptimisticSut().PurchaseAsync(Dto());

        _productRepository.Verify(
            x => x.TryUpdateWithVersionAsync(It.IsAny<Product>()),
            Times.Exactly(3));

        // 每次重試都必須重新讀取，否則會拿著過期的 RowVersion 一直撞
        _productRepository.Verify(
            x => x.GetByIdAsync(1),
            Times.Exactly(3));

        _transaction.Verify(x => x.RollbackAsync(), Times.Exactly(2));
        _transaction.Verify(x => x.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task Optimistic_WhenRetriesExhausted_ShouldThrowBusinessException()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(() => new Product { Id = 1, Stock = 5 });

        _productRepository
            .Setup(x => x.TryUpdateWithVersionAsync(It.IsAny<Product>()))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<BusinessException>(
            () => CreateOptimisticSut().PurchaseAsync(Dto()));

        _orderRepository.Verify(
            x => x.CreateAsync(It.IsAny<Order>()),
            Times.Never);

        _transaction.Verify(x => x.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Optimistic_WhenStockIsInsufficient_ShouldNotRetry()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Product { Id = 1, Stock = 0 });

        await Assert.ThrowsAsync<BusinessException>(
            () => CreateOptimisticSut().PurchaseAsync(Dto()));

        // 庫存真的不足時重試沒有意義，只會浪費資料庫往返
        _productRepository.Verify(
            x => x.GetByIdAsync(1),
            Times.Once);
    }

    // ------------------------------------------------------------------
    // Version C — Atomic Update
    // ------------------------------------------------------------------

    private AtomicFlashSalePurchaseStrategy CreateAtomicSut()
    {
        return new AtomicFlashSalePurchaseStrategy(
            _unitOfWork.Object,
            _productRepository.Object,
            _orderRepository.Object);
    }

    [Fact]
    public async Task Atomic_WhenDeductSucceeds_ShouldNeverReadStock()
    {
        _productRepository
            .Setup(x => x.TryDeductStockAsync(1, 1))
            .ReturnsAsync(1);

        var order = await CreateAtomicSut().PurchaseAsync(Dto());

        Assert.Equal(OrderStatus.Completed, order.Status);

        // 成功路徑上完全不需要 SELECT 庫存 —— 這是它比另外兩版少一次往返的原因
        _productRepository.Verify(
            x => x.GetByIdAsync(It.IsAny<int>()),
            Times.Never);

        _productRepository.Verify(
            x => x.GetByIdWithUpdateLockAsync(It.IsAny<int>()),
            Times.Never);

        _transaction.Verify(x => x.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task Atomic_WhenNoRowAffectedAndProductExists_ShouldThrowInsufficientStock()
    {
        _productRepository
            .Setup(x => x.TryDeductStockAsync(1, 1))
            .ReturnsAsync(0);

        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Product { Id = 1, Stock = 0 });

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateAtomicSut().PurchaseAsync(Dto()));

        Assert.Equal("Insufficient stock.", ex.Message);

        _transaction.Verify(x => x.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Atomic_WhenNoRowAffectedAndProductMissing_ShouldThrowNotFound()
    {
        _productRepository
            .Setup(x => x.TryDeductStockAsync(1, 1))
            .ReturnsAsync(0);

        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync((Product?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateAtomicSut().PurchaseAsync(Dto()));
    }

    // ------------------------------------------------------------------
    // Baseline — 對照組
    // ------------------------------------------------------------------

    [Fact]
    public async Task Baseline_ShouldBypassVersionCheck()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Product { Id = 1, Stock = 5 });

        var sut = new BaselineFlashSalePurchaseStrategy(
            _productRepository.Object,
            _orderRepository.Object);

        await sut.PurchaseAsync(Dto());

        // Baseline 的意義就在於「直接覆寫、不檢查版本」，
        // 否則加了 rowversion 之後它就不再會 Lost Update，對照組失效。
        _productRepository.Verify(
            x => x.OverwriteStockWithoutVersionCheckAsync(1, 4),
            Times.Once);

        _productRepository.Verify(
            x => x.TryUpdateWithVersionAsync(It.IsAny<Product>()),
            Times.Never);
    }

    [Fact]
    public async Task Baseline_WhenStockIsInsufficient_ShouldThrowBusinessException()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Product { Id = 1, Stock = 1 });

        var sut = new BaselineFlashSalePurchaseStrategy(
            _productRepository.Object,
            _orderRepository.Object);

        await Assert.ThrowsAsync<BusinessException>(
            () => sut.PurchaseAsync(Dto(quantity: 2)));

        _orderRepository.Verify(
            x => x.CreateAsync(It.IsAny<Order>()),
            Times.Never);
    }

    [Fact]
    public async Task Baseline_WhenProductNotFound_ShouldThrowNotFoundException()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((Product?)null);

        var sut = new BaselineFlashSalePurchaseStrategy(
            _productRepository.Object,
            _orderRepository.Object);

        await Assert.ThrowsAsync<NotFoundException>(
            () => sut.PurchaseAsync(Dto()));
    }

    /// <summary>
    /// 把 Baseline 的缺陷釘住。
    ///
    /// 這個測試不是在驗證正確行為 —— 兩個各自讀到 Stock = 1 的請求
    /// 都會通過檢查並各自建單，最終賣出 2 件。
    /// Stage 2 已在真實 SQL Server 上以 k6 重現同一件事
    /// （docs/load-test/race-condition.md）。
    ///
    /// 它通過，代表對照組仍然如預期地會超賣，比較才有意義。
    /// </summary>
    [Fact]
    public async Task Baseline_WhenTwoRequestsReadSameStock_ShouldOversell()
    {
        _productRepository
            .SetupSequence(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Product { Id = 1, Stock = 1 })
            .ReturnsAsync(new Product { Id = 1, Stock = 1 });

        var sut = new BaselineFlashSalePurchaseStrategy(
            _productRepository.Object,
            _orderRepository.Object);

        await sut.PurchaseAsync(Dto());
        await sut.PurchaseAsync(Dto());

        // 庫存只有 1，卻建立了 2 筆訂單
        _orderRepository.Verify(
            x => x.CreateAsync(It.IsAny<Order>()),
            Times.Exactly(2));
    }
}
