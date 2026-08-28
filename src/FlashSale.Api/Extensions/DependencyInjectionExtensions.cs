using FlashSale.Api.Data;
using FlashSale.Api.Mappings;
using FlashSale.Api.Repositories;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services;
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
    }

    private static void RegisterRepositories(
        IServiceCollection services)
    {
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
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
