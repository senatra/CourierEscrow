namespace CourierEscrow.Application.Interfaces;

public sealed record CapturedSms(DateTimeOffset At, Guid? EscrowId, string ToPhone, string Template, string Body);

public interface ISmsCapture
{
    void Record(Guid? escrowId, string toPhone, string template, string body, DateTimeOffset at);
    IReadOnlyList<CapturedSms> Recent(int take = 100);
}

public sealed class NullSmsCapture : ISmsCapture
{
    public void Record(Guid? escrowId, string toPhone, string template, string body, DateTimeOffset at) { }
    public IReadOnlyList<CapturedSms> Recent(int take = 100) => [];
}

public interface ISmsSender
{
    Task<string?> SendAsync(string toPhone, string body, CancellationToken ct);
}
