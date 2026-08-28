using FlashSale.Api.Models.Dtos.Products;

namespace FlashSale.Api.Services.Interfaces;

public interface IProductService
{
    Task<List<ProductDtoModel>> GetListAsync();

    Task<ProductDtoModel> GetByIdAsync(int id);

    Task<ProductDtoModel> CreateAsync(CreateProductDtoModel dto);

    Task<ProductDtoModel> UpdateAsync(UpdateProductDtoModel dto);
}
