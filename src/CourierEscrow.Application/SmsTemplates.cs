using CourierEscrow.Domain;

namespace CourierEscrow.Application;

/// <summary>Every SMS the system sends. Wording follows the product's message designs; plain text, no app needed.</summary>
public static class SmsTemplates
{
    private static string K(long minor, Escrow e) => Money.Format(minor, e.Currency);

    public static string OrderCreatedBuyer(Escrow e, string sellerName, string trackUrl) =>
        $"Order {e.OrderNumber}: {e.ItemDescription} from {sellerName}, {K(e.ItemAmount, e)} + {K(e.DeliveryFee, e)} delivery. Open this link, check the 3 photos, and confirm it is the item you are buying: {trackUrl}";

    public static string OrderCreatedSeller(Escrow e) =>
        $"Order {e.OrderNumber}: {e.ItemDescription}. The buyer will pay {K(e.Total, e)} into escrow. Wait for the payment message before handing over the item.";

    public static string PaymentInstructions(Escrow e, string? accountNumber, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((expiresAt - now).TotalMinutes));
        return accountNumber is null
            ? $"Approve {K(e.Total, e)} to Nexa Escrow (ref {e.OrderNumber})? Enter PIN to confirm."
            : $"Pay {K(e.Total, e)} for order {e.OrderNumber} by mobile money or bank to account {accountNumber} within {minutes} minutes. Pay the exact amount.";
    }

    public static string FundedSeller(Escrow e, string courierName) =>
        $"{e.OrderNumber}: buyer has paid {K(e.Total, e)} into escrow. Hand the sealed item to courier {courierName}. Do not release without the courier.";

    public static string FundedCourier(Escrow e, string sellerName) =>
        $"{e.OrderNumber} is funded. Pick up {e.ItemDescription} from {sellerName}.";

    public static string PickedUpBuyer(Escrow e, string code, string trackUrl) =>
        $"Your money is held safely. Courier has collected your item. Give this code ONLY after you inspect and accept it: {code[..3]} {code[3..]}. Track: {trackUrl} valid 24h";

    public static string DispatchProofSeller(Escrow e, DateTimeOffset at, int photos) =>
        $"Picked up {at:HH:mm}. Seal {e.SealId}, {(e.WeightGrams ?? 0) / 1000m:0.0##} kg, {photos} photos saved. Keep this message as your proof.";

    public static string ReleasedSeller(Escrow e, Settlement s) =>
        $"Buyer accepted. {K(s.Seller, e)} is on its way to your mobile money ({K(e.ItemAmount, e)} less {e.FeeBps / 100m:0.##}% fee {K(s.Platform, e)}).";

    public static string ReleasedCourier(Escrow e, Settlement s) =>
        $"{e.OrderNumber} delivered. {K(s.Courier, e)} is on its way to your wallet.";

    public static string RejectedSeller(Escrow e, string reason) =>
        $"{e.OrderNumber} was rejected at the door (reason: {reason}). Funds are held. Courier will return the item. Please confirm receipt.";

    public static string RejectedBuyer(Escrow e) =>
        $"{e.OrderNumber}: rejection recorded. Funds stay frozen until we decide.";

    public static string DisputeOpened(Escrow e) =>
        $"{e.OrderNumber}: a dispute was opened. Funds stay frozen until we decide, within 48 hours.";

    public static string DisputeDecided(Escrow e, string outcome) =>
        $"{e.OrderNumber}: dispute decided ({outcome}). Payments are on their way.";

    public static string RefundBuyer(Escrow e, Settlement s) =>
        $"{e.OrderNumber}: {K(s.BuyerRefund, e)} is on its way back to your mobile money.";

    public static string CourierPaidOnRefund(Escrow e, Settlement s) =>
        $"{e.OrderNumber}: {K(s.Courier, e)} delivery fee is on its way to your wallet.";

    public static string PaymentExpired(Escrow e) =>
        $"{e.OrderNumber}: your payment request expired. No money was taken. The order was cancelled.";

    public static string PaymentFailed(Escrow e) =>
        $"{e.OrderNumber}: the payment did not go through. No money was taken. The order was cancelled.";

    public static string WrongAmount(Escrow e, long received) =>
        $"{e.OrderNumber}: wrong amount received ({K(received, e)} not {K(e.Total, e)}). The order stays unfunded and ops will contact you.";

    public static string Cancelled(Escrow e) =>
        $"{e.OrderNumber} was cancelled. Any held money returns to the buyer.";

    public static string OtpReissued(Escrow e, string code, string trackUrl) =>
        $"New delivery code for {e.OrderNumber}: {code[..3]} {code[3..]}. Give it ONLY after you inspect and accept the item. Track: {trackUrl}";
}
