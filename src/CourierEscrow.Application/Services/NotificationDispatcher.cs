using CourierEscrow.Application.Interfaces;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Application.Services;

public sealed class NotificationDispatcher(INotificationStore store, ISmsSender sender, IProtector protector, IUnitOfWork unitOfWork, TimeProvider clock, ISmsCapture capture)
{
    public async Task<int> DispatchDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var sent = 0;
        foreach (var n in await store.ListDueAsync(now, 25, ct))
        {
            try
            {
                var body = n.Sensitive ? protector.Unprotect(n.Body) : n.Body;
                n.MarkSent(await sender.SendAsync(n.ToPhone, body, ct), now);
                capture.Record(n.EscrowId, n.ToPhone, n.Template, body, now);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                n.MarkFailed(ex.Message, now);
            }
            await unitOfWork.CommitAsync(ct);
        }
        return sent;
    }
}
