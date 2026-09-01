using AutoMapper;
using FlashSale.Api.Models.Dtos.Orders;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Models.Params.Orders;
using FlashSale.Api.Models.ViewModels.Orders;

namespace FlashSale.Api.Mappings;

public class OrderProfile : Profile
{
    public OrderProfile()
    {
        CreateMap<CreateOrderParamModel, CreateOrderDtoModel>();

        // Id 由資料庫產生、Status 與 CreatedAt 由 Service 設定。
        CreateMap<CreateOrderDtoModel, Order>()
            .ForMember(
                dest => dest.Id,
                opt => opt.Ignore())
            .ForMember(
                dest => dest.Status,
                opt => opt.Ignore())
            .ForMember(
                dest => dest.CreatedAt,
                opt => opt.Ignore())
            .ForMember(
                dest => dest.IdempotencyKey,
                opt => opt.Ignore());

        CreateMap<Order, OrderDtoModel>();

        CreateMap<OrderDtoModel, OrderViewModel>()
            .ForMember(
                dest => dest.Status,
                opt => opt.MapFrom(src => src.Status.ToString()));
    }
}
