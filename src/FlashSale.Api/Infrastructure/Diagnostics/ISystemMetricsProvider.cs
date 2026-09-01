using FlashSale.Api.Models.Dtos.Diagnostics;

namespace FlashSale.Api.Infrastructure.Diagnostics;

public interface ISystemMetricsProvider
{
    Task<SystemMetricsDtoModel> GetAsync(CancellationToken cancellationToken = default);
}
