using System.ComponentModel.DataAnnotations;

namespace FlashSale.Api.Models.Params.Products;

public class CreateProductParamModel
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Range(0, double.MaxValue)]
    public decimal Price { get; set; }

    [Range(0, int.MaxValue)]
    public int Stock { get; set; }
}
