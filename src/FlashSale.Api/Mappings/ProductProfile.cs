using AutoMapper;
using FlashSale.Api.Models.Dtos.Products;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Models.Params.Products;
using FlashSale.Api.Models.ViewModels.Products;

namespace FlashSale.Api.Mappings;

public class ProductProfile : Profile
{
    public ProductProfile()
    {
        CreateMap<CreateProductParamModel, CreateProductDtoModel>();

        // Id 來自 Route，由 Controller 補上。
        CreateMap<UpdateProductParamModel, UpdateProductDtoModel>()
            .ForMember(
                dest => dest.Id,
                opt => opt.Ignore());

        // Id 由資料庫產生、CreatedAt 由 Service 設定。
        CreateMap<CreateProductDtoModel, Product>()
            .ForMember(
                dest => dest.Id,
                opt => opt.Ignore())
            .ForMember(
                dest => dest.CreatedAt,
                opt => opt.Ignore());

        CreateMap<Product, ProductDtoModel>();

        CreateMap<ProductDtoModel, ProductViewModel>();
    }
}
