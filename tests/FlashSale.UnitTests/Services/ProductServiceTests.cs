using FlashSale.Api.Common.Constants;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Infrastructure.Cache;
using FlashSale.Api.Models.Dtos.Products;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Options;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services;
using Microsoft.Extensions.Options;
using Moq;

namespace FlashSale.UnitTests.Services;

public class ProductServiceTests
{
    private readonly Mock<IProductRepository> _productRepository = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<IKeyedLock> _keyedLock = new();

    private readonly CacheOptions _cacheOptions = new()
    {
        Enabled = true,
        TtlSeconds = 10,
        NullTtlSeconds = 3,
        EnableNullCaching = true,
        EnableSingleFlight = true
    };

    public ProductServiceTests()
    {
        // 預設：快取全部 Miss，行為等同沒有快取
        _cache
            .Setup(x => x.GetAsync<ProductDtoModel>(It.IsAny<string>()))
            .ReturnsAsync(CacheResult<ProductDtoModel>.Miss());

        _cache
            .Setup(x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<ProductDtoModel?>(),
                It.IsAny<TimeSpan>()))
            .Returns(Task.CompletedTask);

        _cache
            .Setup(x => x.RemoveAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        _keyedLock
            .Setup(x => x.AcquireAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
    }

    private ProductService CreateSut()
    {
        return new ProductService(
            _productRepository.Object,
            _cache.Object,
            _keyedLock.Object,
            Microsoft.Extensions.Options.Options.Create(_cacheOptions),
            TestMapperFactory.Create());
    }

    [Fact]
    public async Task CreateAsync_WhenNameIsUnique_ShouldCreateProduct()
    {
        _productRepository
            .Setup(x => x.ExistsByNameAsync("iPhone"))
            .ReturnsAsync(false);

        Product? created = null;

        _productRepository
            .Setup(x => x.CreateAsync(It.IsAny<Product>()))
            .Callback<Product>(x =>
            {
                x.Id = 1;
                created = x;
            })
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        var result = await sut.CreateAsync(new CreateProductDtoModel
        {
            Name = "iPhone",
            Price = 30000m,
            Stock = 100
        });

        Assert.NotNull(created);
        Assert.Equal("iPhone", created!.Name);
        Assert.Equal(100, created.Stock);
        Assert.NotEqual(default, created.CreatedAt);

        Assert.Equal(1, result.Id);
        Assert.Equal("iPhone", result.Name);
    }

    [Fact]
    public async Task CreateAsync_WhenNameAlreadyExists_ShouldThrowBusinessException()
    {
        _productRepository
            .Setup(x => x.ExistsByNameAsync("iPhone"))
            .ReturnsAsync(true);

        var sut = CreateSut();

        await Assert.ThrowsAsync<BusinessException>(
            () => sut.CreateAsync(new CreateProductDtoModel
            {
                Name = "iPhone",
                Price = 30000m,
                Stock = 100
            }));

        _productRepository.Verify(
            x => x.CreateAsync(It.IsAny<Product>()),
            Times.Never);
    }

    [Fact]
    public async Task GetByIdAsync_WhenProductNotFound_ShouldThrowNotFoundException()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((Product?)null);

        var sut = CreateSut();

        await Assert.ThrowsAsync<NotFoundException>(
            () => sut.GetByIdAsync(404));
    }

    [Fact]
    public async Task UpdateAsync_WhenProductExists_ShouldOverwriteFields()
    {
        var product = new Product
        {
            Id = 1,
            Name = "Old",
            Price = 100m,
            Stock = 5,
            CreatedAt = DateTime.UtcNow
        };

        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(product);

        _productRepository
            .Setup(x => x.TryUpdateWithVersionAsync(product))
            .ReturnsAsync(true);

        var sut = CreateSut();

        var result = await sut.UpdateAsync(new UpdateProductDtoModel
        {
            Id = 1,
            Name = "New",
            Price = 200m,
            Stock = 50
        });

        Assert.Equal("New", product.Name);
        Assert.Equal(200m, product.Price);
        Assert.Equal(50, product.Stock);

        _productRepository.Verify(
            x => x.TryUpdateWithVersionAsync(product),
            Times.Once);

        Assert.Equal("New", result.Name);
    }

