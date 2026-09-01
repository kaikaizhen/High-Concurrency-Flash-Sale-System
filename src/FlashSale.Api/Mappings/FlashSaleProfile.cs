using AutoMapper;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Params.FlashSales;
using FlashSale.Api.Models.ViewModels.FlashSales;

namespace FlashSale.Api.Mappings;

public class FlashSaleProfile : Profile
{
    public FlashSaleProfile()
    {
        // ProductId 來自 Route、IdempotencyKey 來自 Header，
        // 兩者都由 Controller 補上。Strategy 直接沿用 ParamModel 的值。
        CreateMap<CreateFlashSaleParamModel, CreateFlashSaleDtoModel>()
            .ForMember(
                dest => dest.ProductId,
                opt => opt.Ignore())
            .ForMember(
                dest => dest.IdempotencyKey,
                opt => opt.Ignore());

        CreateMap<FlashSalePurchaseDtoModel, FlashSaleResultViewModel>()
            .ForMember(
                dest => dest.Status,
                opt => opt.MapFrom(src => src.IsQueued ? "Queued" : "Completed"));
    }
}
