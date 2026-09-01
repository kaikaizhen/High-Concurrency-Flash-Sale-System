using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Services.FlashSaleStrategies;

/// <summary>
/// Stage 10 的優化成果 —— 單一往返版的 Atomic Update。
///
/// **這是依數據推導出來的優化，不是憑感覺。**
///
///     Measure          Stage 9 量到搶購路徑 141 RPS、CPU 僅 1.3%
///     Find Bottleneck  不是 CPU、記憶體或網路頻寬，是庫存那一列的排他鎖
///     Hypothesis       Atomic 版是四次往返，鎖從 UPDATE 持有到 COMMIT，
///                      橫跨三次往返延遲。把整段送成一個命令，
///                      鎖只在伺服器端執行期間持有，臨界區應大幅縮短
///     Change           見 IProductRepository.TryPurchaseInSingleRoundTripAsync
///     Measure Again    見 docs/final-analysis.md
///
/// 與 <see cref="AtomicFlashSalePurchaseStrategy"/> 的**語意完全相同** ——
/// 一樣的正確性保證、一樣的冪等防護、一樣的回應。
/// 差別只在往返次數。這是刻意的：優化不該改變行為，
/// 否則量到的差異分不清是「變快了」還是「少做了事」。
/// </summary>
public class AtomicBatchedFlashSalePurchaseStrategy : IFlashSalePurchaseStrategy
{
    private readonly IProductRepository _productRepository;

    public AtomicBatchedFlashSalePurchaseStrategy(
        IProductRepository productRepository)
    {
        _productRepository = productRepository;
    }

    public FlashSaleStrategy Strategy => FlashSaleStrategy.AtomicBatched;

    public async Task<FlashSalePurchaseResult> PurchaseAsync(
        CreateFlashSaleDtoModel dto)
    {
        var createdAt = DateTime.UtcNow;

        int orderId;

        try
        {
            orderId = await _productRepository.TryPurchaseInSingleRoundTripAsync(
                dto.ProductId,
                dto.Quantity,
                dto.UserId,
                dto.IdempotencyKey,
                createdAt);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new BusinessException(
                "A request with the same Idempotency-Key has already been processed.");
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // 走 SqlQuery 而不是 SaveChanges，唯一索引違反會直接是
            // SqlException 而不是包在 DbUpdateException 裡。
            // XACT_ABORT ON 已經讓那筆交易整個回滾，庫存不會憑空少掉。
            throw new BusinessException(
                "A request with the same Idempotency-Key has already been processed.");
        }

        if (orderId == 0)
        {
            // 影響列數為 0 有兩種可能：商品不存在，或庫存不足。
            // 為了回傳正確的狀態碼才多讀一次 —— 只在失敗路徑上發生，
            // 成功路徑仍然只有一次往返。
            var exists = await _productRepository.GetByIdAsync(dto.ProductId);

            if (exists is null)
            {
                throw new NotFoundException("Product not found.");
            }

            throw new BusinessException("Insufficient stock.");
        }

        return FlashSalePurchaseResult.Completed(new Order
        {
            Id = orderId,
            UserId = dto.UserId,
            ProductId = dto.ProductId,
            Quantity = dto.Quantity,
            Status = OrderStatus.Completed,
            CreatedAt = createdAt,
            IdempotencyKey = dto.IdempotencyKey
        });
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqlException sql &&
               sql.Number is 2601 or 2627;
    }
}
