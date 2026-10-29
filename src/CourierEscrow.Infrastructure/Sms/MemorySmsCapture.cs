using System.Collections.Concurrent;
using CourierEscrow.Application.Interfaces;

namespace CourierEscrow.Infrastructure.Sms;

public sealed class MemorySmsCapture : ISmsCapture
{
    private const int Cap = 200;
    private readonly ConcurrentQueue<CapturedSms> _items = new();

    public void Record(Guid? escrowId, string toPhone, string template, string body, DateTimeOffset at)
    {
        _items.Enqueue(new CapturedSms(at, escrowId, toPhone, template, body));
        while (_items.Count > Cap && _items.TryDequeue(out _)) { }
    }

    public IReadOnlyList<CapturedSms> Recent(int take = 100) =>
        _items.Reverse().Take(Math.Clamp(take, 1, Cap)).ToList();
}
