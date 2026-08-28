using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services;
using Moq;

namespace FlashSale.UnitTests.Services;

public class FlashSaleServiceTests
{
    private readonly Mock<IProductRepository> _productRepository = new();
    private readonly Mock<IOrderRepository> _orderRepository = new();

    private FlashSaleService CreateSut()
    {
        return new FlashSaleService(
            _productRepository.Object,
            _orderRepository.Object,
            TestMapperFactory.Create());
    }

    [Fact]
    public async Task PurchaseAsync_WhenStockIsEnough_ShouldDeductStockAndCreateOrder()
    {
        var product = new Product
        {
            Id = 1,
            Name = "iPhone",
            Price = 30000m,
            Stock = 10,
            CreatedAt = DateTime.UtcNow
        };

        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(product);

        Order? created = null;

        _orderRepository
            .Setup(x => x.CreateAsync(It.IsAny<Order>()))
            .Callback<Order>(x => created = x)
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        var result = await sut.PurchaseAsync(new CreateFlashSaleDtoModel
        {
            ProductId = 1,
            UserId = 99,
            Quantity = 3
        });

        Assert.Equal(7, product.Stock);

        _productRepository.Verify(
            x => x.UpdateAsync(product),
            Times.Once);

        Assert.NotNull(created);
        Assert.Equal(1, created!.ProductId);
        Assert.Equal(99, created.UserId);
        Assert.Equal(3, created.Quantity);
        Assert.Equal(OrderStatus.Completed, created.Status);

        Assert.Equal(OrderStatus.Completed, result.Status);
        Assert.Equal(3, result.Quantity);
    }

    [Fact]
    public async Task PurchaseAsync_WhenStockIsInsufficient_ShouldThrowBusinessException()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Product { Id = 1, Stock = 1 });

        var sut = CreateSut();

        await Assert.ThrowsAsync<BusinessException>(
            () => sut.PurchaseAsync(new CreateFlashSaleDtoModel
            {
                ProductId = 1,
                UserId = 99,
                Quantity = 2
            }));

        _productRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Product>()),
            Times.Never);

        _orderRepository.Verify(
            x => x.CreateAsync(It.IsAny<Order>()),
            Times.Never);
    }

    [Fact]
    public async Task PurchaseAsync_WhenProductNotFound_ShouldThrowNotFoundException()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((Product?)null);

        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(
            () => sut.PurchaseAsync(new CreateFlashSaleDtoModel
            {
                ProductId = 404,
                UserId = 99,
                Quantity = 1
            }));
    }

    /// <summary>
    /// Stage 1 的 Baseline 特性：
    /// 「讀取庫存」與「寫回庫存」之間沒有任何保護。
    ///
    /// 這個測試不是在驗證正確行為，而是把 Baseline 的缺陷釘住：
    /// 兩個各自讀到 Stock = 1 的請求，都會通過檢查並各自建立訂單，
    /// 最終賣出 2 件。Stage 2 會用 k6 在真實 DB 上重現同一件事。
    /// </summary>
    [Fact]
    public async Task PurchaseAsync_WhenTwoRequestsReadSameStock_ShouldOversell()
    {
        var snapshotA = new Product { Id = 1, Stock = 1 };
        var snapshotB = new Product { Id = 1, Stock = 1 };

        _productRepository
            .SetupSequence(x => x.GetByIdAsync(1))
            .ReturnsAsync(snapshotA)
            .ReturnsAsync(snapshotB);

        _orderRepository
            .Setup(x => x.CreateAsync(It.IsAny<Order>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        var dto = new CreateFlashSaleDtoModel
        {
            ProductId = 1,
            UserId = 99,
            Quantity = 1
        };

        await sut.PurchaseAsync(dto);
        await sut.PurchaseAsync(dto);

        // 庫存只有 1，卻建立了 2 筆訂單。
        _orderRepository.Verify(
            x => x.CreateAsync(It.IsAny<Order>()),
            Times.Exactly(2));
    }
}
