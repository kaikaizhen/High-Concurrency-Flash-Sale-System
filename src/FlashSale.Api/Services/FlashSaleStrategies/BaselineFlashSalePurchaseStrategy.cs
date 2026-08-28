using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.Interfaces;

namespace FlashSale.Api.Services.FlashSaleStrategies;

/// <summary>
/// Stage 1 Baseline —— **對照組，不是可用的方案**。
///
///     Read Product → Stock >= Qty ? → Stock -= Qty → UPDATE → INSERT Order
///
/// 沒有交易、沒有鎖、沒有版本檢查，且庫存是被整個覆寫而非在資料庫端做減法。
/// Stage 2 已證明它會 Lost Update 與超賣（docs/load-test/race-condition.md）。
///
/// 保留它的唯一理由：Stage 3 的比較需要一個「什麼都不做」的基準線，
/// 而且必須在同一次測試、同一台機器上量測才有可比性。
/// </summary>
public class BaselineFlashSalePurchaseStrategy : IFlashSalePurchaseStrategy
{
    private readonly IProductRepository _productRepository;
    private readonly IOrderRepository _orderRepository;

    public BaselineFlashSalePurchaseStrategy(
        IProductRepository productRepository,
        IOrderRepository orderRepository)
    {
        _productRepository = productRepository;
        _orderRepository = orderRepository;
    }

    public FlashSaleStrategy Strategy => FlashSaleStrategy.Baseline;

    public async Task<Order> PurchaseAsync(CreateFlashSaleDtoModel dto)
    {
        var product = await _productRepository.GetByIdAsync(dto.ProductId);

        if (product is null)
        {
            throw new NotFoundException("Product not found.");
        }

        if (product.Stock < dto.Quantity)
        {
            throw new BusinessException("Insufficient stock.");
        }

        var newStock = product.Stock - dto.Quantity;

        // 刻意繞過 rowversion 檢查，重現 Stage 1 的 Lost Update。
        await _productRepository.OverwriteStockWithoutVersionCheckAsync(
            dto.ProductId,
            newStock);

        var order = new Order
        {
            UserId = dto.UserId,
            ProductId = dto.ProductId,
            Quantity = dto.Quantity,
            Status = OrderStatus.Completed,
            CreatedAt = DateTime.UtcNow
        };

        await _orderRepository.CreateAsync(order);

        return order;
    }
}
