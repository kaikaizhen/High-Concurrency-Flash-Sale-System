namespace FlashSale.Api.Options;

public class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>
    /// StackExchange.Redis 連線字串，例如 <c>host:6379</c>。
    /// </summary>
    public string Configuration { get; set; } = string.Empty;

    /// <summary>
    /// Redis 6 ACL 使用者。未啟用 ACL 時留空。
    /// </summary>
    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// 所有 Key 的前綴，避免與共用同一台 Redis 的其他應用程式撞名。
    /// </summary>
    public string InstanceName { get; set; } = "flashsale:";

    public int ConnectTimeoutMs { get; set; } = 5000;
}
