using CourierEscrow.Application.Interfaces;
using CourierEscrow.Infrastructure.Messaging;
using CourierEscrow.Infrastructure.Payments;
using CourierEscrow.Infrastructure.Persistence;
using CourierEscrow.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CourierEscrow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEscrowInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<EscrowDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Escrow")
                        ?? throw new InvalidOperationException("ConnectionStrings:Escrow is not configured."),
                npg => npg.EnableRetryOnFailure(5)));

        // Registered first so the schema exists before any worker starts.
        if (config.GetValue("Database:AutoInit", false)) services.AddHostedService<DatabaseInitializer>();

        // Runs whether or not AutoInit is on: a fresh system always gets its first administrator (and logs why, if it cannot).
        services.AddHostedService<AdminBootstrapper>();

        services.AddScoped<IEscrowRepository, EscrowRepository>();
        services.AddScoped<IPartyRepository, PartyRepository>();
        services.AddScoped<ICourierRepository, CourierRepository>();
        services.AddScoped<IStaffUserRepository, StaffUserRepository>();
        services.AddScoped<IPaymentIntentRepository, PaymentIntentRepository>();
        services.AddScoped<IPaymentAttemptRepository, PaymentAttemptRepository>();
        services.AddScoped<IPayoutRepository, PayoutRepository>();
        services.AddScoped<IOtpRepository, OtpRepository>();
        services.AddScoped<IProofRepository, ProofRepository>();
        services.AddScoped<IUploadRepository, UploadRepository>();
        services.AddScoped<IDisputeRepository, DisputeRepository>();
        services.AddScoped<IOrderEventStore, OrderEventStore>();
        services.AddScoped<IWebhookInbox, WebhookInbox>();
        services.AddScoped<INotificationStore, NotificationStore>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IReconciliationStore, ReconciliationStore>();
        services.AddScoped<ILedgerStore, LedgerStore>();
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<INumberSequence, EfNumberSequence>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        // Payment provider, SMS, photo storage, encryption and the application services (no database types in there).
        services.AddEscrowServices(config);
        services.AddEscrowWorkers(config);

        if (config.GetValue("RabbitMq:Enabled", false))
        {
            services.Configure<RabbitMqOptions>(config.GetSection("RabbitMq"));
            services.AddHostedService<OutboxPublisher>();
        }

        return services;
    }
}
