using CourierEscrow.Application.Interfaces;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Application.Services.Support;

/// <summary>Stages an aggregate's outbox events and order events, then commits everything in one transaction.</summary>
public sealed class EscrowCommit(IOutbox outbox, IOrderEventStore orderEvents, IUnitOfWork unitOfWork)
{
    public async Task SaveAsync(CancellationToken ct, params Escrow[] escrows)
    {
        foreach (var e in escrows)
        {
            foreach (var d in e.PendingEvents) outbox.Add(d);
            foreach (var oe in e.PendingOrderEvents) orderEvents.Add(oe);
            e.ClearEvents();
        }
        await unitOfWork.CommitAsync(ct);
    }
}

/// <summary>Queues SMS inside the caller's unit of work. Sensitive bodies (OTP) are encrypted until they are sent.</summary>
public sealed class Notifier(INotificationStore store, IProtector protector, TimeProvider clock)
{
    public void Queue(Escrow escrow, string phone, string template, string body, bool sensitive = false) =>
        store.Add(Notification.Create(escrow.Id, phone, template, sensitive ? protector.Protect(body) : body, sensitive, clock.GetUtcNow()));
}

public static class Actors
{
    public const string System = "system";
    public static string Courier(Guid id) => $"courier:{id}";
    public static string Staff(string name) => $"staff:{name}";
}
