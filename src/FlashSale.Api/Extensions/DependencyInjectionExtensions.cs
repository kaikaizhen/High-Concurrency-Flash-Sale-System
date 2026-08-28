using FlashSale.Api.Data;
using FlashSale.Api.Data.Interceptors;
using FlashSale.Api.Infrastructure.Cache;
using FlashSale.Api.Infrastructure.Diagnostics;
using FlashSale.Api.Mappings;
using FlashSale.Api.Options;
using FlashSale.Api.Repositories;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services;
using FlashSale.Api.Services.FlashSaleStrategies;
using FlashSale.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

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
        RegisterInfrastructureServices(services, configuration);

        return services;
    }

    private static void RegisterServices(
        IServiceCollection services)
    {
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IFlashSaleService, FlashSaleService>();
        services.AddScoped<IDiagnosticsService, DiagnosticsService>();

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
        services.AddDbContext<AppDbContext>((provider, options) =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString(
                    "DefaultConnection"));

            // Stage 4：精確計算送到資料庫的命令數，用於 Before / After 比較。
            options.AddInterceptors(
                provider.GetRequiredService<MetricsDbCommandInterceptor>());
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
        services.Configure<RedisOptions>(
            configuration.GetSection(RedisOptions.SectionName));

        services.Configure<CacheOptions>(
            configuration.GetSection(CacheOptions.SectionName));
    }

    private static void RegisterInfrastructureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        // 計數器與鎖必須是 Singleton，否則每個請求都會拿到全新的實例。
        services.AddSingleton<IMetricsCollector, InMemoryMetricsCollector>();
        services.AddSingleton<IKeyedLock, KeyedLock>();
        services.AddSingleton<MetricsDbCommandInterceptor>();

        RegisterRedis(services, configuration);

        services.AddScoped<ICacheService, RedisCacheService>();
    }

    /// <summary>
    /// IConnectionMultiplexer 是執行緒安全且設計為整個應用程式共用一個實例，
    /// 每個請求各建一條連線會直接把 Redis 的連線數打爆。
    /// </summary>
    private static void RegisterRedis(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var redisOptions = configuration
            .GetSection(RedisOptions.SectionName)
            .Get<RedisOptions>() ?? new RedisOptions();

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var config = ConfigurationOptions.Parse(
                redisOptions.Configuration);

            config.AbortOnConnectFail = false;
            config.ConnectTimeout = redisOptions.ConnectTimeoutMs;

            if (!string.IsNullOrWhiteSpace(redisOptions.User))
            {
                config.User = redisOptions.User;
            }

            if (!string.IsNullOrWhiteSpace(redisOptions.Password))
            {
                config.Password = redisOptions.Password;
            }

            return ConnectionMultiplexer.Connect(config);
        });
    }
}
