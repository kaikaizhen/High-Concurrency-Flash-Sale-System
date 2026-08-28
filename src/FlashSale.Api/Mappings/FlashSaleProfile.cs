using AutoMapper;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Params.FlashSales;

namespace FlashSale.Api.Mappings;

public class FlashSaleProfile : Profile
{
    public FlashSaleProfile()
    {
        // ProductId 來自 Route，由 Controller 補上。
        CreateMap<CreateFlashSaleParamModel, CreateFlashSaleDtoModel>()
            .ForMember(
                dest => dest.ProductId,
                opt => opt.Ignore());
    }
}
