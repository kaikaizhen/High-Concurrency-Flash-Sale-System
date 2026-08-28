namespace FlashSale.Api.Models.Dtos.Orders;

public class CreateOrderDtoModel
{
    public int UserId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }
}
