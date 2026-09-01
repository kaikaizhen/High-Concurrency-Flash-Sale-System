using FlashSale.Api.Models.Entities;

namespace FlashSale.Api.Repositories.Interfaces;

public interface IOrderRepository
{
    Task<List<Order>> GetListByProductIdAsync(int productId);

    Task<Order?> GetByIdAsync(int id);

    Task<int> CountByProductIdAsync(int productId);

    Task CreateAsync(Order entity);

    /// <summary>
    /// 建立訂單，若 <see cref="Order.IdempotencyKey"/> 已存在則放棄。
    ///
    /// 判斷完全交給資料庫的篩選唯一索引 —— 「先查詢再新增」在併發下
    /// 會讓兩個請求同時通過查詢，冪等保證就失效了。
    /// </summary>
    /// <returns>true = 已建立；false = 該識別碼的訂單已存在。</returns>
    Task<bool> TryCreateAsync(Order entity);
}
