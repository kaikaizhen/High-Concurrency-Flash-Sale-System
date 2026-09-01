using AutoMapper;
using FlashSale.Api.Common.Constants;
using FlashSale.Api.Filters;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Params.FlashSales;
using FlashSale.Api.Models.ViewModels.FlashSales;
using FlashSale.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FlashSale.Api.Controllers;

[ApiController]
[Route("api/flash-sale")]
public class FlashSaleController : ControllerBase
{
    private readonly IFlashSaleService _flashSaleService;
    private readonly IMapper _mapper;

    public FlashSaleController(
        IFlashSaleService flashSaleService,
        IMapper mapper)
    {
        _flashSaleService = flashSaleService;
        _mapper = mapper;
    }

    [HttpPost("{productId:int}")]
    [EnableRateLimiting(RateLimitPolicies.FlashSale)]
    [ServiceFilter(typeof(IdempotencyFilter))]
    [ProducesResponseType(typeof(FlashSaleResultViewModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(FlashSaleResultViewModel), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<FlashSaleResultViewModel>> PurchaseAsync(
        int productId,
        [FromBody] CreateFlashSaleParamModel param)
    {
        var dto = _mapper.Map<CreateFlashSaleDtoModel>(param);

        dto.ProductId = productId;

        // Idempotency-Key 來自 Header 而不是 Body ——
        // 它描述的是「這次傳輸」而不是「要買什麼」。
        // IdempotencyFilter 已經用它擋掉重送，這裡再往下傳一次，
        // 讓它落到訂單上成為資料庫層級的最後防線。
        if (Request.Headers.TryGetValue(IdempotencyFilter.HeaderName, out var key) &&
            !string.IsNullOrWhiteSpace(key))
        {
            dto.IdempotencyKey = key.ToString().Trim();
        }

        var result = await _flashSaleService.PurchaseAsync(dto);

        var viewModel = _mapper.Map<FlashSaleResultViewModel>(result);

        // 202 Accepted 表達的是「已受理，但還沒做完」。
        // 非同步路徑回 200 會讓客戶端誤以為訂單已經存在。
        return result.IsQueued
            ? Accepted(viewModel)
            : Ok(viewModel);
    }
}
