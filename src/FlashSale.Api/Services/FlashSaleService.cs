using AutoMapper;
using FlashSale.Api.Common.Constants;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Infrastructure.Cache;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Dtos.Orders;
using FlashSale.Api.Options;
using FlashSale.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace FlashSale.Api.Services;

/// <summary>
/// 搶購流程入口。
///
/// Stage 3 需要四種併發控制版本並存以便比較，實際邏輯拆到
/// <see cref="IFlashSalePurchaseStrategy"/> 的各個實作，
/// 這裡負責挑選、委派，以及成交後的快取失效。
/// </summary>
public class FlashSaleService : IFlashSaleService
{
    private readonly IReadOnlyDictionary<FlashSaleStrategy, IFlashSalePurchaseStrategy> _strategies;
    private readonly ICacheService _cache;
    private readonly CacheOptions _cacheOptions;
    private readonly IMapper _mapper;

    public FlashSaleService(
        IEnumerable<IFlashSalePurchaseStrategy> strategies,
        ICacheService cache,
        IOptions<CacheOptions> cacheOptions,
        IMapper mapper)
    {
        _strategies = strategies.ToDictionary(x => x.Strategy);
        _cache = cache;
        _cacheOptions = cacheOptions.Value;
        _mapper = mapper;
    }

    public async Task<FlashSalePurchaseDtoModel> PurchaseAsync(
        CreateFlashSaleDtoModel dto)
    {
        if (!_strategies.TryGetValue(dto.Strategy, out var strategy))
        {
            throw new BusinessException(
                $"Unsupported flash sale strategy: {dto.Strategy}.");
        }

        var result = await strategy.PurchaseAsync(dto);

        // 搶購改動了庫存，而商品快取裡存的正是庫存值。
        // 不在這裡清除的話，商品頁在整場秒殺期間都會顯示錯誤的庫存 ——
        // 偏偏那是最多人在看的時候。
        //
        // 成本可控：清除次數受限於庫存量（賣完就不再有成交），
        // 而讀取次數通常高出好幾個數量級。
        //
        // 清除後的下一波讀取會同時 Miss，由 Single Flight 收斂成一次查詢。
        //
        // 非同步路徑同樣要清 —— 庫存在 API 這一端就已經扣掉了，
        // 尚未建立的只有訂單。
        await InvalidateProductCacheAsync(dto.ProductId);

        return new FlashSalePurchaseDtoModel
        {
            IsQueued = result.IsQueued,
            RequestId = result.RequestId,
            Order = result.Order is null
                ? null
                : _mapper.Map<OrderDtoModel>(result.Order)
        };
    }

    private async Task InvalidateProductCacheAsync(int productId)
    {
        if (!_cacheOptions.Enabled)
        {
            return;
        }

        await _cache.RemoveAsync(CacheKeys.Product(productId));
    }
}
