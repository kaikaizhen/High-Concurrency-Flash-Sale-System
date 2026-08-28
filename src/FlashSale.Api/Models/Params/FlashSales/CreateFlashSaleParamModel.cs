using System.ComponentModel.DataAnnotations;

namespace FlashSale.Api.Models.Params.FlashSales;

public class CreateFlashSaleParamModel
{
    [Range(1, int.MaxValue)]
    public int UserId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; } = 1;
}
