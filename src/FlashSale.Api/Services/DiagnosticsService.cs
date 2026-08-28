using FlashSale.Api.Infrastructure.Diagnostics;
using FlashSale.Api.Infrastructure.Messaging;
using FlashSale.Api.Models.Dtos.Diagnostics;
using FlashSale.Api.Services.Interfaces;

namespace FlashSale.Api.Services;

public class DiagnosticsService : IDiagnosticsService
{
    private readonly IMetricsCollector _metrics;
    private readonly IQueueInspector _queueInspector;

    public DiagnosticsService(
        IMetricsCollector metrics,
        IQueueInspector queueInspector)
    {
        _metrics = metrics;
        _queueInspector = queueInspector;
    }

    public MetricsDtoModel GetMetrics()
    {
        return _metrics.GetSnapshot();
    }

    public void ResetMetrics()
    {
        _metrics.Reset();
    }

    public Task<QueueMetricsDtoModel> GetQueueMetricsAsync()
    {
        return _queueInspector.GetQueueMetricsAsync();
    }
}
