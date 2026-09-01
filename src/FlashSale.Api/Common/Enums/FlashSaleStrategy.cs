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
    Atomic = 3,

    /// <summary>
    /// Stage 5：Atomic Update 扣庫存（仍然同步），訂單建立交給 RabbitMQ Worker。
    ///
    /// 回應為 202 Accepted，此時訂單尚未寫入資料庫。
    /// </summary>
    AtomicQueued = 4,

    /// <summary>
    /// Stage 10 的優化成果：把扣庫存與建訂單合併成**單一批次語句**。
    ///
    /// Atomic 版本是 BEGIN TRAN / UPDATE / INSERT / COMMIT 四次網路往返，
    /// 而庫存那一列的排他鎖從 UPDATE 一直持有到 COMMIT ——
    /// 橫跨三次往返。秒殺時所有人搶同一列，那段時間就是全系統的序列化瓶頸。
    ///
    /// 這個版本把整段送成一個命令，鎖只在伺服器端執行期間持有。
    /// </summary>
    AtomicBatched = 5
}
