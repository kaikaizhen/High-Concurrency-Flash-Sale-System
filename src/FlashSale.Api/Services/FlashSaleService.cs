using System.Diagnostics;
using AutoMapper;
using FlashSale.Api.Common.Constants;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Infrastructure.Cache;
using FlashSale.Api.Infrastructure.Observability;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Dtos.Orders;
using FlashSale.Api.Options;
using FlashSale.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace FlashSale.Api.Services;

/// <summary>
/// 搶購流程入口。
///
/// Stage 3 需要多種併發控制版本並存以便比較，實際邏輯拆到
/// <see cref="IFlashSalePurchaseStrategy"/> 的各個實作，
/// 這裡負責挑選、委派、成交後的快取失效，以及 Stage 10 的觀測。
///
/// 觀測放在這一層而不是各個策略裡：
/// 每個策略都自己記一次，就會出現「有的策略記了、有的忘了」，
/// 而指標一旦不完整就無法比較。
/// </summary>
public class FlashSaleService : IFlashSaleService
{
    private readonly IReadOnlyDictionary<FlashSaleStrategy, IFlashSalePurchaseStrategy> _strategies;
    private readonly ICacheService _cache;
    private readonly CacheOptions _cacheOptions;
    private readonly FlashSaleMetrics _metrics;
    private readonly IMapper _mapper;

    public FlashSaleService(
        IEnumerable<IFlashSalePurchaseStrategy> strategies,
        ICacheService cache,
        IOptions<CacheOptions> cacheOptions,
        FlashSaleMetrics metrics,
        IMapper mapper)
    {
        _strategies = strategies.ToDictionary(x => x.Strategy);
        _cache = cache;
        _cacheOptions = cacheOptions.Value;
        _metrics = metrics;
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

        using var activity = FlashSaleActivitySource.StartPurchase(
            dto.Strategy.ToString(),
            dto.ProductId);

        _metrics.RecordAttempt(dto.Strategy);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await strategy.PurchaseAsync(dto);

            // 搶購改動了庫存，而商品快取裡存的正是庫存值。
            // 不在這裡清除的話，商品頁在整場秒殺期間都會顯示錯誤的庫存 ——
            // 偏偏那是最多人在看的時候。
            //
            // 成本可控：清除次數受限於庫存量（賣完就不再有成交），
            // 而讀取次數通常高出好幾個數量級。
            //
            // 非同步路徑同樣要清 —— 庫存在 API 這一端就已經扣掉了，
            // 尚未建立的只有訂單。
            await InvalidateProductCacheAsync(dto.ProductId);

            _metrics.RecordOrderCreated(dto.Strategy, result.IsQueued);

            activity?.SetTag("flashsale.result", result.IsQueued ? "queued" : "completed");
            activity?.SetTag("flashsale.request_id", result.RequestId);

            return new FlashSalePurchaseDtoModel
            {
                IsQueued = result.IsQueued,
                RequestId = result.RequestId,
                Order = result.Order is null
                    ? null
                    : _mapper.Map<OrderDtoModel>(result.Order)
            };
        }
        catch (BusinessException ex)
        {
            // 拒絕的**原因**才是有價值的資訊。
            // 「拒絕數上升」本身沒有資訊量 —— 庫存賣完是正常的，
            // 樂觀鎖重試用盡代表系統過載，兩者的處置完全不同。
            _metrics.RecordRejected(dto.Strategy, Classify(ex.Message));

            activity?.SetTag("flashsale.result", "rejected");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            throw;
        }
        catch (NotFoundException)
        {
            _metrics.RecordRejected(dto.Strategy, "product_not_found");

            activity?.SetTag("flashsale.result", "not_found");

            throw;
        }
        finally
        {
            stopwatch.Stop();

            _metrics.RecordPurchaseDuration(
                dto.Strategy,
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// 把訊息歸類成少數幾個固定的值。
    ///
    /// 直接把 Exception 訊息當成標籤會造成「高基數」問題：
    /// 每個不同的字串都會產生一組新的時序資料，
    /// 訊息若含有 Id 或數值，指標系統會被撐爆。
    /// </summary>
    private static string Classify(string message)
    {
        if (message.Contains("Insufficient stock", StringComparison.OrdinalIgnoreCase))
        {
            return "insufficient_stock";
        }

        if (message.Contains("concurrent updates", StringComparison.OrdinalIgnoreCase))
        {
            return "concurrency_retry_exhausted";
        }

        if (message.Contains("Idempotency-Key", StringComparison.OrdinalIgnoreCase))
        {
            return "duplicate_request";
        }

        return "other";
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
