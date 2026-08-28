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

        await _productRepository.UpdateAsync(entity);

        return _mapper.Map<ProductDtoModel>(entity);
    }
}
