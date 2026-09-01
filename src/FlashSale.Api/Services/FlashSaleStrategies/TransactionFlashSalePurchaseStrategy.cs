using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.Interfaces;

namespace FlashSale.Api.Services.FlashSaleStrategies;

/// <summary>
/// Version A —— Transaction + 悲觀鎖（UPDLOCK）。
///
///     BEGIN TRAN
///       SELECT ... WITH (UPDLOCK, ROWLOCK)   ← 取得更新鎖，持有到交易結束
///       檢查庫存
///       UPDATE Stock
///       INSERT Order
///     COMMIT
///
/// 關鍵在 UPDLOCK，不在 Transaction 本身。
/// 單純把四個步驟包進交易，在 SQL Server 預設的 READ COMMITTED 下
/// 共享鎖讀完就釋放，兩個請求照樣會讀到同一個庫存值 —— 超賣不會消失。
/// UPDLOCK 讓「讀取」就取得更新鎖，同一列的第二個請求必須排隊等待，
/// 從而把並行執行變成串行執行。
///
/// 代價就是排隊：正確性換 Throughput。
/// </summary>
public class TransactionFlashSalePurchaseStrategy : IFlashSalePurchaseStrategy
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IProductRepository _productRepository;
    private readonly IOrderRepository _orderRepository;

    public TransactionFlashSalePurchaseStrategy(
        IUnitOfWork unitOfWork,
        IProductRepository productRepository,
        IOrderRepository orderRepository)
    {
        _unitOfWork = unitOfWork;
        _productRepository = productRepository;
        _orderRepository = orderRepository;
    }

    public FlashSaleStrategy Strategy => FlashSaleStrategy.Transaction;

    public async Task<FlashSalePurchaseResult> PurchaseAsync(CreateFlashSaleDtoModel dto)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync();

        var product = await _productRepository
            .GetByIdWithUpdateLockAsync(dto.ProductId);

        if (product is null)
        {
            throw new NotFoundException("Product not found.");
        }

        if (product.Stock < dto.Quantity)
        {
            throw new BusinessException("Insufficient stock.");
        }

        product.Stock -= dto.Quantity;

        await _productRepository.UpdateAsync(product);

        var order = new Order
        {
            UserId = dto.UserId,
            ProductId = dto.ProductId,
            Quantity = dto.Quantity,
            Status = OrderStatus.Completed,
            CreatedAt = DateTime.UtcNow,

            // 讓資料庫的篩選唯一索引成為重複訂單的最後一道防線。
            // 沒帶 Idempotency-Key 時為 null，該索引已排除 NULL。
            IdempotencyKey = dto.IdempotencyKey
        };

        var created = await _orderRepository.TryCreateAsync(order);

        if (!created)
        {
            // 見 AtomicFlashSalePurchaseStrategy 的說明：
            // 資料庫唯一索引是最後一道防線，擋下時必須 Rollback 還原庫存。
            await transaction.RollbackAsync();

            throw new BusinessException(
                "A request with the same Idempotency-Key has already been processed.");
        }

        await transaction.CommitAsync();

        return FlashSalePurchaseResult.Completed(order);
    }
}
