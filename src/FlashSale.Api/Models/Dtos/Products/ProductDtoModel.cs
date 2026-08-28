namespace FlashSale.Api.Models.Dtos.Products;

public class ProductDtoModel
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int Stock { get; set; }

    public DateTime CreatedAt { get; set; }
}
