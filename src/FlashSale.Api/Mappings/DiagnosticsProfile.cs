using AutoMapper;
using FlashSale.Api.Models.Dtos.Diagnostics;
using FlashSale.Api.Models.ViewModels.Diagnostics;

namespace FlashSale.Api.Mappings;

public class DiagnosticsProfile : Profile
{
    public DiagnosticsProfile()
    {
        CreateMap<MetricsDtoModel, MetricsViewModel>();
    }
}
