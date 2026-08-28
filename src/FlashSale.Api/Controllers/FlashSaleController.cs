using AutoMapper;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Params.FlashSales;
using FlashSale.Api.Models.ViewModels.FlashSales;
using FlashSale.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

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
    [ProducesResponseType(typeof(FlashSaleResultViewModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(FlashSaleResultViewModel), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<FlashSaleResultViewModel>> PurchaseAsync(
        int productId,
        [FromBody] CreateFlashSaleParamModel param)
    {
        var dto = _mapper.Map<CreateFlashSaleDtoModel>(param);

        dto.ProductId = productId;

        var result = await _flashSaleService.PurchaseAsync(dto);

        var viewModel = _mapper.Map<FlashSaleResultViewModel>(result);

        // 202 Accepted 表達的是「已受理，但還沒做完」。
        // 非同步路徑回 200 會讓客戶端誤以為訂單已經存在。
        return result.IsQueued
            ? Accepted(viewModel)
            : Ok(viewModel);
    }
}
