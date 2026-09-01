using FlashSale.Api.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Metrics;

namespace FlashSale.UnitTests;

/// <summary>
/// 測試用的 FlashSaleMetrics。
///
/// 用真的 IMeterFactory 而不是 Mock：Counter&lt;T&gt; 與 Histogram&lt;T&gt;
/// 是 sealed 的，Mock 不了。真實的 Meter 沒有訂閱者時不做任何事，
/// 成本可以忽略。
/// </summary>
public static class TestMetricsFactory
{
    public static FlashSaleMetrics CreateFlashSaleMetrics()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        return new FlashSaleMetrics(
            services.BuildServiceProvider().GetRequiredService<IMeterFactory>());
    }
}
