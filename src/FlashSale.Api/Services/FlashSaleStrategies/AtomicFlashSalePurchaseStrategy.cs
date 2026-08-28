using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.Interfaces;

namespace FlashSale.Api.Services.FlashSaleStrategies;

/// <summary>
/// Version C —— Atomic Update。
///
///     BEGIN TRAN
///       UPDATE Products
///       SET Stock = Stock - @qty
///       WHERE Id = @id AND Stock >= @qty      ← 檢查與扣減同一個語句
///
///       AffectedRows = 1 → INSERT Order → COMMIT
///       AffectedRows = 0 → 庫存不足 → ROLLBACK
///     COMMIT
///
/// 關鍵差異：**應用程式完全不讀取庫存**。
///
/// 前面兩個版本都是「先把庫存讀到應用程式，算完再寫回去」，
/// 差別只在於用什麼手段防止讀寫之間被插隊。
/// 這個版本根本不把庫存讀出來 —— 檢查與減法都在資料庫端的同一個語句內完成，
/// 由資料庫對該列的排他鎖保證原子性，Read-Modify-Write 的視窗直接消失。
///
/// 少一次 SELECT 往返，且鎖只在單一 UPDATE 語句期間持有，
/// 因此預期是三者中最快的。
/// </summary>
public class AtomicFlashSalePurchaseStrategy : IFlashSalePurchaseStrategy
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IProductRepository _productRepository;
    private readonly IOrderRepository _orderRepository;

    public AtomicFlashSalePurchaseStrategy(
        IUnitOfWork unitOfWork,
        IProductRepository productRepository,
        IOrderRepository orderRepository)
    {
        _unitOfWork = unitOfWork;
        _productRepository = productRepository;
        _orderRepository = orderRepository;
    }

    public FlashSaleStrategy Strategy => FlashSaleStrategy.Atomic;

    public async Task<Order> PurchaseAsync(CreateFlashSaleDtoModel dto)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync();

        var affected = await _productRepository
            .TryDeductStockAsync(dto.ProductId, dto.Quantity);

        if (affected == 0)
        {
            // 影響列數為 0 有兩種可能：商品不存在，或庫存不足。
            // 為了回傳正確的狀態碼，這時才需要多讀一次 —— 只在失敗路徑上發生。
            var exists = await _productRepository.GetByIdAsync(dto.ProductId);

            if (exists is null)
            {
                throw new NotFoundException("Product not found.");
            }

            throw new BusinessException("Insufficient stock.");
        }

        var order = new Order
        {
            UserId = dto.UserId,
            ProductId = dto.ProductId,
            Quantity = dto.Quantity,
            Status = OrderStatus.Completed,
            CreatedAt = DateTime.UtcNow
        };

        await _orderRepository.CreateAsync(order);

        await transaction.CommitAsync();

        return order;
    }
}
