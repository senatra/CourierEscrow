using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Application.Services;

public sealed record LoginResult(string Token, Guid CourierId, string Name, DateTimeOffset ExpiresAt);
public sealed record CourierJob(Escrow Escrow, Party Seller, Party Buyer);

/// <summary>Courier accounts, phone + PIN sign-in with device binding and lockout, and the courier's job list.</summary>
public sealed class CourierService(
    ICourierRepository couriers, IEscrowRepository escrows, IPartyRepository parties, IAuditLog audit,
    IUnitOfWork unitOfWork, AccessTokens tokens, TimeProvider clock)
{
    public async Task<LoginResult> LoginAsync(string phone, string pin, string deviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new DomainValidationException("A device id is required.");
        string normalized;
        try { normalized = PhoneNumber.Normalize(phone); }
        catch (DomainValidationException) { throw new AuthenticationFailedException("Wrong phone or PIN."); }

        var now = clock.GetUtcNow();
        var courier = await couriers.FindByPhoneAsync(normalized, ct) ?? throw new AuthenticationFailedException("Wrong phone or PIN.");
        if (courier.Status != CourierStatus.Active) throw new AuthenticationFailedException("This account is suspended. Contact your dispatcher.");
        if (courier.IsLocked(now)) throw new TooManyAttemptsException("Too many wrong PINs. Try again in a few minutes.");

        if (!PinHasher.Verify(pin, courier.PinHash))
        {
            courier.RegisterFailedLogin(now);
            await unitOfWork.CommitAsync(ct);
            throw new AuthenticationFailedException("Wrong phone or PIN.");
        }

        if (!courier.DeviceAllowed(deviceId))
            throw new ForbiddenException("This phone is not approved. Ask ops to approve the new device.");

        courier.BindDevice(deviceId);
        courier.RegisterSuccessfulLogin();
        await unitOfWork.CommitAsync(ct);

        var (token, expires) = tokens.Issue(courier.Id.ToString(), Roles.Courier, courier.Name);
        return new LoginResult(token, courier.Id, courier.Name, expires);
    }

    public async Task<CourierAccount> CreateAsync(string name, string phone, string network, string pin, string actor, CancellationToken ct = default)
    {
        var normalized = PhoneNumber.Normalize(phone);
        if (await couriers.FindByPhoneAsync(normalized, ct) is not null)
            throw new DomainValidationException("A courier with this phone number already exists.");

        var courier = CourierAccount.Create(name, normalized, network, PinHasher.Hash(pin), clock.GetUtcNow());
        couriers.Add(courier);
        audit.Add(AuditEntry.Create(actor, "courier.create", "courier", courier.Id.ToString(), courier.Name, clock.GetUtcNow()));
        await unitOfWork.CommitAsync(ct);
        return courier;
    }

    public async Task<CourierAccount> ResetDeviceAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var courier = await couriers.GetAsync(id, ct) ?? throw new NotFoundException("Courier not found.");
        courier.ResetDevice();
        audit.Add(AuditEntry.Create(actor, "courier.reset_device", "courier", id.ToString(), null, clock.GetUtcNow()));
        await unitOfWork.CommitAsync(ct);
        return courier;
    }

    public async Task<CourierAccount> SetStatusAsync(Guid id, bool active, string actor, CancellationToken ct = default)
    {
        var courier = await couriers.GetAsync(id, ct) ?? throw new NotFoundException("Courier not found.");
        if (active) courier.Activate(); else courier.Suspend();
        audit.Add(AuditEntry.Create(actor, active ? "courier.activate" : "courier.suspend", "courier", id.ToString(), null, clock.GetUtcNow()));
        await unitOfWork.CommitAsync(ct);
        return courier;
    }

    public Task<IReadOnlyList<CourierAccount>> ListAsync(CancellationToken ct = default) => couriers.ListAsync(ct);

    public async Task<IReadOnlyList<CourierJob>> GetJobsAsync(Guid courierId, CancellationToken ct = default)
    {
        var list = await escrows.ListForCourierAsync(courierId, ct);
        var ids = list.SelectMany(e => new[] { e.BuyerId, e.SellerId }).Distinct().ToList();
        var people = (await parties.GetManyAsync(ids, ct)).ToDictionary(p => p.Id);
        return list.Select(e => new CourierJob(e, people[e.SellerId], people[e.BuyerId])).ToList();
    }

    public async Task<CourierJob> GetJobAsync(Guid courierId, Guid escrowId, CancellationToken ct = default)
    {
        var e = await escrows.GetAsync(escrowId, ct);
        if (e is null || e.CourierId != courierId) throw new NotFoundException("Job not found.");
        var people = (await parties.GetManyAsync([e.BuyerId, e.SellerId], ct)).ToDictionary(p => p.Id);
        return new CourierJob(e, people[e.SellerId], people[e.BuyerId]);
    }
}
