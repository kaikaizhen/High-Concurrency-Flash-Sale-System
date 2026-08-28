using FlashSale.Api.Models.Entities;

namespace FlashSale.Api.Repositories.Interfaces;

public interface IProductRepository
{
    Task<List<Product>> GetListAsync();

    Task<Product?> GetByIdAsync(int id);

    Task<bool> ExistsByNameAsync(string name);

    Task CreateAsync(Product entity);

    Task UpdateAsync(Product entity);

    // --- Stage 3 Version A：Transaction + 悲觀鎖 ---

    /// <summary>
    /// 以 <c>UPDLOCK, ROWLOCK</c> 讀取商品。
    ///
    /// 更新鎖會持有到**交易結束**為止，期間其他人可以讀，但不能取得
    /// 同一列的更新鎖，因此無法在此期間搶先修改庫存。
    /// 必須在交易內呼叫，否則鎖會在語句結束時立刻釋放。
    /// </summary>
    Task<Product?> GetByIdWithUpdateLockAsync(int id);

    // --- Stage 3 Version B：Optimistic Concurrency ---

    /// <summary>
    /// 帶版本檢查的更新。
    ///
    /// 送出的 SQL 附帶 <c>WHERE Id = @id AND RowVersion = @original</c>，
    /// 若影響列數為 0 代表資料已被別人改過，回傳 <c>false</c>。
    /// 呼叫端應重新讀取後再試。
    /// </summary>
    /// <returns>成功更新為 true；發生版本衝突為 false。</returns>
    Task<bool> TryUpdateWithVersionAsync(Product entity);

    // --- Stage 3 Version C：Atomic Update ---

    /// <summary>
    /// 在資料庫端直接做減法：
    /// <c>UPDATE Products SET Stock = Stock - @quantity WHERE Id = @id AND Stock >= @quantity</c>
    ///
    /// 檢查與扣減是同一個語句，由資料庫保證原子性，
    /// 應用程式完全不需要先讀取庫存。
    /// </summary>
    /// <returns>影響列數。1 = 扣減成功；0 = 庫存不足或商品不存在。</returns>
    Task<int> TryDeductStockAsync(int productId, int quantity);

    /// <summary>
    /// 把先前扣掉的庫存加回去（補償用）：
    /// <c>UPDATE Products SET Stock = Stock + @quantity WHERE Id = @id</c>
    ///
    /// 沒有條件判斷 —— 呼叫端已經確認扣減成功過，這裡只是把它還原。
    /// Stage 5 在「庫存已扣減但訊息發布失敗」時使用。
    /// </summary>
    Task RestoreStockAsync(int productId, int quantity);

    // --- Stage 1 Baseline 對照組 ---

    /// <summary>
    /// 不做版本檢查、直接覆寫庫存：
    /// <c>UPDATE Products SET Stock = @stock WHERE Id = @id</c>
    ///
    /// 這是 Stage 1 Baseline 行為的忠實重現。加入 rowversion 之後，
    /// 經由變更追蹤的更新都會自動帶版本檢查，Baseline 就不再會 Lost Update；
    /// 為了保留可比較的對照組，這裡刻意繞過檢查。
    ///
    /// **只允許 BaselineFlashSalePurchaseStrategy 使用。**
    /// </summary>
    Task OverwriteStockWithoutVersionCheckAsync(int productId, int stock);
}
