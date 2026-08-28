using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Services;
using FlashSale.Api.Services.Interfaces;
using Moq;

namespace FlashSale.UnitTests.Services;

/// <summary>
/// FlashSaleService 在 Stage 3 之後只負責挑選並委派策略，
/// 實際的搶購邏輯測試在 <see cref="FlashSaleStrategyTests"/>。
/// </summary>
public class FlashSaleServiceTests
{
    private static Mock<IFlashSalePurchaseStrategy> CreateStrategy(
        FlashSaleStrategy strategy,
        Order? result = null)
    {
        var mock = new Mock<IFlashSalePurchaseStrategy>();

        mock.SetupGet(x => x.Strategy).Returns(strategy);

        mock.Setup(x => x.PurchaseAsync(It.IsAny<CreateFlashSaleDtoModel>()))
            .ReturnsAsync(result ?? new Order
            {
                Id = 1,
                UserId = 99,
                ProductId = 1,
                Quantity = 1,
                Status = OrderStatus.Completed,
                CreatedAt = DateTime.UtcNow
            });

        return mock;
    }

    [Fact]
    public async Task PurchaseAsync_ShouldDispatchToRequestedStrategy()
    {
        var atomic = CreateStrategy(FlashSaleStrategy.Atomic);
        var transaction = CreateStrategy(FlashSaleStrategy.Transaction);

        var sut = new FlashSaleService(
            new[] { atomic.Object, transaction.Object },
            TestMapperFactory.Create());

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

        var sut = new FlashSaleService(
            new[] { atomic.Object },
            TestMapperFactory.Create());

        var result = await sut.PurchaseAsync(new CreateFlashSaleDtoModel
        {
            ProductId = 3,
            UserId = 7,
            Quantity = 2,
            Strategy = FlashSaleStrategy.Atomic
        });

        Assert.Equal(42, result.Id);
        Assert.Equal(7, result.UserId);
        Assert.Equal(2, result.Quantity);
        Assert.Equal(OrderStatus.Completed, result.Status);
    }

    [Fact]
    public async Task PurchaseAsync_WhenStrategyIsNotRegistered_ShouldThrowBusinessException()
    {
        var atomic = CreateStrategy(FlashSaleStrategy.Atomic);

        var sut = new FlashSaleService(
            new[] { atomic.Object },
            TestMapperFactory.Create());

        await Assert.ThrowsAsync<BusinessException>(
            () => sut.PurchaseAsync(new CreateFlashSaleDtoModel
            {
                ProductId = 1,
                UserId = 1,
                Quantity = 1,
                Strategy = FlashSaleStrategy.Optimistic
            }));
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
