namespace CourierEscrow.Domain;

public enum PartyRole { Buyer = 0, Seller = 1 }
public enum MobileNetwork { Airtel = 0, Tnm = 1 }

/// <summary>A buyer or seller. They have no app and no login; they are reached by SMS and paid by mobile money.</summary>
public sealed class Party
{
    private Party() { Name = ""; Phone = ""; }

    public Guid Id { get; private set; }
    public PartyRole Role { get; private set; }
    public string Name { get; private set; }

    /// <summary>Digits only, 265XXXXXXXXX. Also the wallet number payouts go to.</summary>
    public string Phone { get; private set; }
    public string? WalletNetwork { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Party Create(PartyRole role, string name, string phone, string? network, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Name is required.");
        return new Party { Id = Guid.NewGuid(), Role = role, Name = name.Trim(), Phone = PhoneNumber.Normalize(phone), WalletNetwork = network, CreatedAt = now };
    }

    public void UpdateWallet(string? network)
    {
        if (network is not null) WalletNetwork = network;
    }
}

public enum CourierStatus { Active = 0, Suspended = 1 }

/// <summary>A courier who signs in to the web app with phone and PIN, bound to one device.</summary>
public sealed class CourierAccount
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private CourierAccount() { Name = ""; Phone = ""; PinHash = ""; }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string Phone { get; private set; }
    public string WalletNetwork { get; private set; }
    public string PinHash { get; private set; }
    public string? DeviceId { get; private set; }
    public CourierStatus Status { get; private set; }
    public int FailedLogins { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static CourierAccount Create(string name, string phone, string network, string pinHash, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Name is required.");
        if (string.IsNullOrWhiteSpace(pinHash)) throw new DomainValidationException("PIN is required.");
        return new CourierAccount { Id = Guid.NewGuid(), Name = name.Trim(), Phone = PhoneNumber.Normalize(phone), WalletNetwork = network, PinHash = pinHash, CreatedAt = now };
    }

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void RegisterFailedLogin(DateTimeOffset now)
    {
        FailedLogins++;
        if (FailedLogins >= MaxFailedLogins) { LockedUntil = now + LockDuration; FailedLogins = 0; }
    }

    public void RegisterSuccessfulLogin() { FailedLogins = 0; LockedUntil = null; }
    public bool DeviceAllowed(string deviceId) => DeviceId is null || DeviceId == deviceId;
    public void BindDevice(string deviceId) => DeviceId ??= deviceId;
    public void ResetDevice() => DeviceId = null;
    public void Suspend() => Status = CourierStatus.Suspended;
    public void Activate() => Status = CourierStatus.Active;
    public void ChangePin(string pinHash) => PinHash = pinHash;
}
