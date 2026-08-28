using AutoMapper;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Dtos.Orders;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.Interfaces;

namespace FlashSale.Api.Services;

/// <summary>
/// Stage 1 — CRUD Baseline 的搶購流程。
///
/// 這一版**刻意**使用一般 CRUD 思維：
///
///     Read Product → Stock > 0 ? → Stock-- → Create Order → Save
///
/// 「檢查庫存」與「扣減庫存」是兩個分開的往返，
/// 中間沒有任何 Transaction、Lock、Atomic Update 或版本控制。
/// 因此在併發下必然發生 Race Condition 與超賣。
///
/// 這是 Baseline，Stage 2 會證明問題存在，Stage 3 才修正。
/// 在 Stage 3 之前請勿在此加入併發控制。
/// </summary>
public class FlashSaleService : IFlashSaleService
{
    private readonly IProductRepository _productRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly IMapper _mapper;

    public FlashSaleService(
        IProductRepository productRepository,
        IOrderRepository orderRepository,
        IMapper mapper)
    {
        _productRepository = productRepository;
        _orderRepository = orderRepository;
        _mapper = mapper;
    }

    public async Task<OrderDtoModel> PurchaseAsync(
        CreateFlashSaleDtoModel dto)
    {
        // 1. Read
        var product = await _productRepository
            .GetByIdAsync(dto.ProductId);

        if (product is null)
        {
            throw new NotFoundException("Product not found.");
        }

        // 2. Check
        if (product.Stock < dto.Quantity)
        {
            throw new BusinessException("Insufficient stock.");
        }

        // 3. Modify
        product.Stock -= dto.Quantity;

        // 4. Write
        await _productRepository.UpdateAsync(product);

        var order = new Order
        {
            UserId = dto.UserId,
            ProductId = dto.ProductId,
            Quantity = dto.Quantity,
            Status = OrderStatus.Completed,
            CreatedAt = DateTime.UtcNow
        };

        await _orderRepository.CreateAsync(order);

        return _mapper.Map<OrderDtoModel>(order);
    }
}
