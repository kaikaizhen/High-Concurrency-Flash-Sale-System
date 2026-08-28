namespace FlashSale.Api.Models.Dtos.Products;

public class CreateProductDtoModel
{
    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int Stock { get; set; }
}
