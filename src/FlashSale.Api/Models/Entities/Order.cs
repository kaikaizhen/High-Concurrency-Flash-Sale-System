using FlashSale.Api.Common.Enums;

namespace FlashSale.Api.Models.Entities;

public class Order
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public OrderStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
}
