using FlashSale.Api.Models.Dtos.Diagnostics;

namespace FlashSale.Api.Services.Interfaces;

public interface IDiagnosticsService
{
    MetricsDtoModel GetMetrics();

    void ResetMetrics();
}
