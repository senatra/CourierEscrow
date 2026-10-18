using CourierEscrow.Application.Helpers;
using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CourierEscrow.Infrastructure.Persistence;

/// <summary>
/// Database-level protection, applied after the schema exists. Application code is not trusted to be the only
/// writer: even a buggy migration or a manual psql session cannot rewrite the ledger, the order history or the
/// audit trail, or commit an unbalanced ledger transaction. Also creates the order-number sequence.
/// </summary>
public static class LedgerGuards
{
    public const string Sql = """
        CREATE OR REPLACE FUNCTION forbid_mutation() RETURNS trigger AS $$
        BEGIN
          RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP
            USING ERRCODE = 'integrity_constraint_violation';
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS trg_ledger_entries_immutable ON ledger_entries;
        CREATE TRIGGER trg_ledger_entries_immutable BEFORE UPDATE OR DELETE ON ledger_entries
          FOR EACH ROW EXECUTE FUNCTION forbid_mutation();

        DROP TRIGGER IF EXISTS trg_ledger_transactions_immutable ON ledger_transactions;
        CREATE TRIGGER trg_ledger_transactions_immutable BEFORE UPDATE OR DELETE ON ledger_transactions
          FOR EACH ROW EXECUTE FUNCTION forbid_mutation();

        DROP TRIGGER IF EXISTS trg_ledger_entries_no_truncate ON ledger_entries;
        CREATE TRIGGER trg_ledger_entries_no_truncate BEFORE TRUNCATE ON ledger_entries
          FOR EACH STATEMENT EXECUTE FUNCTION forbid_mutation();

        DROP TRIGGER IF EXISTS trg_order_events_immutable ON order_events;
        CREATE TRIGGER trg_order_events_immutable BEFORE UPDATE OR DELETE ON order_events
          FOR EACH ROW EXECUTE FUNCTION forbid_mutation();

        DROP TRIGGER IF EXISTS trg_audit_log_immutable ON audit_log;
        CREATE TRIGGER trg_audit_log_immutable BEFORE UPDATE OR DELETE ON audit_log
          FOR EACH ROW EXECUTE FUNCTION forbid_mutation();

        CREATE OR REPLACE FUNCTION ledger_assert_balanced() RETURNS trigger AS $$
        DECLARE
          diff bigint;
        BEGIN
          SELECT COALESCE(SUM(CASE WHEN direction = 'Debit' THEN amount ELSE -amount END), 0)
            INTO diff
            FROM ledger_entries
           WHERE transaction_id = NEW.transaction_id;
          IF diff <> 0 THEN
            RAISE EXCEPTION 'ledger transaction % is unbalanced by %', NEW.transaction_id, diff
              USING ERRCODE = 'integrity_constraint_violation';
          END IF;
          RETURN NULL;
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS trg_ledger_entries_balanced ON ledger_entries;
        CREATE CONSTRAINT TRIGGER trg_ledger_entries_balanced
          AFTER INSERT ON ledger_entries
          DEFERRABLE INITIALLY DEFERRED
          FOR EACH ROW EXECUTE FUNCTION ledger_assert_balanced();

        CREATE SEQUENCE IF NOT EXISTS order_number_seq START 1001;
        """;

    /// <summary>
    /// Columns added after the first release. <c>EnsureCreated</c> never alters a database that already has its tables, so a
    /// development database created earlier would be missing them and every order query would fail. Each statement is safe
    /// to run on every start (and does nothing on a fresh database). In production these belong in a real migration.
    /// </summary>
    public const string Upgrades = """
        ALTER TABLE escrows ADD COLUMN IF NOT EXISTS "ProductConfirmedAt" timestamp with time zone NULL;

        CREATE TABLE IF NOT EXISTS staff_users (
          "Id" uuid NOT NULL PRIMARY KEY,
          "Username" character varying(60) NOT NULL,
          "DisplayName" character varying(120) NOT NULL,
          "PasswordHash" character varying(200) NOT NULL,
          "Role" character varying(20) NOT NULL,
          "Status" character varying(12) NOT NULL,
          "MustChangePassword" boolean NOT NULL,
          "FailedLogins" integer NOT NULL,
          "LockedUntil" timestamp with time zone NULL,
          "SecurityStamp" character varying(40) NOT NULL,
          "CreatedAt" timestamp with time zone NOT NULL,
          "PasswordChangedAt" timestamp with time zone NOT NULL,
          "LastLoginAt" timestamp with time zone NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_staff_users_Username" ON staff_users ("Username");
        """;

    /// <summary>Development convenience. For production, generate real migrations and ship <see cref="Sql"/> and <see cref="Upgrades"/> as part of them.</summary>
    public static async Task InitializeAsync(EscrowDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync(Upgrades, ct);
        await db.Database.ExecuteSqlRawAsync(Sql, ct);
    }
}

/// <summary>Creates the database schema and append-only guards on startup when <c>Database:AutoInit</c> is true.</summary>
public sealed class DatabaseInitializer(IServiceScopeFactory scopes, ILogger<DatabaseInitializer> log, IHostEnvironment environment) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EscrowDbContext>();
        log.LogInformation("Initializing escrow database schema for {Environment}.", environment.EnvironmentName);
        try
        {
            await LedgerGuards.InitializeAsync(db, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogCritical(ex, "Escrow database initialization failed. Verify ConnectionStrings:Escrow, PostgreSQL availability, and database/schema privileges.");
            throw;
        }

        if (environment.IsDevelopment() && !await db.Couriers.AnyAsync(ct))
        {
            db.Couriers.Add(CourierAccount.Create("Demo Courier", PhoneNumber.Normalize("0999123456"), MobileNetwork.Airtel.ToString(), PinHasher.Hash("1234"), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            log.LogInformation("Seeded demo courier 0999 123 456 with PIN 1234 (development).");
        }
        log.LogInformation("Database schema and ledger guards are in place.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Creates the first administrator when the system has no staff users, so a fresh install can always be signed in to.
/// Registered after <see cref="DatabaseInitializer"/>, so in development the schema already exists. Elsewhere the
/// staff_users table must exist (see <see cref="LedgerGuards.Upgrades"/>); if it does not, this logs the problem and the app still starts.
/// </summary>
public sealed class AdminBootstrapper(IServiceScopeFactory scopes, ILogger<AdminBootstrapper> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var created = await scope.ServiceProvider.GetRequiredService<Application.Services.UserService>().EnsureBootstrapAdminAsync(ct);
            if (created is null) return;

            if (created.GeneratedPassword is null)
                log.LogWarning("Created the first administrator '{Username}' with the password from Auth:BootstrapAdmin:Password. It must be changed at first sign-in.", created.User.Username);
            else
                log.LogWarning("Created the first administrator '{Username}'. One-time password: {Password}  (shown once; it must be changed at first sign-in)", created.User.Username, created.GeneratedPassword);
        }
        catch (DuplicateOperationException)
        {
            // Another instance created it first.
        }
        catch (DomainValidationException ex)
        {
            log.LogError("Could not create the first administrator: {Reason} Fix Auth:BootstrapAdmin (or remove its Password to have one generated).", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not create the first administrator. Does the staff_users table exist? Run the statements in LedgerGuards.Upgrades, or enable Database:AutoInit.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
