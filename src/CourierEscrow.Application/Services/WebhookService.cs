using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Application.Services;

public enum WebhookReceipt { Unauthorized, Ignored, Duplicate, Stored }

public sealed class WebhookService(
    IPaymentProvider provider, IWebhookInbox inbox, CollectionService collections, PayoutService payouts,
    IUnitOfWork unitOfWork, TimeProvider clock, IPaymentProviderResolver? resolver = null)
{
    public async Task<(WebhookReceipt Receipt, Guid? ItemId)> ReceiveAsync(byte[] raw, string? signature, string? eventCode, CancellationToken ct = default, IPaymentProvider? source = null)
    {
        var callbackProvider = source ?? provider;
        if (!await callbackProvider.VerifyWebhookAsync(raw, eventCode, signature, ct)) return (WebhookReceipt.Unauthorized, null);

        var body = System.Text.Encoding.UTF8.GetString(raw);
        var notification = callbackProvider.ParseWebhook(eventCode, body);
        if (notification is null) return (WebhookReceipt.Ignored, null);
        if (await inbox.ExistsAsync(notification.EventKey, ct)) return (WebhookReceipt.Duplicate, null);

        var item = WebhookInboxItem.Create(callbackProvider.Name, notification.EventKey, eventCode ?? "", body, clock.GetUtcNow());
        inbox.Add(item);
        try { await unitOfWork.CommitAsync(ct); }
        catch (DuplicateOperationException) { return (WebhookReceipt.Duplicate, null); }
        return (WebhookReceipt.Stored, item.Id);
    }

    public async Task<string> ProcessAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await inbox.GetAsync(itemId, ct) ?? throw new NotFoundException("Inbox item not found.");
        if (item.Status == WebhookStatus.Processed) return item.Outcome ?? "Processed";

        var callbackProvider = resolver?.GetProvider(item.Provider) ?? provider;
        var notification = callbackProvider.ParseWebhook(item.EventCode, item.RawPayload);
        if (notification is null)
        {
            item.MarkProcessed("Ignored", clock.GetUtcNow());
            await unitOfWork.CommitAsync(ct);
            return "Ignored";
        }

        var outcome = notification switch
        {
            CollectionNotification c => await collections.HandleAsync(c, item, ct),
            PayoutNotification p => await payouts.ApplyAsync(p, item, ct),
            _ => CallbackOutcome.Ignored
        };
        return outcome.ToString();
    }

    public async Task RecordFailureAsync(Guid itemId, string error, CancellationToken ct = default)
    {
        var item = await inbox.GetAsync(itemId, ct);
        if (item is null) return;
        item.MarkFailed(error);
        await unitOfWork.CommitAsync(ct);
    }

    public Task<IReadOnlyList<WebhookInboxItem>> ListPendingAsync(int take, CancellationToken ct = default) => inbox.ListPendingAsync(take, ct);
}
