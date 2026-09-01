using FlashSale.Api.Data;
using FlashSale.Api.Data.Interceptors;
using FlashSale.Api.Filters;
using FlashSale.Api.Infrastructure.Cache;
using FlashSale.Api.Infrastructure.Idempotency;
using FlashSale.Api.Infrastructure.Diagnostics;
using FlashSale.Api.Infrastructure.Messaging;
using FlashSale.Api.Infrastructure.RateLimiting;
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
        services.AddScoped<IFlashSalePurchaseStrategy, QueuedAtomicFlashSalePurchaseStrategy>();
        services.AddScoped<IFlashSalePurchaseStrategy, AtomicBatchedFlashSalePurchaseStrategy>();
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

        services.Configure<RabbitMqOptions>(
            configuration.GetSection(RabbitMqOptions.SectionName));

        services.Configure<IdempotencyOptions>(
            configuration.GetSection(IdempotencyOptions.SectionName));

        services.Configure<RateLimitOptions>(
            configuration.GetSection(RateLimitOptions.SectionName));

        services.Configure<SharedStateOptions>(
            configuration.GetSection(SharedStateOptions.SectionName));
    }

    private static void RegisterInfrastructureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<MetricsDbCommandInterceptor>();

        RegisterRedis(services, configuration);
        RegisterSharedState(services, configuration);

        services.AddScoped<ICacheService, RedisCacheService>();

        RegisterMessaging(services);
        RegisterIdempotency(services, configuration);
    }

    /// <summary>
    /// Stage 6：冪等記錄的儲存體。
    ///
    /// 計畫 §11 要求比較 SQL Server 與 Redis 兩種做法，因此兩個實作並存，
    /// 由設定決定實際註冊哪一個。
    /// </summary>
    private static void RegisterIdempotency(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection(IdempotencyOptions.SectionName)
            .Get<IdempotencyOptions>() ?? new IdempotencyOptions();

        if (options.Provider == IdempotencyProvider.SqlServer)
        {
            // SQL Server 版依賴 DbContext，必須是 Scoped。
            services.AddScoped<IIdempotencyStore, SqlServerIdempotencyStore>();
        }
        else
        {
            services.AddScoped<IIdempotencyStore, RedisIdempotencyStore>();
        }

        services.AddScoped<IdempotencyFilter>();
    }

    /// <summary>
    /// Stage 5：RabbitMQ。
    ///
    /// 連線是 Singleton（建立成本高，共用一條）；
    /// Publisher 與 Inspector 每次都會自己借用一個 Channel，
    /// 因為 IChannel 不是執行緒安全的。
    /// </summary>
    private static void RegisterMessaging(IServiceCollection services)
    {
        services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
        services.AddSingleton<IChannelPool, ChannelPool>();
        services.AddSingleton<RabbitMqTopology>();

        // Publisher 與 Inspector 都是無狀態的 —— 每次呼叫自己借一個 Channel，
        // 不持有任何跨呼叫的狀態，所以註冊為 Singleton。
        //
        // 這也是必要的：Worker 的 OrderCreatedConsumer 是 BackgroundService
        // （Singleton），Singleton 無法注入 Scoped 服務。
        services.AddSingleton<IMessagePublisher, RabbitMqMessagePublisher>();
        services.AddSingleton<IQueueInspector, RabbitMqQueueInspector>();
    }

    /// <summary>
    /// Stage 8：跨 Instance 共用狀態。
    ///
    /// 三個元件都是 Singleton —— 它們持有的是「整個應用程式共用的東西」
    /// （計數器、鎖、限流額度），每個請求各建一份等於沒有共用。
    /// </summary>
    private static void RegisterSharedState(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection(SharedStateOptions.SectionName)
            .Get<SharedStateOptions>() ?? new SharedStateOptions();

        if (options.DistributedMetrics)
        {
            services.AddSingleton<IMetricsCollector, RedisMetricsCollector>();
        }
        else
        {
            services.AddSingleton<IMetricsCollector, InMemoryMetricsCollector>();
        }

        if (options.DistributedLock)
        {
            services.AddSingleton<IKeyedLock, RedisKeyedLock>();
        }
        else
        {
            services.AddSingleton<IKeyedLock, KeyedLock>();
        }

        services.AddSingleton<IDistributedRateLimiter, RedisSlidingWindowRateLimiter>();

        // 必須是 Singleton：CPU 使用率要靠「兩次取樣的差」計算，
        // 每個請求各建一份就永遠拿不到基準點。
        services.AddSingleton<ISystemMetricsProvider, SystemMetricsProvider>();
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
