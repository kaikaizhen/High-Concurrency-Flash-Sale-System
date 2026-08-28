using FlashSale.Api.Common.Enums;
using FlashSale.Api.Data;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.UnitTests.Repositories;

/// <summary>
/// 以 EF Core In-Memory Provider 驗證 Repository 的查詢行為。
///
/// 注意：In-Memory Provider 不模擬 SQL Server 的 Transaction、Lock
/// 與 Isolation Level，所以 Stage 2 之後的併發行為必須在真實
/// SQL Server 上用 k6 驗證，不能靠這裡的測試。
/// </summary>
public class OrderRepositoryTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    [Fact]
    public async Task CreateAsync_ShouldPersistOrder()
    {
        await using var dbContext = CreateDbContext();

        var sut = new OrderRepository(dbContext);

        await sut.CreateAsync(new Order
        {
            UserId = 1,
            ProductId = 10,
            Quantity = 2,
            Status = OrderStatus.Completed,
            CreatedAt = DateTime.UtcNow
        });

        var saved = await dbContext.Orders.SingleAsync();

        Assert.True(saved.Id > 0);
        Assert.Equal(10, saved.ProductId);
        Assert.Equal(2, saved.Quantity);
    }

    [Fact]
    public async Task CountByProductIdAsync_ShouldOnlyCountMatchingProduct()
    {
        await using var dbContext = CreateDbContext();

        dbContext.Orders.AddRange(
            new Order { UserId = 1, ProductId = 10, Quantity = 1, CreatedAt = DateTime.UtcNow },
            new Order { UserId = 2, ProductId = 10, Quantity = 1, CreatedAt = DateTime.UtcNow },
            new Order { UserId = 3, ProductId = 20, Quantity = 1, CreatedAt = DateTime.UtcNow });

        await dbContext.SaveChangesAsync();

        var sut = new OrderRepository(dbContext);

        Assert.Equal(2, await sut.CountByProductIdAsync(10));
        Assert.Equal(1, await sut.CountByProductIdAsync(20));
        Assert.Equal(0, await sut.CountByProductIdAsync(30));
    }

    [Fact]
    public async Task GetListByProductIdAsync_ShouldReturnOrdersOrderedById()
    {
        await using var dbContext = CreateDbContext();

        dbContext.Orders.AddRange(
            new Order { UserId = 1, ProductId = 10, Quantity = 1, CreatedAt = DateTime.UtcNow },
            new Order { UserId = 2, ProductId = 10, Quantity = 1, CreatedAt = DateTime.UtcNow });

        await dbContext.SaveChangesAsync();

        var sut = new OrderRepository(dbContext);

        var result = await sut.GetListByProductIdAsync(10);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].Id < result[1].Id);
    }
}
