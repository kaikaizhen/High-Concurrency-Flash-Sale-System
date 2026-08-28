namespace FlashSale.Api.Common.Constants;

/// <summary>
/// 快取 Key 統一在此組出，避免各處字串散落造成寫入與清除用了不同的 Key。
/// 實際送到 Redis 時還會再加上 <c>RedisOptions.InstanceName</c> 前綴。
/// </summary>
public static class CacheKeys
{
    public static string Product(int productId)
    {
        return $"product:{productId}";
    }
}
