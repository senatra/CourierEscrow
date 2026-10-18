namespace CourierEscrow.Web.Models;

public sealed record NewOrderForm
{
    public string BuyerName { get; set; } = "";
    public string BuyerPhone { get; set; } = "";
    public string? BuyerNetwork { get; set; }
    public string SellerName { get; set; } = "";
    public string SellerPhone { get; set; } = "";
    public string? SellerNetwork { get; set; }
    public Guid CourierId { get; set; }
    public string ItemDescription { get; set; } = "";
    public string ItemKwacha { get; set; } = "";
    public string DeliveryKwacha { get; set; } = "";
    public string? PickupAddress { get; set; }
    public string? DeliveryAddress { get; set; }
}
