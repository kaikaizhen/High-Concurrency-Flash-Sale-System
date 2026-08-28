using FlashSale.Api.Infrastructure.Diagnostics;
using FlashSale.Api.Models.Dtos.Diagnostics;
using FlashSale.Api.Services.Interfaces;

namespace FlashSale.Api.Services;

public class DiagnosticsService : IDiagnosticsService
{
    private readonly IMetricsCollector _metrics;

    public DiagnosticsService(IMetricsCollector metrics)
    {
        _metrics = metrics;
    }

    public MetricsDtoModel GetMetrics()
    {
        return _metrics.GetSnapshot();
    }

    public void ResetMetrics()
    {
        _metrics.Reset();
    }
}
