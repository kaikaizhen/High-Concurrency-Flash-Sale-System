namespace FlashSale.Api.Common.Enums;

public enum IdempotencyStatus
{
    /// <summary>
    /// 已被某個請求佔用，但還沒處理完。
    ///
    /// 這個狀態存在的唯一理由是**併發重複**：
    /// 兩個帶相同 Key 的請求同時抵達時，只有一個能佔用成功，
    /// 另一個看到這個狀態就知道「有人正在做同一件事」。
    ///
    /// 少了它，兩個請求都會看到「查無此 Key」而各自建立訂單。
    /// </summary>
    InProgress = 0,

    /// <summary>
    /// 已處理完成，回應內容已保存。重送時直接回放。
    /// </summary>
    Completed = 1
}
