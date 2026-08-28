using AutoMapper;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Dtos.Orders;
using FlashSale.Api.Services.Interfaces;

namespace FlashSale.Api.Services;

/// <summary>
/// 搶購流程入口。
///
/// Stage 3 需要四種併發控制版本並存以便比較，實際邏輯拆到
/// <see cref="IFlashSalePurchaseStrategy"/> 的各個實作，
/// 這裡只負責挑選並委派。
/// </summary>
public class FlashSaleService : IFlashSaleService
{
    private readonly IReadOnlyDictionary<Common.Enums.FlashSaleStrategy, IFlashSalePurchaseStrategy> _strategies;
    private readonly IMapper _mapper;

    public FlashSaleService(
        IEnumerable<IFlashSalePurchaseStrategy> strategies,
        IMapper mapper)
    {
        _strategies = strategies.ToDictionary(x => x.Strategy);
        _mapper = mapper;
    }

    public async Task<OrderDtoModel> PurchaseAsync(
        CreateFlashSaleDtoModel dto)
    {
        if (!_strategies.TryGetValue(dto.Strategy, out var strategy))
        {
            throw new BusinessException(
                $"Unsupported flash sale strategy: {dto.Strategy}.");
        }

        var order = await strategy.PurchaseAsync(dto);

        return _mapper.Map<OrderDtoModel>(order);
    }
}
