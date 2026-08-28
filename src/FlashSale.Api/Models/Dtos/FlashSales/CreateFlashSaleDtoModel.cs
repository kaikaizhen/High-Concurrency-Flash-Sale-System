namespace FlashSale.Api.Models.Dtos.FlashSales;

public class CreateFlashSaleDtoModel
{
    public int ProductId { get; set; }

    public int UserId { get; set; }

    public int Quantity { get; set; }
}
