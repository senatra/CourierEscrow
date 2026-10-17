using CourierEscrow.Domain;

namespace CourierEscrow.Application.Services;

/// <summary>
/// What the buyer's page needs after tapping "Yes, this is the item".
/// <see cref="PaymentReady"/> is false when the order is approved but the provider could not be reached yet:
/// nothing is lost, and approving again simply retries the payment request with the same reference.
/// </summary>
public sealed record ApprovalResult(
    bool AlreadyConfirmed, string State, bool PaymentReady, string? Reference, string? AccountNumber,
    DateTimeOffset? ExpiresAt, string? Message);

/// <summary>
/// The buyer approves the item photos, and only then is the payment requested (never before).
/// Approval is committed first, on its own, so a provider outage cannot undo it; the payment request is then started or
/// safely repeated. Calling this again for an approved order is how a stuck payment is resumed.
/// </summary>
public sealed class BuyerApprovalService(OrderService orders, CollectionService collections)
{
    public async Task<ApprovalResult> ApproveAsync(string token, CancellationToken ct = default)
    {
        var result = await orders.ConfirmProductAsync(token, ct);
        var escrow = result.Escrow;

        // Created: payment not started yet. PaymentPending: started earlier but maybe never reached the provider.
        // StartAsync handles both and reuses the same idempotency reference, so repeating it is harmless.
        if (escrow.State is not (EscrowState.Created or EscrowState.PaymentPending))
            return new(result.AlreadyConfirmed, escrow.State.ToString(), false, escrow.PaymentReference, null, null, null);

        try
        {
            var payment = await collections.StartAsync(escrow.Id, "buyer", ct);
            return new(result.AlreadyConfirmed, payment.State, true, payment.Reference, payment.AccountNumber, payment.ExpiresAt, null);
        }
        catch (ProviderException ex)
        {
            return new(result.AlreadyConfirmed, EscrowState.PaymentPending.ToString(), false, escrow.PaymentReference, null, null,
                ex.Transient
                    ? "The payment provider is not answering right now. Your approval is saved. Tap the button to try again."
                    : "The payment provider could not set up your payment. Our team has been told. You can try again shortly.");
        }
    }
}
