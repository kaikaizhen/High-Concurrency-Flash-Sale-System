using FlashSale.Api.Common.Constants;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.Interfaces;

namespace FlashSale.Api.Services.FlashSaleStrategies;

/// <summary>
/// Version B —— Optimistic Concurrency（rowversion + 重試）。
///
///     讀取 Product（含 RowVersion）
///         │
///         ▼
///     UPDATE ... WHERE Id = @id AND RowVersion = @original
///         │
///         ├── AffectedRows = 1 → 建立訂單 → COMMIT
///         │
///         └── AffectedRows = 0 → DbUpdateConcurrencyException
///                                  → ROLLBACK → 重新讀取後重試
///
/// 不上鎖，先做再說；只有衝突時才付出代價。
/// 適合衝突率低的場景 —— 但秒殺**恰好是衝突率最高的場景**，
/// 所以這裡預期會看到大量重試。這正是要量測的重點。
///
/// 每一次重試都是一次完整的「讀 + 寫」往返，直接反映在 Latency 上。
///
/// 為什麼把樂觀更新放進交易內：版本檢查在 SaveChanges 當下就完成，
/// 更新成功後該列的排他鎖會持有到 commit，因此接著建立訂單是安全的，
/// 兩者可以同生共死，不需要補償交易。
/// </summary>
public class OptimisticFlashSalePurchaseStrategy : IFlashSalePurchaseStrategy
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IProductRepository _productRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly ILogger<OptimisticFlashSalePurchaseStrategy> _logger;

    public OptimisticFlashSalePurchaseStrategy(
        IUnitOfWork unitOfWork,
        IProductRepository productRepository,
        IOrderRepository orderRepository,
        ILogger<OptimisticFlashSalePurchaseStrategy> logger)
    {
        _unitOfWork = unitOfWork;
        _productRepository = productRepository;
        _orderRepository = orderRepository;
        _logger = logger;
    }

    public FlashSaleStrategy Strategy => FlashSaleStrategy.Optimistic;

    public async Task<FlashSalePurchaseResult> PurchaseAsync(
        CreateFlashSaleDtoModel dto)
    {
        for (var attempt = 1;
             attempt <= GlobalConstants.MaxConcurrencyRetryCount;
             attempt++)
        {
            var order = await TryPurchaseOnceAsync(dto, attempt);

            if (order is not null)
            {
                return FlashSalePurchaseResult.Completed(order);
            }
        }

        _logger.LogWarning(
            "FlashSale optimistic retry exhausted. ProductId={ProductId} MaxAttempts={MaxAttempts}",
            dto.ProductId,
            GlobalConstants.MaxConcurrencyRetryCount);

        throw new BusinessException("Too many concurrent updates, please retry.");
    }

    /// <returns>成功時回傳訂單；發生版本衝突時回傳 null 代表需要重試。</returns>
    private async Task<Order?> TryPurchaseOnceAsync(
        CreateFlashSaleDtoModel dto,
        int attempt)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync();

        var product = await _productRepository.GetByIdAsync(dto.ProductId);

        if (product is null)
        {
            throw new NotFoundException("Product not found.");
        }

        if (product.Stock < dto.Quantity)
        {
            // 庫存真的不足，重試也不會變多，直接拒絕。
            throw new BusinessException("Insufficient stock.");
        }

        product.Stock -= dto.Quantity;

        var updated = await _productRepository
            .TryUpdateWithVersionAsync(product);

        if (!updated)
        {
            // 版本衝突：這一列在我們讀取之後被別人改過，
            // 手上的庫存值已經過期。放棄這次交易，重新讀取再算一次。
            _logger.LogWarning(
                "FlashSale optimistic conflict. ProductId={ProductId} Attempt={Attempt}",
                dto.ProductId,
                attempt);

            await transaction.RollbackAsync();

            return null;
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
