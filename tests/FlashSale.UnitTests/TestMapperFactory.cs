using AutoMapper;
using FlashSale.Api.Mappings;
using Microsoft.Extensions.DependencyInjection;

namespace FlashSale.UnitTests;

/// <summary>
/// 以與 Production 相同的方式（掃描整個 Assembly 的 Profile）
/// 建立 IMapper，確保測試看到的映射設定就是實際執行時的設定。
/// </summary>
public static class TestMapperFactory
{
    public static IMapper Create()
    {
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddAutoMapper(cfg =>
            cfg.AddMaps(typeof(ProductProfile).Assembly));

        return services
            .BuildServiceProvider()
            .GetRequiredService<IMapper>();
    }
}
