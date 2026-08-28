namespace FlashSale.Api.Models.Entities;

public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int Stock { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// SQL Server rowversion。每次該列被更新時由資料庫自動遞增。
    ///
    /// 設定為 EF Core 的 Concurrency Token（見 ProductConfiguration），
    /// 因此透過變更追蹤送出的 UPDATE 會自動帶上
    /// <c>WHERE RowVersion = @original</c>，這是 Version B 的基礎。
    /// </summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
