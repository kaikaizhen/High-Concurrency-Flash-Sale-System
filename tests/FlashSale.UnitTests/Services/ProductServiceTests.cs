using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.Products;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services;
using Moq;

namespace FlashSale.UnitTests.Services;

public class ProductServiceTests
{
    private readonly Mock<IProductRepository> _productRepository = new();

    private ProductService CreateSut()
    {
        return new ProductService(
            _productRepository.Object,
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
