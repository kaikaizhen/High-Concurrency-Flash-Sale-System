using AutoMapper;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.Orders;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.Interfaces;

namespace FlashSale.Api.Services;

/// <summary>
/// 一般訂單流程。
///
/// 與 <see cref="FlashSaleService"/> 的差別：
/// 這裡只建立訂單（Pending），不扣庫存。
/// 扣庫存與超賣問題集中在 Flash Sale 流程觀察。
/// </summary>
public class OrderService : IOrderService
{
    private readonly IOrderRepository _orderRepository;
    private readonly IProductRepository _productRepository;
    private readonly IMapper _mapper;

    public OrderService(
        IOrderRepository orderRepository,
        IProductRepository productRepository,
        IMapper mapper)
    {
        _orderRepository = orderRepository;
        _productRepository = productRepository;
        _mapper = mapper;
    }

    public async Task<List<OrderDtoModel>> GetListByProductIdAsync(
        int productId)
    {
        var entities = await _orderRepository
            .GetListByProductIdAsync(productId);

        return _mapper.Map<List<OrderDtoModel>>(entities);
    }

    public async Task<OrderDtoModel> GetByIdAsync(int id)
    {
        var entity = await _orderRepository.GetByIdAsync(id);

        if (entity is null)
        {
            throw new NotFoundException("Order not found.");
        }

        return _mapper.Map<OrderDtoModel>(entity);
    }

    public async Task<OrderDtoModel> CreateAsync(
        CreateOrderDtoModel dto)
    {
        var product = await _productRepository
            .GetByIdAsync(dto.ProductId);

        if (product is null)
        {
            throw new NotFoundException("Product not found.");
        }

        var entity = _mapper.Map<Order>(dto);

        entity.Status = OrderStatus.Pending;
        entity.CreatedAt = DateTime.UtcNow;

        await _orderRepository.CreateAsync(entity);

        return _mapper.Map<OrderDtoModel>(entity);
    }
}
