using AutoMapper;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.Products;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.Interfaces;

namespace FlashSale.Api.Services;

public class ProductService : IProductService
{
    private readonly IProductRepository _productRepository;
    private readonly IMapper _mapper;

    public ProductService(
        IProductRepository productRepository,
        IMapper mapper)
    {
        _productRepository = productRepository;
        _mapper = mapper;
    }

    public async Task<List<ProductDtoModel>> GetListAsync()
    {
        var entities = await _productRepository.GetListAsync();

        return _mapper.Map<List<ProductDtoModel>>(entities);
    }

    public async Task<ProductDtoModel> GetByIdAsync(int id)
    {
        var entity = await _productRepository.GetByIdAsync(id);

        if (entity is null)
        {
            throw new NotFoundException("Product not found.");
        }

        return _mapper.Map<ProductDtoModel>(entity);
    }

    public async Task<ProductDtoModel> CreateAsync(
        CreateProductDtoModel dto)
    {
        var exists = await _productRepository
            .ExistsByNameAsync(dto.Name);

        if (exists)
        {
            throw new BusinessException("Product name already exists.");
        }

        var entity = _mapper.Map<Product>(dto);

        entity.CreatedAt = DateTime.UtcNow;

        await _productRepository.CreateAsync(entity);

        return _mapper.Map<ProductDtoModel>(entity);
    }

    public async Task<ProductDtoModel> UpdateAsync(
        UpdateProductDtoModel dto)
    {
        var entity = await _productRepository.GetByIdAsync(dto.Id);

        if (entity is null)
        {
            throw new NotFoundException("Product not found.");
        }

        entity.Name = dto.Name;
        entity.Price = dto.Price;
        entity.Stock = dto.Stock;

        // 加入 rowversion 之後，這裡的更新也會帶版本檢查。
        // 若在讀取與寫入之間有人改過這筆商品（例如同時進行的搶購），
        // 這次更新會失敗 —— 這是正確行為，不該讓它變成 500。
        var updated = await _productRepository
            .TryUpdateWithVersionAsync(entity);

        if (!updated)
        {
            throw new BusinessException(
                "Product was modified by another request, please retry.");
        }

        return _mapper.Map<ProductDtoModel>(entity);
    }
}
