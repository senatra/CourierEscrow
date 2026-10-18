using CourierEscrow.Domain;

namespace CourierEscrow.Application.Interfaces;

public interface IOutbox
{
    void Add(DomainEvent domainEvent);
}

public interface IUnitOfWork
{
    Task CommitAsync(CancellationToken ct);
}

public interface INumberSequence
{
    Task<long> NextAsync(CancellationToken ct);
}
