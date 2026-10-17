using System.Security.Cryptography;

namespace CourierEscrow.Domain;

public enum StaffStatus { Active = 0, Disabled = 1 }

/// <summary>
/// A person who signs in to the ops console with a username and password. Couriers have their own account type
/// (<see cref="CourierAccount"/>) and buyers and sellers have none.
/// </summary>
public sealed class StaffUser
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private StaffUser() { Username = ""; DisplayName = ""; PasswordHash = ""; Role = ""; SecurityStamp = ""; }

    public Guid Id { get; private set; }

    /// <summary>Lower case, unique. Used to sign in and written to the audit trail as "staff:{username}".</summary>
    public string Username { get; private set; }
    public string DisplayName { get; private set; }
    public string PasswordHash { get; private set; }

    /// <summary>One of the staff roles in <c>Roles</c> (ops_agent, ops_admin, finance). Validated by the user service.</summary>
    public string Role { get; private set; }
    public StaffStatus Status { get; private set; }

    /// <summary>True for a new or reset account: the person must choose their own password before doing anything else.</summary>
    public bool MustChangePassword { get; private set; }
    public int FailedLogins { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>
    /// Changes whenever the password, role or status changes. Access tokens carry the stamp they were issued under,
    /// so every older token stops working at once.
    /// </summary>
    public string SecurityStamp { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset PasswordChangedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }

    public static string NormalizeUsername(string? raw)
    {
        var name = (raw ?? "").Trim().ToLowerInvariant();
        if (name.Length is < 3 or > 60 || !name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' or '@'))
            throw new DomainValidationException("Username must be 3 to 60 characters: letters, digits, dot, dash, underscore or @.");
        return name;
    }

    public static StaffUser Create(string username, string displayName, string passwordHash, string role, bool mustChangePassword, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new DomainValidationException("Name is required.");
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new DomainValidationException("Password is required.");
        return new StaffUser
        {
            Id = Guid.NewGuid(), Username = NormalizeUsername(username), DisplayName = displayName.Trim(), PasswordHash = passwordHash,
            Role = role, Status = StaffStatus.Active, MustChangePassword = mustChangePassword, SecurityStamp = NewStamp(),
            CreatedAt = now, PasswordChangedAt = now
        };
    }

    public bool IsActive => Status == StaffStatus.Active;
    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>Counts a wrong password. Returns true when this one locked the account.</summary>
    public bool RegisterFailedLogin(DateTimeOffset now)
    {
        FailedLogins++;
        if (FailedLogins < MaxFailedLogins) return false;
        LockedUntil = now + LockDuration;
        FailedLogins = 0;
        return true;
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now) { FailedLogins = 0; LockedUntil = null; LastLoginAt = now; }
    public void Unlock() { FailedLogins = 0; LockedUntil = null; }

    public void SetPassword(string passwordHash, bool mustChange, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new DomainValidationException("Password is required.");
        PasswordHash = passwordHash;
        MustChangePassword = mustChange;
        PasswordChangedAt = now;
        Unlock();
        RotateStamp();
    }

    public void ChangeRole(string role) { if (Role != role) { Role = role; RotateStamp(); } }

    public void Rename(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new DomainValidationException("Name is required.");
        DisplayName = displayName.Trim();
    }

    public void Disable() { Status = StaffStatus.Disabled; RotateStamp(); }
    public void Enable() { Status = StaffStatus.Active; Unlock(); RotateStamp(); }

    private void RotateStamp() => SecurityStamp = NewStamp();
    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
