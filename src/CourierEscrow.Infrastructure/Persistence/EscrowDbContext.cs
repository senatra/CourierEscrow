using CourierEscrow.Domain;
using CourierEscrow.Domain.Models;
using CourierEscrow.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CourierEscrow.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
}

public sealed class EscrowDbContext(DbContextOptions<EscrowDbContext> options) : DbContext(options)
{
    /// <summary>Unique index names the unit of work translates into DuplicateOperationException.</summary>
    public const string LedgerIdempotencyIndex = "ux_ledger_transactions_idempotency_key";
    public const string WebhookEventKeyIndex = "ux_webhook_inbox_event_key";
    public const string ClientEventIndex = "ux_order_events_client_event";
    public const string StaffUsernameIndex = "IX_staff_users_Username";

    public DbSet<Escrow> Escrows => Set<Escrow>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<CourierAccount> Couriers => Set<CourierAccount>();
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<PaymentIntent> PaymentIntents => Set<PaymentIntent>();
    public DbSet<Payout> Payouts => Set<Payout>();
    public DbSet<Otp> Otps => Set<Otp>();
    public DbSet<Proof> Proofs => Set<Proof>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<Dispute> Disputes => Set<Dispute>();
    public DbSet<WebhookInboxItem> WebhookInbox => Set<WebhookInboxItem>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationItem> ReconciliationItems => Set<ReconciliationItem>();
    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Escrow>(b =>
        {
            b.ToTable("escrows");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.OrderNumber).HasMaxLength(30).IsRequired();
            b.Property(e => e.ItemDescription).HasMaxLength(300).IsRequired();
            b.Property(e => e.PickupAddress).HasMaxLength(300);
            b.Property(e => e.DeliveryAddress).HasMaxLength(300);
            b.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            b.Property(e => e.State).HasConversion<string>().HasMaxLength(20).IsRequired();
            b.Property(e => e.DisputedFrom).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.PaymentReference).HasMaxLength(40);
            b.Property(e => e.SealId).HasMaxLength(60);
            b.Property(e => e.TrackingToken).HasMaxLength(20).IsRequired();
            b.Property(e => e.OpsFlag).HasMaxLength(500);
            b.Property(e => e.Version).IsConcurrencyToken();
            b.HasIndex(e => e.PaymentReference).IsUnique();
            b.HasIndex(e => e.TrackingToken).IsUnique();
            b.HasIndex(e => e.OrderNumber).IsUnique();
            b.HasIndex(e => new { e.CourierId, e.State });
            b.Ignore(e => e.PendingEvents);
            b.Ignore(e => e.PendingOrderEvents);
            b.Ignore(e => e.Total);
            b.Ignore(e => e.SellerProceeds);
        });

        m.Entity<OrderEvent>(b =>
        {
            b.ToTable("order_events");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Type).HasMaxLength(60).IsRequired();
            b.Property(e => e.FromState).HasMaxLength(20).IsRequired();
            b.Property(e => e.ToState).HasMaxLength(20).IsRequired();
            b.Property(e => e.Actor).HasMaxLength(100).IsRequired();
            b.Property(e => e.ClientEventId).HasMaxLength(100);
            b.Property(e => e.Payload).HasColumnType("jsonb");
            b.HasIndex(e => new { e.EscrowId, e.CreatedAt });
            b.HasIndex(e => new { e.EscrowId, e.ClientEventId }).IsUnique().HasDatabaseName(ClientEventIndex);
        });

        m.Entity<Party>(b =>
        {
            b.ToTable("parties");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.Property(p => p.Role).HasConversion<string>().HasMaxLength(10).IsRequired();
            b.Property(p => p.Name).HasMaxLength(120).IsRequired();
            b.Property(p => p.Phone).HasMaxLength(15).IsRequired();
            b.Property(p => p.WalletNetwork).HasConversion<string>().HasMaxLength(10);
            b.HasIndex(p => new { p.Role, p.Phone }).IsUnique();
        });

        m.Entity<CourierAccount>(b =>
        {
            b.ToTable("couriers");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).ValueGeneratedNever();
            b.Property(c => c.Name).HasMaxLength(120).IsRequired();
            b.Property(c => c.Phone).HasMaxLength(15).IsRequired();
            b.Property(c => c.WalletNetwork).HasConversion<string>().HasMaxLength(10);
            b.Property(c => c.PinHash).HasMaxLength(200).IsRequired();
            b.Property(c => c.DeviceId).HasMaxLength(100);
            b.Property(c => c.Status).HasConversion<string>().HasMaxLength(12);
            b.HasIndex(c => c.Phone).IsUnique();
        });

        m.Entity<StaffUser>(b =>
        {
            b.ToTable("staff_users");
            b.HasKey(u => u.Id);
            b.Property(u => u.Id).ValueGeneratedNever();
            b.Property(u => u.Username).HasMaxLength(60).IsRequired();
            b.Property(u => u.DisplayName).HasMaxLength(120).IsRequired();
            b.Property(u => u.PasswordHash).HasMaxLength(200).IsRequired();
            b.Property(u => u.Role).HasMaxLength(20).IsRequired();
            b.Property(u => u.Status).HasConversion<string>().HasMaxLength(12);
            b.Property(u => u.SecurityStamp).HasMaxLength(40).IsRequired();
            b.HasIndex(u => u.Username).IsUnique().HasDatabaseName(StaffUsernameIndex);
        });

        m.Entity<PaymentIntent>(b =>
        {
            b.ToTable("payment_intents");
            b.HasKey(i => i.Id);
            b.Property(i => i.Id).ValueGeneratedNever();
            b.Property(i => i.Reference).HasMaxLength(40).IsRequired();
            b.Property(i => i.ProviderRef).HasMaxLength(100);
            b.Property(i => i.Currency).HasMaxLength(3).IsRequired();
            b.Property(i => i.Status).HasConversion<string>().HasMaxLength(12);
            b.Ignore(i => i.IsOpen);
            b.HasIndex(i => i.Reference).IsUnique();
            b.HasIndex(i => i.EscrowId);
            b.HasIndex(i => i.Status);
        });

        m.Entity<Payout>(b =>
        {
            b.ToTable("payouts");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.Property(p => p.Kind).HasConversion<string>().HasMaxLength(12);
            b.Property(p => p.BeneficiaryName).HasMaxLength(120).IsRequired();
            b.Property(p => p.Msisdn).HasMaxLength(15).IsRequired();
            b.Property(p => p.Network).HasConversion<string>().HasMaxLength(10);
            b.Property(p => p.Currency).HasMaxLength(3).IsRequired();
            b.Property(p => p.Reference).HasMaxLength(40).IsRequired();
            b.Property(p => p.ProviderRef).HasMaxLength(100);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(12);
            b.Property(p => p.LastError).HasMaxLength(500);
            b.Ignore(p => p.PayableAccountKey);
            b.HasIndex(p => p.Reference).IsUnique();
            b.HasIndex(p => p.EscrowId);
            b.HasIndex(p => new { p.Status, p.NextAttemptAt });
        });

        m.Entity<Otp>(b =>
        {
            b.ToTable("otps");
            b.HasKey(o => o.EscrowId);
            b.Property(o => o.EscrowId).ValueGeneratedNever();
            b.Property(o => o.Hmac).HasMaxLength(100).IsRequired();
            b.Property(o => o.Status).HasConversion<string>().HasMaxLength(10);
        });

        m.Entity<Proof>(b =>
        {
            b.ToTable("proofs");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.Property(p => p.Kind).HasConversion<string>().HasMaxLength(12);
            b.Property(p => p.ObjectKey).HasMaxLength(200).IsRequired();
            b.Property(p => p.Sha256).HasMaxLength(64).IsRequired();
            b.HasIndex(p => p.EscrowId);
        });

        m.Entity<Upload>(b =>
        {
            b.ToTable("uploads");
            b.HasKey(u => u.Id);
            b.Property(u => u.Id).ValueGeneratedNever();
            b.Property(u => u.ObjectKey).HasMaxLength(200).IsRequired();
            b.Property(u => u.ContentType).HasMaxLength(60).IsRequired();
            b.Property(u => u.Sha256).HasMaxLength(64);
        });

        m.Entity<Dispute>(b =>
        {
            b.ToTable("disputes");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).ValueGeneratedNever();
            b.Property(d => d.RaisedBy).HasMaxLength(100).IsRequired();
            b.Property(d => d.Reason).HasMaxLength(1000).IsRequired();
            b.Property(d => d.Status).HasConversion<string>().HasMaxLength(10);
            b.Property(d => d.PriorState).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.Outcome).HasConversion<string>().HasMaxLength(30);
            b.Property(d => d.DecidedBy).HasMaxLength(100);
            b.Property(d => d.DecisionReason).HasMaxLength(1000);
            b.HasIndex(d => d.EscrowId);
            b.HasIndex(d => d.Status);
        });

        m.Entity<WebhookInboxItem>(b =>
        {
            b.ToTable("webhook_inbox");
            b.HasKey(i => i.Id);
            b.Property(i => i.Id).ValueGeneratedNever();
            b.Property(i => i.Provider).HasMaxLength(30).IsRequired();
            b.Property(i => i.EventKey).HasMaxLength(200).IsRequired();
            b.Property(i => i.EventCode).HasMaxLength(60).IsRequired();
            b.Property(i => i.RawPayload).HasColumnType("text").IsRequired();
            b.Property(i => i.Status).HasConversion<string>().HasMaxLength(10);
            b.Property(i => i.Outcome).HasMaxLength(30);
            b.Property(i => i.LastError).HasMaxLength(500);
            b.HasIndex(i => i.EventKey).IsUnique().HasDatabaseName(WebhookEventKeyIndex);
            b.HasIndex(i => i.Status);
        });

        m.Entity<Notification>(b =>
        {
            b.ToTable("notifications");
            b.HasKey(n => n.Id);
            b.Property(n => n.Id).ValueGeneratedNever();
            b.Property(n => n.ToPhone).HasMaxLength(15).IsRequired();
            b.Property(n => n.Template).HasMaxLength(60).IsRequired();
            b.Property(n => n.Body).HasMaxLength(1000).IsRequired();
            b.Property(n => n.Status).HasConversion<string>().HasMaxLength(10);
            b.Property(n => n.ProviderRef).HasMaxLength(100);
            b.Property(n => n.LastError).HasMaxLength(500);
            b.HasIndex(n => new { n.Status, n.NextAttemptAt });
            b.HasIndex(n => n.EscrowId);
        });

        m.Entity<AuditEntry>(b =>
        {
            b.ToTable("audit_log");
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).ValueGeneratedNever();
            b.Property(a => a.Actor).HasMaxLength(100).IsRequired();
            b.Property(a => a.Action).HasMaxLength(60).IsRequired();
            b.Property(a => a.EntityType).HasMaxLength(30).IsRequired();
            b.Property(a => a.EntityId).HasMaxLength(60).IsRequired();
            b.Property(a => a.Detail).HasMaxLength(2000);
            b.HasIndex(a => a.CreatedAt);
        });

        m.Entity<ReconciliationRun>(b =>
        {
            b.ToTable("reconciliation_runs");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).ValueGeneratedNever();
            b.HasIndex(r => r.RanAt);
        });

        m.Entity<ReconciliationItem>(b =>
        {
            b.ToTable("reconciliation_items");
            b.HasKey(i => i.Id);
            b.Property(i => i.Id).ValueGeneratedNever();
            b.Property(i => i.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(i => i.Reference).HasMaxLength(100).IsRequired();
            b.Property(i => i.Detail).HasMaxLength(500).IsRequired();
            b.HasIndex(i => i.RunId);
        });

        // Column names are explicit because the SQL guards in LedgerGuards.cs refer to them.
        m.Entity<LedgerTransaction>(b =>
        {
            b.ToTable("ledger_transactions");
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(t => t.EscrowId).HasColumnName("escrow_id");
            b.Property(t => t.Kind).HasColumnName("kind").HasMaxLength(30).IsRequired();
            b.Property(t => t.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(200).IsRequired();
            b.Property(t => t.CreatedAt).HasColumnName("created_at");
            b.HasIndex(t => t.IdempotencyKey).IsUnique().HasDatabaseName(LedgerIdempotencyIndex);
            b.HasIndex(t => t.EscrowId);
            b.HasMany(t => t.Entries).WithOne().HasForeignKey(e => e.TransactionId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(t => t.Entries).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        m.Entity<LedgerEntry>(b =>
        {
            b.ToTable("ledger_entries", t => t.HasCheckConstraint("ck_ledger_entries_amount_positive", "amount > 0"));
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.TransactionId).HasColumnName("transaction_id");
            b.Property(e => e.Account).HasColumnName("account").HasMaxLength(200).IsRequired();
            b.Property(e => e.Direction).HasColumnName("direction").HasConversion<string>().HasMaxLength(10).IsRequired();
            b.Property(e => e.Amount).HasColumnName("amount");
            b.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            b.HasIndex(e => e.Account);
            b.HasIndex(e => e.TransactionId);
        });

        m.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasMaxLength(100).IsRequired();
            b.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            b.HasIndex(x => x.OccurredAt).HasFilter("\"PublishedAt\" IS NULL").HasDatabaseName("ix_outbox_unpublished");
        });
    }
}
