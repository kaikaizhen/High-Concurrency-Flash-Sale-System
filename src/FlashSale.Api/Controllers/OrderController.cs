using AutoMapper;
using FlashSale.Api.Models.Dtos.Orders;
using FlashSale.Api.Models.Params.Orders;
using FlashSale.Api.Models.ViewModels.Orders;
using FlashSale.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FlashSale.Api.Controllers;

[ApiController]
[Route("api/orders")]
public class OrderController : ControllerBase
{
    private readonly IOrderService _orderService;
    private readonly IMapper _mapper;

    public OrderController(
        IOrderService orderService,
        IMapper mapper)
    {
        _orderService = orderService;
        _mapper = mapper;
    }

    /// <summary>
    /// 依商品查詢訂單。
    /// 用於驗證「送出 N 次 Request 後究竟建立了幾筆訂單」。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<OrderViewModel>>> GetListAsync(
        [FromQuery] int productId)
    {
        var result = await _orderService
            .GetListByProductIdAsync(productId);

        var viewModel = _mapper.Map<List<OrderViewModel>>(result);

        return Ok(viewModel);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderViewModel>> GetByIdAsync(int id)
    {
        var result = await _orderService.GetByIdAsync(id);

        var viewModel = _mapper.Map<OrderViewModel>(result);

        return Ok(viewModel);
    }

    [HttpPost]
    public async Task<ActionResult<OrderViewModel>> CreateAsync(
        [FromBody] CreateOrderParamModel param)
    {
        var dto = _mapper.Map<CreateOrderDtoModel>(param);

        var result = await _orderService.CreateAsync(dto);

        var viewModel = _mapper.Map<OrderViewModel>(result);

        return CreatedAtAction(
            nameof(GetByIdAsync),
            new { id = viewModel.Id },
            viewModel);
    }
}
