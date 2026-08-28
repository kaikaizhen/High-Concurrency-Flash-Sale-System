using FlashSale.Api.Common.Constants;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Infrastructure.Cache;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Options;
using FlashSale.Api.Services;
using FlashSale.Api.Services.Interfaces;
using Moq;

namespace FlashSale.UnitTests.Services;

/// <summary>
/// FlashSaleService 在 Stage 3 之後負責挑選並委派策略，
/// Stage 4 起再加上成交後的快取失效。
/// 實際的搶購邏輯測試在 <see cref="FlashSaleStrategyTests"/>。
/// </summary>
public class FlashSaleServiceTests
{
    private readonly Mock<ICacheService> _cache = new();

    private readonly CacheOptions _cacheOptions = new() { Enabled = true };

    public FlashSaleServiceTests()
    {
        _cache
            .Setup(x => x.RemoveAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    private FlashSaleService CreateSut(
        params IFlashSalePurchaseStrategy[] strategies)
    {
        return new FlashSaleService(
            strategies,
            _cache.Object,
            Microsoft.Extensions.Options.Options.Create(_cacheOptions),
            TestMapperFactory.Create());
    }

    private static Mock<IFlashSalePurchaseStrategy> CreateStrategy(
        FlashSaleStrategy strategy,
        Order? result = null)
    {
        var mock = new Mock<IFlashSalePurchaseStrategy>();

        mock.SetupGet(x => x.Strategy).Returns(strategy);

        mock.Setup(x => x.PurchaseAsync(It.IsAny<CreateFlashSaleDtoModel>()))
            .ReturnsAsync(FlashSalePurchaseResult.Completed(result ?? new Order
            {
                Id = 1,
                UserId = 99,
                ProductId = 1,
                Quantity = 1,
                Status = OrderStatus.Completed,
                CreatedAt = DateTime.UtcNow
            }));

        return mock;
    }

    /// <summary>Stage 5：回傳「已排入佇列、訂單尚未建立」的策略。</summary>
    private static Mock<IFlashSalePurchaseStrategy> CreateQueuedStrategy(
        FlashSaleStrategy strategy,
        Guid requestId)
    {
        var mock = new Mock<IFlashSalePurchaseStrategy>();

        mock.SetupGet(x => x.Strategy).Returns(strategy);

        mock.Setup(x => x.PurchaseAsync(It.IsAny<CreateFlashSaleDtoModel>()))
            .ReturnsAsync(FlashSalePurchaseResult.Queued(requestId));

        return mock;
    }

    [Fact]
    public async Task PurchaseAsync_ShouldDispatchToRequestedStrategy()
    {
        var atomic = CreateStrategy(FlashSaleStrategy.Atomic);
        var transaction = CreateStrategy(FlashSaleStrategy.Transaction);

        var sut = CreateSut(atomic.Object, transaction.Object);

        await sut.PurchaseAsync(new CreateFlashSaleDtoModel
        {
            ProductId = 1,
            UserId = 99,
            Quantity = 1,
            Strategy = FlashSaleStrategy.Transaction
        });

        transaction.Verify(
            x => x.PurchaseAsync(It.IsAny<CreateFlashSaleDtoModel>()),
            Times.Once);

        atomic.Verify(
            x => x.PurchaseAsync(It.IsAny<CreateFlashSaleDtoModel>()),
            Times.Never);
    }

    [Fact]
    public async Task PurchaseAsync_ShouldMapOrderEntityToDto()
    {
        var order = new Order
        {
            Id = 42,
            UserId = 7,
            ProductId = 3,
            Quantity = 2,
            Status = OrderStatus.Completed,
            CreatedAt = DateTime.UtcNow
        };

        var atomic = CreateStrategy(FlashSaleStrategy.Atomic, order);

        var sut = CreateSut(atomic.Object);

        var result = await sut.PurchaseAsync(new CreateFlashSaleDtoModel
        {
            ProductId = 3,
            UserId = 7,
            Quantity = 2,
            Strategy = FlashSaleStrategy.Atomic
        });

        Assert.False(result.IsQueued);
        Assert.NotNull(result.Order);
        Assert.Equal(42, result.Order!.Id);
        Assert.Equal(7, result.Order.UserId);
        Assert.Equal(2, result.Order.Quantity);
        Assert.Equal(OrderStatus.Completed, result.Order.Status);
    }

    // ------------------------------------------------------------------
    // Stage 5 — 非同步路徑
    // ------------------------------------------------------------------

    [Fact]
    public async Task PurchaseAsync_WhenStrategyQueues_ShouldReturnQueuedResultWithoutOrder()
    {
        var requestId = Guid.NewGuid();

        var queued = CreateQueuedStrategy(
            FlashSaleStrategy.AtomicQueued,
            requestId);

        var sut = CreateSut(queued.Object);

        var result = await sut.PurchaseAsync(new CreateFlashSaleDtoModel
        {
            ProductId = 3,
            UserId = 7,
            Quantity = 1,
            Strategy = FlashSaleStrategy.AtomicQueued
        });

        Assert.True(result.IsQueued);
        Assert.Equal(requestId, result.RequestId);

        // 訂單此刻還不存在。回一個 Id = 0 的假訂單會讓呼叫端
        // 無法分辨「還沒建立」與「建立失敗」。
        Assert.Null(result.Order);
    }

    [Fact]
    public async Task PurchaseAsync_WhenStrategyQueues_ShouldStillInvalidateProductCache()
    {
        var queued = CreateQueuedStrategy(
            FlashSaleStrategy.AtomicQueued,
            Guid.NewGuid());

        var sut = CreateSut(queued.Object);

        await sut.PurchaseAsync(new CreateFlashSaleDtoModel
        {
            ProductId = 7,
            UserId = 1,
            Quantity = 1,
            Strategy = FlashSaleStrategy.AtomicQueued
        });

        // 庫存在 API 這一端就已經扣掉了，尚未建立的只有訂單，
        // 所以商品快取一樣必須失效。
        _cache.Verify(
            x => x.RemoveAsync(CacheKeys.Product(7)),
            Times.Once);
    }

    [Fact]
    public async Task PurchaseAsync_WhenStrategyIsNotRegistered_ShouldThrowBusinessException()
    {
        var atomic = CreateStrategy(FlashSaleStrategy.Atomic);

        var sut = CreateSut(atomic.Object);

        await Assert.ThrowsAsync<BusinessException>(
            () => sut.PurchaseAsync(new CreateFlashSaleDtoModel
            {
                ProductId = 1,
                UserId = 1,
                Quantity = 1,
                Strategy = FlashSaleStrategy.Optimistic
            }));
    }

    // ------------------------------------------------------------------
    // Stage 4 — 成交後的快取失效
    // ------------------------------------------------------------------

    [Fact]
    public async Task PurchaseAsync_WhenPurchaseSucceeds_ShouldInvalidateProductCache()
    {
        var atomic = CreateStrategy(FlashSaleStrategy.Atomic);

        var sut = CreateSut(atomic.Object);

        await sut.PurchaseAsync(new CreateFlashSaleDtoModel
        {
            ProductId = 7,
            UserId = 1,
            Quantity = 1,
            Strategy = FlashSaleStrategy.Atomic
        });

        // 搶購改動了庫存，商品快取必須失效，
        // 否則商品頁在整場秒殺期間都顯示錯的庫存
        _cache.Verify(
            x => x.RemoveAsync(CacheKeys.Product(7)),
            Times.Once);
    }

    [Fact]
    public async Task PurchaseAsync_WhenPurchaseFails_ShouldNotInvalidateProductCache()
    {
        var atomic = new Mock<IFlashSalePurchaseStrategy>();

        atomic.SetupGet(x => x.Strategy).Returns(FlashSaleStrategy.Atomic);

        atomic
            .Setup(x => x.PurchaseAsync(It.IsAny<CreateFlashSaleDtoModel>()))
            .ThrowsAsync(new BusinessException("Insufficient stock."));

        var sut = CreateSut(atomic.Object);

        await Assert.ThrowsAsync<BusinessException>(
            () => sut.PurchaseAsync(new CreateFlashSaleDtoModel
            {
                ProductId = 7,
                UserId = 1,
                Quantity = 1,
                Strategy = FlashSaleStrategy.Atomic
            }));

        // 沒有成交就沒有庫存變動，清快取只會製造沒必要的 Miss。
        // 秒殺賣完後 98% 的請求都走這條路徑，清了等於自廢快取。
        _cache.Verify(
            x => x.RemoveAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public void DefaultStrategy_ShouldBeAtomic()
    {
        // Stage 3 的結論：未指定策略時走 Atomic Update。
        // 這個測試把選定的主要方案釘住，避免日後被無意改掉。
        Assert.Equal(
            FlashSaleStrategy.Atomic,
            new CreateFlashSaleDtoModel().Strategy);
    }
}
