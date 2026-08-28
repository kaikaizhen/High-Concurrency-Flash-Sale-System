using System.ComponentModel.DataAnnotations;

namespace FlashSale.Api.Models.Params.Orders;

public class CreateOrderParamModel
{
    [Range(1, int.MaxValue)]
    public int UserId { get; set; }

    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; } = 1;
}
