using FlashSale.Api.Data;
using FlashSale.Api.Mappings;
using FlashSale.Api.Repositories;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services;
using FlashSale.Api.Services.FlashSaleStrategies;
using FlashSale.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Extensions;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddApplicationDependencies(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        RegisterServices(services);
        RegisterRepositories(services);
        RegisterDatabase(services, configuration);
        RegisterMappings(services);
        RegisterOptions(services, configuration);
        RegisterInfrastructureServices(services);

        return services;
    }

    private static void RegisterServices(
        IServiceCollection services)
    {
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IFlashSaleService, FlashSaleService>();

        RegisterFlashSaleStrategies(services);
    }

    /// <summary>
    /// Stage 3：四種併發控制版本並存以便比較。
    /// FlashSaleService 注入 IEnumerable&lt;IFlashSalePurchaseStrategy&gt; 後
    /// 依 DtoModel 指定的 Strategy 委派。
    /// </summary>
    private static void RegisterFlashSaleStrategies(
        IServiceCollection services)
    {
        services.AddScoped<IFlashSalePurchaseStrategy, BaselineFlashSalePurchaseStrategy>();
        services.AddScoped<IFlashSalePurchaseStrategy, TransactionFlashSalePurchaseStrategy>();
        services.AddScoped<IFlashSalePurchaseStrategy, OptimisticFlashSalePurchaseStrategy>();
        services.AddScoped<IFlashSalePurchaseStrategy, AtomicFlashSalePurchaseStrategy>();
    }

    private static void RegisterRepositories(
        IServiceCollection services)
    {
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
    }

    private static void RegisterDatabase(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString(
                    "DefaultConnection"));
        });
    }

    private static void RegisterMappings(
        IServiceCollection services)
    {
        services.AddAutoMapper(cfg =>
            cfg.AddMaps(typeof(ProductProfile).Assembly));
    }

    private static void RegisterOptions(
        IServiceCollection services,
        IConfiguration configuration)
    {
        // Stage 1 尚無 Options。
        // Stage 4 (Redis) 與 Stage 5 (RabbitMQ) 會在此註冊。
    }

    private static void RegisterInfrastructureServices(
        IServiceCollection services)
    {
        // Stage 1 尚無外部 I/O。
        // Stage 4 (Redis) 與 Stage 5 (RabbitMQ) 會在此註冊。
    }
}
