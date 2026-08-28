using AutoMapper;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Params.FlashSales;
using FlashSale.Api.Models.ViewModels.Orders;
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
    public async Task<ActionResult<OrderViewModel>> PurchaseAsync(
        int productId,
        [FromBody] CreateFlashSaleParamModel param)
    {
        var dto = _mapper.Map<CreateFlashSaleDtoModel>(param);

        dto.ProductId = productId;

        var result = await _flashSaleService.PurchaseAsync(dto);

        var viewModel = _mapper.Map<OrderViewModel>(result);

        return Ok(viewModel);
    }
}
