namespace FlashSale.Api.Common.Enums;

/// <summary>
/// 搶購時採用的併發控制策略。
///
/// Stage 3 需要四個版本並存才能互相比較，因此讓呼叫端指定。
/// 比較完成、選定主要方案後，未指定時一律走 <see cref="Atomic"/>。
/// </summary>
public enum FlashSaleStrategy
{
    /// <summary>
    /// Stage 1 Baseline：Read → Check → Modify → Write，無任何保護。
    /// 保留作為對照組，**不可用於正式流程**。
    /// </summary>
    Baseline = 0,

    /// <summary>
    /// Version A：Transaction + 悲觀鎖（UPDLOCK）。
    /// </summary>
    Transaction = 1,

    /// <summary>
    /// Version B：Optimistic Concurrency（rowversion + 重試）。
    /// </summary>
    Optimistic = 2,

    /// <summary>
    /// Version C：Atomic Update（在資料庫端做減法，以 AffectedRows 判斷）。
    /// </summary>
    Atomic = 3
}
