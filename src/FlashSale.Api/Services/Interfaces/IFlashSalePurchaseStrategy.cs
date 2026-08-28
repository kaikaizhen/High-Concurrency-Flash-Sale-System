using FlashSale.Api.Common.Enums;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Entities;

namespace FlashSale.Api.Services.Interfaces;

/// <summary>
/// 搶購的結果。
///
/// Stage 5 之後有兩種本質不同的成功：
///
///   Completed —— 訂單已經寫進資料庫，Order 可用。
///   Queued    —— 庫存已扣減，訂單交給 Worker 稍後建立，此刻還沒有 Order。
///
/// 用 Id = 0 的假 Order 來表達後者會是謊言，
/// 呼叫端無法分辨「訂單還沒建立」與「訂單建立失敗」，所以獨立成一個型別。
/// </summary>
public sealed class FlashSalePurchaseResult
{
    private FlashSalePurchaseResult(bool isQueued, Guid requestId, Order? order)
    {
        IsQueued = isQueued;
        RequestId = requestId;
        Order = order;
    }

    /// <summary>true = 訂單尚未建立，已排入佇列。</summary>
    public bool IsQueued { get; }

    /// <summary>本次請求的追蹤碼。Stage 6 會演變成 Idempotency-Key。</summary>
    public Guid RequestId { get; }

    /// <summary>僅同步路徑有值。</summary>
    public Order? Order { get; }

    public static FlashSalePurchaseResult Completed(Order order)
    {
        return new FlashSalePurchaseResult(false, Guid.NewGuid(), order);
    }

    public static FlashSalePurchaseResult Queued(Guid requestId)
    {
        return new FlashSalePurchaseResult(true, requestId, null);
    }
}

/// <summary>
/// 一種併發控制做法。Stage 3 讓多個實作並存以便互相比較，
/// Stage 5 再加入一個把訂單建立非同步化的版本。
/// </summary>
public interface IFlashSalePurchaseStrategy
{
    FlashSaleStrategy Strategy { get; }

    /// <summary>
    /// 扣減庫存並建立訂單（或將訂單建立排入佇列）。
    /// </summary>
    /// <exception cref="Common.Exceptions.NotFoundException">商品不存在。</exception>
    /// <exception cref="Common.Exceptions.BusinessException">庫存不足或衝突重試次數用盡。</exception>
    Task<FlashSalePurchaseResult> PurchaseAsync(CreateFlashSaleDtoModel dto);
}