    // ------------------------------------------------------------------
    // Stage 4 — Cache Aside
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetByIdAsync_WhenCacheHit_ShouldNotTouchDatabase()
    {
        _cache
            .Setup(x => x.GetAsync<ProductDtoModel>(CacheKeys.Product(1)))
            .ReturnsAsync(CacheResult<ProductDtoModel>.Hit(
                new ProductDtoModel { Id = 1, Name = "Cached", Stock = 5 }));

        var result = await CreateSut().GetByIdAsync(1);

        Assert.Equal("Cached", result.Name);

        _productRepository.Verify(
            x => x.GetByIdAsync(It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task GetByIdAsync_WhenCacheMiss_ShouldLoadFromDatabaseAndPopulateCache()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Product { Id = 1, Name = "FromDb", Stock = 5 });

        var result = await CreateSut().GetByIdAsync(1);

        Assert.Equal("FromDb", result.Name);

        _cache.Verify(
            x => x.SetAsync(
                CacheKeys.Product(1),
                It.Is<ProductDtoModel?>(d => d != null && d.Name == "FromDb"),
                TimeSpan.FromSeconds(10)),
            Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_WhenSingleFlightEnabled_ShouldRecheckCacheAfterAcquiringLock()
    {
        // 取得鎖之後必須再讀一次快取，否則排隊的請求還是會一個個查資料庫，
        // 只是從併發變成串行，查詢次數不會減少。
        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Product { Id = 1, Name = "FromDb" });

        await CreateSut().GetByIdAsync(1);

        _keyedLock.Verify(
            x => x.AcquireAsync(CacheKeys.Product(1)),
            Times.Once);

        // 一次在取鎖前、一次在取鎖後
        _cache.Verify(
            x => x.GetAsync<ProductDtoModel>(CacheKeys.Product(1)),
            Times.Exactly(2));
    }

    [Fact]
    public async Task GetByIdAsync_WhenProductMissing_ShouldCacheTheAbsence()
    {
        _productRepository
            .Setup(x => x.GetByIdAsync(404))
            .ReturnsAsync((Product?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateSut().GetByIdAsync(404));

        // Cache Penetration 防護：把「查無資料」也快取起來，但 TTL 要短
        _cache.Verify(
            x => x.SetAsync(
                CacheKeys.Product(404),
                (ProductDtoModel?)null,
                TimeSpan.FromSeconds(3)),
            Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_WhenNegativeCacheHit_ShouldThrowNotFoundWithoutDatabase()
    {
        _cache
            .Setup(x => x.GetAsync<ProductDtoModel>(CacheKeys.Product(404)))
            .ReturnsAsync(CacheResult<ProductDtoModel>.Hit(null));

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateSut().GetByIdAsync(404));

        _productRepository.Verify(
            x => x.GetByIdAsync(It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task GetByIdAsync_WhenCacheDisabled_ShouldAlwaysQueryDatabase()
    {
        _cacheOptions.Enabled = false;

        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(new Product { Id = 1, Name = "FromDb" });

        await CreateSut().GetByIdAsync(1);

        _productRepository.Verify(x => x.GetByIdAsync(1), Times.Once);

        _cache.Verify(
            x => x.GetAsync<ProductDtoModel>(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_ShouldInvalidateCacheAfterWritingDatabase()
    {
        var product = new Product { Id = 1, Name = "Old", Stock = 5 };

        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(product);

        _productRepository
            .Setup(x => x.TryUpdateWithVersionAsync(product))
            .ReturnsAsync(true);

        await CreateSut().UpdateAsync(new UpdateProductDtoModel
        {
            Id = 1,
            Name = "New",
            Price = 200m,
            Stock = 50
        });

        _cache.Verify(
            x => x.RemoveAsync(CacheKeys.Product(1)),
            Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_WhenWriteFails_ShouldNotInvalidateCache()
    {
        var product = new Product { Id = 1, Name = "Old", Stock = 5 };

        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(product);

        _productRepository
            .Setup(x => x.TryUpdateWithVersionAsync(product))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<BusinessException>(
            () => CreateSut().UpdateAsync(new UpdateProductDtoModel
            {
                Id = 1,
                Name = "New",
                Price = 200m,
                Stock = 50
            }));

        // 寫入沒成功就不該清快取 —— 快取裡的還是正確的舊值
        _cache.Verify(
            x => x.RemoveAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_WhenVersionConflicts_ShouldThrowBusinessException()
    {
        var product = new Product { Id = 1, Name = "Old", Stock = 5 };

        _productRepository
            .Setup(x => x.GetByIdAsync(1))
            .ReturnsAsync(product);

        _productRepository
            .Setup(x => x.TryUpdateWithVersionAsync(product))
            .ReturnsAsync(false);

        var sut = CreateSut();

        // 版本衝突是可預期的商業狀況，應回 409 而不是 500
        await Assert.ThrowsAsync<BusinessException>(
            () => sut.UpdateAsync(new UpdateProductDtoModel
            {
                Id = 1,
                Name = "New",
                Price = 200m,
                Stock = 50
            }));
    }
}
