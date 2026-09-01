using FlashSale.Api.Common.Enums;

namespace FlashSale.Api.Models.Entities;

/// <summary>
/// 冪等記錄（SQL Server 版儲存體）。
///
/// 計畫 §11 指定要保存的欄位：
/// IdempotencyKey / Status / Response / CreatedAt / ExpiresAt。
/// </summary>
public class IdempotencyRecord
{
    /// <summary>
    /// 主鍵就是 Key 本身。
    ///
    /// 用主鍵而不是加索引的一般欄位，是因為要靠**主鍵衝突**來達成
    /// 「檢查 + 建立」的原子性 —— 兩個併發 INSERT 只有一個會成功。
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public IdempotencyStatus Status { get; set; }

    public int StatusCode { get; set; }

    public string? ResponseBody { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 過期時間。SQL Server 沒有 Redis 那種自動過期，
    /// 因此讀取時必須自行判斷，並需要另外的清理機制（見 §已知限制）。
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}
