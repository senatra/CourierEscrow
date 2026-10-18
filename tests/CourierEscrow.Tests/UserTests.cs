using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Services;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
using CourierEscrow.Tests.Fakes;

namespace CourierEscrow.Tests;

/// <summary>Staff accounts: first administrator, sign-in and lockout, roles, password changes and the audit trail.</summary>
public class UserTests
{
    private const string Strong = "Sunrise-2026-ok";

    private sealed class UserEnv
    {
        public MemoryDb Db { get; } = new();
        public MemoryRepos Repos { get; }
        public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
        public AuthOptions Auth { get; } = new();
        public AccessTokens Tokens { get; }
        public UserService Users { get; }

        public UserEnv()
        {
            Repos = new MemoryRepos(Db);
            Tokens = new AccessTokens(Auth, Clock);
            Users = new UserService(Repos, Repos, Db, Tokens, Auth, Clock);
        }

        /// <summary>An administrator who has already chosen their own password.</summary>
        public async Task<StaffUser> AdminAsync(string username = "alice")
        {
            var u = await Users.CreateAsync(username, "Alice Admin", Roles.OpsAdmin, "Temp-pass-123", "system");
            await Users.ChangePasswordAsync(u.Id, "Temp-pass-123", Strong);
            return u;
        }
    }

    // First administrator

    [Fact]
    public async Task Fresh_system_gets_an_admin_with_a_generated_one_time_password()
    {
        var env = new UserEnv();
        var result = await env.Users.EnsureBootstrapAdminAsync();

        Assert.NotNull(result);
        Assert.Equal("admin", result!.User.Username);
        Assert.Equal(Roles.OpsAdmin, result.User.Role);
        Assert.True(result.User.MustChangePassword);
        Assert.NotNull(result.GeneratedPassword);

        var session = await env.Users.LoginAsync("admin", result.GeneratedPassword);
        Assert.True(session.User.MustChangePassword);
    }

    [Fact]
    public async Task Bootstrap_uses_the_configured_password_and_does_not_hand_it_back()
    {
        var env = new UserEnv();
        env.Auth.BootstrapAdmin.Username = "Root";
        env.Auth.BootstrapAdmin.Password = "Configured-pass-7";

        var result = await env.Users.EnsureBootstrapAdminAsync();

        Assert.Equal("root", result!.User.Username);
        Assert.Null(result.GeneratedPassword);
        await env.Users.LoginAsync("root", "Configured-pass-7");
    }

    [Fact]
    public async Task The_development_bootstrap_defaults_are_accepted()
    {
        var env = new UserEnv();
        env.Auth.BootstrapAdmin.Username = "admin";
        env.Auth.BootstrapAdmin.Password = "Nexa-dev-2026";   // keep in step with appsettings.Development.json

        Assert.NotNull(await env.Users.EnsureBootstrapAdminAsync());
        await env.Users.LoginAsync("admin", "Nexa-dev-2026");
    }

    [Fact]
    public async Task Bootstrap_does_nothing_once_anyone_exists()
    {
        var env = new UserEnv();
        Assert.NotNull(await env.Users.EnsureBootstrapAdminAsync());
        Assert.Null(await env.Users.EnsureBootstrapAdminAsync());
        Assert.Single(env.Db.Staff.Rows);
    }

