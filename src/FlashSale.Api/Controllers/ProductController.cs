using AutoMapper;
using FlashSale.Api.Models.Dtos.Products;
using FlashSale.Api.Models.Params.Products;
using FlashSale.Api.Models.ViewModels.Products;
using FlashSale.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FlashSale.Api.Controllers;

[ApiController]
[Route("api/products")]
public class ProductController : ControllerBase
{
    private readonly IProductService _productService;
    private readonly IMapper _mapper;

    public ProductController(
        IProductService productService,
        IMapper mapper)
    {
        _productService = productService;
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<ActionResult<List<ProductViewModel>>> GetListAsync()
    {
        var result = await _productService.GetListAsync();

        var viewModel = _mapper.Map<List<ProductViewModel>>(result);

        return Ok(viewModel);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductViewModel>> GetByIdAsync(int id)
    {
        var result = await _productService.GetByIdAsync(id);

        var viewModel = _mapper.Map<ProductViewModel>(result);

        return Ok(viewModel);
    }

    [HttpPost]
    public async Task<ActionResult<ProductViewModel>> CreateAsync(
        [FromBody] CreateProductParamModel param)
    {
        var dto = _mapper.Map<CreateProductDtoModel>(param);

        var result = await _productService.CreateAsync(dto);

        var viewModel = _mapper.Map<ProductViewModel>(result);

        return CreatedAtAction(
            nameof(GetByIdAsync),
            new { id = viewModel.Id },
            viewModel);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ProductViewModel>> UpdateAsync(
        int id,
        [FromBody] UpdateProductParamModel param)
    {
        var dto = _mapper.Map<UpdateProductDtoModel>(param);

        dto.Id = id;

        var result = await _productService.UpdateAsync(dto);

        var viewModel = _mapper.Map<ProductViewModel>(result);

        return Ok(viewModel);
    }
}
