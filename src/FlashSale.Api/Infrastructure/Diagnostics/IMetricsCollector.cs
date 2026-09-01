using FlashSale.Api.Models.Dtos.Diagnostics;

namespace FlashSale.Api.Infrastructure.Diagnostics;

/// <summary>
/// Stage 4 用來回答「加 Redis 前後，5000 個請求各打了幾次資料庫」。
///
/// 已知限制：計數器存在單一 Instance 的記憶體中。
/// Stage 8 導入多 Instance 之後，每台機器只會看到自己的數字 ——
/// 這正是 Stage 8 要面對的 Stateless 問題的一個具體例子。
/// </summary>
public interface IMetricsCollector
{
    void RecordDbCommand();

    void RecordCacheHit();

    void RecordCacheMiss();

    void RecordCacheError();

    MetricsDtoModel GetSnapshot();

    void Reset();
}