    [Fact]
    public async Task Bootstrap_refuses_a_weak_configured_password()
    {
        var env = new UserEnv();
        env.Auth.BootstrapAdmin.Password = "abc";
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.EnsureBootstrapAdminAsync());
        Assert.Empty(env.Db.Staff.Rows);
    }

    // Sign in

    [Fact]
    public async Task Login_issues_a_token_carrying_the_security_stamp()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();

        var session = await env.Users.LoginAsync("  Alice ", Strong);
        var principal = env.Tokens.Validate(session.Token);

        Assert.NotNull(principal);
        Assert.Equal(alice.Id.ToString(), principal!.Subject);
        Assert.Equal(Roles.OpsAdmin, principal.Role);
        Assert.Equal("alice", principal.Name);
        Assert.Equal(alice.SecurityStamp, principal.Stamp);
        Assert.NotNull(session.User.LastLoginAt);
        Assert.Equal(env.Clock.GetUtcNow().AddHours(env.Auth.StaffTokenLifetimeHours), session.ExpiresAt);
    }

    [Fact]
    public async Task Unknown_user_and_wrong_password_look_the_same()
    {
        var env = new UserEnv();
        await env.AdminAsync();

        var unknown = await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("nobody", Strong));
        var wrong = await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("alice", "not-the-password-1"));
        var junk = await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("a b!", Strong));

        Assert.Equal(unknown.Message, wrong.Message);
        Assert.Equal(unknown.Message, junk.Message);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_against_the_right_one()
    {
        var env = new UserEnv();
        await env.AdminAsync();

        for (var i = 0; i < StaffUser.MaxFailedLogins; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("alice", "wrong-password-1"));

        await Assert.ThrowsAsync<TooManyAttemptsException>(() => env.Users.LoginAsync("alice", Strong));
        Assert.Contains(env.Db.Audit.Rows, a => a.Action == "user.locked");

        env.Clock.Advance(StaffUser.LockDuration + TimeSpan.FromSeconds(1));
        await env.Users.LoginAsync("alice", Strong);
    }

    [Fact]
    public async Task A_success_resets_the_failure_count()
    {
        var env = new UserEnv();
        await env.AdminAsync();

        for (var i = 0; i < StaffUser.MaxFailedLogins - 1; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("alice", "wrong-password-1"));
        await env.Users.LoginAsync("alice", Strong);

        for (var i = 0; i < StaffUser.MaxFailedLogins - 1; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("alice", "wrong-password-1"));
        await env.Users.LoginAsync("alice", Strong);   // still not locked
    }

    [Fact]
    public async Task An_administrator_can_unlock()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();
        for (var i = 0; i < StaffUser.MaxFailedLogins; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("alice", "wrong-password-1"));

        await env.Users.UnlockAsync(alice.Id, "staff:bob");

        await env.Users.LoginAsync("alice", Strong);
    }

    [Fact]
    public async Task A_disabled_account_cannot_sign_in_and_only_the_right_password_learns_why()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();
        var bob = await env.Users.CreateAsync("bob", "Bob Ops", Roles.OpsAgent, "Temp-pass-123", "staff:alice");
        await env.Users.SetActiveAsync(bob.Id, false, alice.Id, "staff:alice");

        var right = await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("bob", "Temp-pass-123"));
        var wrong = await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("bob", "wrong-password-1"));

        Assert.Contains("disabled", right.Message);
        Assert.DoesNotContain("disabled", wrong.Message);
    }

    // Creating people

    [Fact]
    public async Task New_users_start_on_a_temporary_password()
    {
        var env = new UserEnv();
        var bob = await env.Users.CreateAsync("Bob.Ops", " Bob Ops ", Roles.Finance, "Temp-pass-123", "staff:alice");

        Assert.Equal("bob.ops", bob.Username);
        Assert.Equal("Bob Ops", bob.DisplayName);
        Assert.Equal(Roles.Finance, bob.Role);
        Assert.True(bob.MustChangePassword);
        Assert.True(bob.IsActive);
    }

    [Fact]
    public async Task Duplicate_usernames_bad_roles_and_weak_passwords_are_refused()
    {
        var env = new UserEnv();
        await env.AdminAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.CreateAsync("ALICE", "Another Alice", Roles.OpsAgent, "Temp-pass-123", "x"));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.CreateAsync("carol", "Carol", "root", "Temp-pass-123", "x"));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.CreateAsync("carol", "Carol", Roles.Courier, "Temp-pass-123", "x"));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.CreateAsync("carol", "Carol", Roles.OpsAgent, "abcd", "x"));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.CreateAsync("carol", "", Roles.OpsAgent, "Temp-pass-123", "x"));
        Assert.Single(env.Db.Staff.Rows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcd")]
    public void Too_short_passwords_are_refused(string password) =>
        Assert.Throws<DomainValidationException>(() => PasswordHasher.Validate(password, "carol"));

    [Theory]
    [InlineData("12345")]
    [InlineData("hello")]
    [InlineData("admin")]
    public void Any_password_of_five_or_more_characters_is_accepted(string password) =>
        PasswordHasher.Validate(password, "admin");

    [Fact]
    public void Generated_passwords_always_pass_the_policy()
    {
        for (var i = 0; i < 200; i++) PasswordHasher.Validate(PasswordHasher.Generate("admin"), "admin");
    }

    [Fact]
    public void Hashes_verify_and_are_salted()
    {
        var a = PasswordHasher.Hash(Strong);
        var b = PasswordHasher.Hash(Strong);

        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify(Strong, a));
        Assert.False(PasswordHasher.Verify("Sunrise-2026-no", a));
        Assert.False(PasswordHasher.Verify(null, a));
        Assert.False(PasswordHasher.Verify(Strong, "not-a-hash"));
    }

    // Roles and status

    [Fact]
    public async Task Changing_a_role_or_disabling_revokes_existing_sessions()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();
        var bob = await env.Users.CreateAsync("bob", "Bob Ops", Roles.OpsAgent, "Temp-pass-123", "staff:alice");
        var before = bob.SecurityStamp;

        await env.Users.ChangeRoleAsync(bob.Id, Roles.Finance, alice.Id, "staff:alice");
        var afterRole = bob.SecurityStamp;
        await env.Users.SetActiveAsync(bob.Id, false, alice.Id, "staff:alice");

        Assert.NotEqual(before, afterRole);
        Assert.NotEqual(afterRole, bob.SecurityStamp);
        Assert.Equal(Roles.Finance, bob.Role);
        Assert.False(bob.IsActive);
    }

    [Fact]
    public async Task Re_enabling_restores_access_with_a_new_stamp()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();
        var bob = await env.Users.CreateAsync("bob", "Bob Ops", Roles.OpsAgent, "Temp-pass-123", "staff:alice");
        await env.Users.SetActiveAsync(bob.Id, false, alice.Id, "staff:alice");
        var disabledStamp = bob.SecurityStamp;

        await env.Users.SetActiveAsync(bob.Id, true, alice.Id, "staff:alice");

        Assert.NotEqual(disabledStamp, bob.SecurityStamp);
        await env.Users.LoginAsync("bob", "Temp-pass-123");
    }

    [Fact]
    public async Task The_last_active_administrator_cannot_be_demoted_or_disabled()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();
        var other = await env.Users.CreateAsync("bob", "Bob Admin", Roles.OpsAdmin, "Temp-pass-123", "staff:alice");

        // Two admins: each can be changed by the other.
        await env.Users.ChangeRoleAsync(other.Id, Roles.Finance, alice.Id, "staff:alice");
        Assert.Equal(Roles.Finance, other.Role);

        // One admin left: even somebody else cannot take them out.
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.SetActiveAsync(alice.Id, false, other.Id, "staff:bob"));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.ChangeRoleAsync(alice.Id, Roles.OpsAgent, other.Id, "staff:bob"));
        Assert.Equal(Roles.OpsAdmin, alice.Role);
        Assert.True(alice.IsActive);
    }

    [Fact]
    public async Task Nobody_can_disable_or_demote_themselves()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();
        await env.Users.CreateAsync("bob", "Bob Admin", Roles.OpsAdmin, "Temp-pass-123", "staff:alice");

        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.SetActiveAsync(alice.Id, false, alice.Id, "staff:alice"));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.ChangeRoleAsync(alice.Id, Roles.OpsAgent, alice.Id, "staff:alice"));
    }

    [Fact]
    public async Task Unknown_users_are_not_found()
    {
        var env = new UserEnv();
        await Assert.ThrowsAsync<NotFoundException>(() => env.Users.SetActiveAsync(Guid.NewGuid(), false, null, "x"));
        await Assert.ThrowsAsync<NotFoundException>(() => env.Users.GetAsync(Guid.NewGuid()));
    }

    // Passwords

    [Fact]
    public async Task Changing_your_password_revokes_older_sessions_and_returns_a_fresh_one()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();
        var old = await env.Users.LoginAsync("alice", Strong);
        var oldStamp = env.Tokens.Validate(old.Token)!.Stamp;

        var fresh = await env.Users.ChangePasswordAsync(alice.Id, Strong, "Brand-new-pass-9");

        Assert.NotEqual(oldStamp, alice.SecurityStamp);
        Assert.Equal(alice.SecurityStamp, env.Tokens.Validate(fresh.Token)!.Stamp);
        Assert.False(fresh.User.MustChangePassword);
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("alice", Strong));
        await env.Users.LoginAsync("alice", "Brand-new-pass-9");
    }

    [Fact]
    public async Task Changing_your_password_needs_the_current_one_and_a_different_valid_new_one()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();

        await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.ChangePasswordAsync(alice.Id, "wrong-current-1", "Brand-new-pass-9"));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.ChangePasswordAsync(alice.Id, Strong, "abcd"));
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.ChangePasswordAsync(alice.Id, Strong, Strong));
        await env.Users.LoginAsync("alice", Strong);   // unchanged
    }

    [Fact]
    public async Task Wrong_current_passwords_count_towards_the_lockout()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();

        for (var i = 0; i < StaffUser.MaxFailedLogins; i++)
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.ChangePasswordAsync(alice.Id, "wrong-current-1", "Brand-new-pass-9"));

        await Assert.ThrowsAsync<TooManyAttemptsException>(() => env.Users.ChangePasswordAsync(alice.Id, Strong, "Brand-new-pass-9"));
    }

    [Fact]
    public async Task An_admin_reset_forces_a_new_password_and_revokes_sessions()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();
        var bob = await env.Users.CreateAsync("bob", "Bob Ops", Roles.OpsAgent, "Temp-pass-123", "staff:alice");
        await env.Users.ChangePasswordAsync(bob.Id, "Temp-pass-123", "Own-secret-pass-5");
        var stamp = bob.SecurityStamp;

        await env.Users.ResetPasswordAsync(bob.Id, "Reset-pass-456", alice.Id, "staff:alice");

        Assert.True(bob.MustChangePassword);
        Assert.NotEqual(stamp, bob.SecurityStamp);
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => env.Users.LoginAsync("bob", "Own-secret-pass-5"));
        Assert.True((await env.Users.LoginAsync("bob", "Reset-pass-456")).User.MustChangePassword);
    }

    [Fact]
    public async Task An_admin_cannot_reset_their_own_password_that_way()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => env.Users.ResetPasswordAsync(alice.Id, "Reset-pass-456", alice.Id, "staff:alice"));
    }

    // Audit

    [Fact]
    public async Task Every_change_is_audited_and_no_password_is_ever_written()
    {
        var env = new UserEnv();
        var alice = await env.AdminAsync();
        var bob = await env.Users.CreateAsync("bob", "Bob Ops", Roles.OpsAgent, "Temp-pass-123", "staff:alice");
        await env.Users.ChangeRoleAsync(bob.Id, Roles.Finance, alice.Id, "staff:alice");
        await env.Users.ResetPasswordAsync(bob.Id, "Reset-pass-456", alice.Id, "staff:alice");
        await env.Users.SetActiveAsync(bob.Id, false, alice.Id, "staff:alice");
        await env.Users.LoginAsync("alice", Strong);

        var actions = env.Db.Audit.Rows.Select(a => a.Action).ToList();
        foreach (var expected in new[] { "user.create", "user.change_password", "user.role", "user.reset_password", "user.disable", "user.login" })
            Assert.Contains(actions, a => a == expected);

        var text = string.Join(" ", env.Db.Audit.Rows.Select(a => $"{a.Actor} {a.Action} {a.EntityId} {a.Detail}"));
        foreach (var secret in new[] { Strong, "Temp-pass-123", "Reset-pass-456" })
            Assert.DoesNotContain(secret, text);
        Assert.Contains(env.Db.Audit.Rows, a => a.Action == "user.login" && a.Actor == "staff:alice");
    }

    // Tokens

    [Fact]
    public void Courier_tokens_without_a_stamp_still_validate()
    {
        var env = new UserEnv();
        var (token, _) = env.Tokens.Issue("courier-id", Roles.Courier, "Mphatso");

        var principal = env.Tokens.Validate(token);

        Assert.NotNull(principal);
        Assert.Null(principal!.Stamp);
        Assert.Equal(Roles.Courier, principal.Role);
    }

    [Fact]
    public void A_tampered_token_is_rejected()
    {
        var env = new UserEnv();
        var (token, _) = env.Tokens.Issue("id", Roles.OpsAdmin, "alice", "stamp");
        var parts = token.Split('.');

        Assert.Null(env.Tokens.Validate(parts[0] + "." + new string(parts[1].Reverse().ToArray())));
        Assert.Null(env.Tokens.Validate(token + "x"));
    }

    [Fact]
    public void An_expired_token_is_rejected()
    {
        var env = new UserEnv();
        var (token, _) = env.Tokens.Issue("id", Roles.OpsAdmin, "alice", "stamp", lifetimeHours: 1);

        env.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        Assert.Null(env.Tokens.Validate(token));
    }
}
