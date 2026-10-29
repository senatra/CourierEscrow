using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;

namespace CourierEscrow.Application.Services;

public sealed record StaffSession(string Token, DateTimeOffset ExpiresAt, StaffUser User);
public sealed record BootstrapResult(StaffUser User, string? GeneratedPassword);

/// <summary>
/// Staff accounts for the ops console: sign-in with lockout, creating and disabling people, roles and password changes.
/// Every change is written to the audit trail in the same transaction.
/// </summary>
public sealed class UserService(
    IStaffUserRepository users, IAuditLog audit, IUnitOfWork unitOfWork, AccessTokens tokens, AuthOptions auth, TimeProvider clock)
{
    private const string WrongCredentials = "Wrong username or password.";

    // Verified against when the username does not exist, so an unknown name takes as long as a wrong password.
    private static readonly string DummyHash = PasswordHasher.Hash("dummy-password-for-timing-1");

    // Sign in

    public async Task<StaffSession> LoginAsync(string? username, string? password, CancellationToken ct = default)
    {
        string normalized;
        try { normalized = StaffUser.NormalizeUsername(username); }
        catch (DomainValidationException) { PasswordHasher.Verify(password, DummyHash); throw new AuthenticationFailedException(WrongCredentials); }

        var now = clock.GetUtcNow();
        var user = await users.FindByUsernameAsync(normalized, ct);
        if (user is null) { PasswordHasher.Verify(password, DummyHash); throw new AuthenticationFailedException(WrongCredentials); }

        if (user.IsLocked(now)) throw new TooManyAttemptsException("Too many wrong passwords. Try again in a few minutes, or ask an administrator to unlock the account.");

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            if (user.RegisterFailedLogin(now))
                audit.Add(AuditEntry.Create(Actors.Staff(user.Username), "user.locked", "staff_user", user.Id.ToString(), "Too many wrong passwords", now));
            await unitOfWork.CommitAsync(ct);
            throw new AuthenticationFailedException(WrongCredentials);
        }

        // Only someone who knows the password is told the account is disabled.
        if (!user.IsActive) throw new AuthenticationFailedException("This account is disabled. Contact an administrator.");

        user.RegisterSuccessfulLogin(now);
        audit.Add(AuditEntry.Create(Actors.Staff(user.Username), "user.login", "staff_user", user.Id.ToString(), null, now));
        await unitOfWork.CommitAsync(ct);
        return Session(user);
    }

    private StaffSession Session(StaffUser user)
    {
        var (token, expires) = tokens.Issue(user.Id.ToString(), user.Role, user.Username, user.SecurityStamp, auth.StaffTokenLifetimeHours);
        return new StaffSession(token, expires, user);
    }

    // First administrator

    /// <summary>
    /// Creates the first administrator when no staff user exists. The password comes from <c>Auth:BootstrapAdmin:Password</c>, or is
    /// generated and returned so the caller can show it once. Either way the person must change it at first sign-in.
    /// Returns null when there is nothing to do.
    /// </summary>
    public async Task<BootstrapResult?> EnsureBootstrapAdminAsync(CancellationToken ct = default)
    {
        if (await users.AnyAsync(ct)) return null;

        var options = auth.BootstrapAdmin;
        var username = StaffUser.NormalizeUsername(string.IsNullOrWhiteSpace(options.Username) ? "admin" : options.Username);
        var configured = !string.IsNullOrWhiteSpace(options.Password);
        var password = configured ? options.Password! : PasswordHasher.Generate(username);
        PasswordHasher.Validate(password, username);

        var now = clock.GetUtcNow();
        var user = StaffUser.Create(username, string.IsNullOrWhiteSpace(options.DisplayName) ? "Administrator" : options.DisplayName,
            PasswordHasher.Hash(password), Roles.OpsAdmin, mustChangePassword: true, now);
        users.Add(user);
        audit.Add(AuditEntry.Create(Actors.System, "user.bootstrap", "staff_user", user.Id.ToString(), user.Username, now));
        await unitOfWork.CommitAsync(ct);
        return new BootstrapResult(user, configured ? null : password);
    }

    // Managing people (administrators)

    public async Task<StaffUser> CreateAsync(string username, string displayName, string role, string password, string actor, CancellationToken ct = default)
    {
        RequireStaffRole(role);
        var normalized = StaffUser.NormalizeUsername(username);
        PasswordHasher.Validate(password, normalized);
        if (await users.FindByUsernameAsync(normalized, ct) is not null)
            throw new DomainValidationException("That username is already taken.");

        var now = clock.GetUtcNow();
        var user = StaffUser.Create(normalized, displayName, PasswordHasher.Hash(password), role, mustChangePassword: true, now);
        users.Add(user);
        audit.Add(AuditEntry.Create(actor, "user.create", "staff_user", user.Id.ToString(), $"{user.Username} as {role}", now));
        await unitOfWork.CommitAsync(ct);
        return user;
    }

    public async Task<StaffUser> RenameAsync(Guid id, string displayName, string actor, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        user.Rename(displayName);
        audit.Add(AuditEntry.Create(actor, "user.rename", "staff_user", id.ToString(), user.DisplayName, clock.GetUtcNow()));
        await unitOfWork.CommitAsync(ct);
        return user;
    }

    public async Task<StaffUser> ChangeRoleAsync(Guid id, string role, Guid? actorId, string actor, CancellationToken ct = default)
    {
        RequireStaffRole(role);
        var user = await GetRequiredAsync(id, ct);
        if (user.Role == role) return user;
        if (id == actorId) throw new DomainValidationException("You cannot change your own role. Ask another administrator.");
        await EnsureAnotherAdminAsync(user, ct);

        var from = user.Role;
        user.ChangeRole(role);
        audit.Add(AuditEntry.Create(actor, "user.role", "staff_user", id.ToString(), $"{user.Username}: {from} to {role}", clock.GetUtcNow()));
        await unitOfWork.CommitAsync(ct);
        return user;
    }

    public async Task<StaffUser> SetActiveAsync(Guid id, bool active, Guid? actorId, string actor, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        if (user.IsActive == active) return user;
        if (!active)
        {
            if (id == actorId) throw new DomainValidationException("You cannot disable your own account.");
            await EnsureAnotherAdminAsync(user, ct);
            user.Disable();
        }
        else user.Enable();

        audit.Add(AuditEntry.Create(actor, active ? "user.enable" : "user.disable", "staff_user", id.ToString(), user.Username, clock.GetUtcNow()));
        await unitOfWork.CommitAsync(ct);
        return user;
    }

    /// <summary>An administrator sets a temporary password for someone who is locked out. They must choose their own at next sign-in.</summary>
    public async Task<StaffUser> ResetPasswordAsync(Guid id, string newPassword, Guid? actorId, string actor, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        if (id == actorId) throw new DomainValidationException("Use Change password for your own account.");
        PasswordHasher.Validate(newPassword, user.Username);

        user.SetPassword(PasswordHasher.Hash(newPassword), mustChange: true, clock.GetUtcNow());
        audit.Add(AuditEntry.Create(actor, "user.reset_password", "staff_user", id.ToString(), user.Username, clock.GetUtcNow()));
        await unitOfWork.CommitAsync(ct);
        return user;
    }

    public async Task<StaffUser> UnlockAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        user.Unlock();
        audit.Add(AuditEntry.Create(actor, "user.unlock", "staff_user", id.ToString(), user.Username, clock.GetUtcNow()));
        await unitOfWork.CommitAsync(ct);
        return user;
    }

    // Your own account

    /// <summary>Changes the caller's password and returns a fresh session; every older token is revoked.</summary>
    public async Task<StaffSession> ChangePasswordAsync(Guid id, string? currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await GetRequiredAsync(id, ct);
        var now = clock.GetUtcNow();

        if (user.IsLocked(now)) throw new TooManyAttemptsException("Too many wrong passwords. Try again in a few minutes.");
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            if (user.RegisterFailedLogin(now))
                audit.Add(AuditEntry.Create(Actors.Staff(user.Username), "user.locked", "staff_user", id.ToString(), "Too many wrong passwords", now));
            await unitOfWork.CommitAsync(ct);
            throw new AuthenticationFailedException("The current password is wrong.");
        }

        PasswordHasher.Validate(newPassword, user.Username);
        if (PasswordHasher.Verify(newPassword, user.PasswordHash)) throw new DomainValidationException("Choose a password you have not used just now.");

        user.SetPassword(PasswordHasher.Hash(newPassword), mustChange: false, now);
        audit.Add(AuditEntry.Create(Actors.Staff(user.Username), "user.change_password", "staff_user", id.ToString(), null, now));
        await unitOfWork.CommitAsync(ct);
        return Session(user);
    }

    // Queries

    public Task<IReadOnlyList<StaffUser>> ListAsync(CancellationToken ct = default) => users.ListAsync(ct);

    public async Task<StaffUser> GetAsync(Guid id, CancellationToken ct = default) => await GetRequiredAsync(id, ct);

    // Rules

    private async Task<StaffUser> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await users.GetAsync(id, ct) ?? throw new NotFoundException("User not found.");

    private static void RequireStaffRole(string? role)
    {
        if (!Roles.IsStaff(role)) throw new DomainValidationException($"Role must be one of: {string.Join(", ", Roles.Staff)}.");
    }

    /// <summary>The system must never be left without an active administrator.</summary>
    private async Task EnsureAnotherAdminAsync(StaffUser user, CancellationToken ct)
    {
        if (user.Role == Roles.OpsAdmin && user.IsActive && await users.CountActiveAdminsAsync(ct) <= 1)
            throw new DomainValidationException("There must be at least one active administrator.");
    }
}
