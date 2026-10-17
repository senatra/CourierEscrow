using CourierEscrow.Application;
using CourierEscrow.Application.Services;
using CourierEscrow.Infrastructure.OneKhusa;
using CourierEscrow.Infrastructure.Security;
using CourierEscrow.Infrastructure.Sms;
using CourierEscrow.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CourierEscrow.Infrastructure.Payments;

public static class ProviderRegistration
{
    /// <summary>Everything that is not the database: payment provider, SMS, photo storage, encryption, application services.</summary>
    public static IServiceCollection AddEscrowServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(TimeProvider.System);

        var escrow = config.GetSection("Escrow").Get<EscrowOptions>() ?? new EscrowOptions();
        var auth = config.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        services.AddSingleton(escrow);
        services.AddSingleton(auth);
        services.AddSingleton<AccessTokens>();

        var security = config.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();
        services.AddSingleton(security);
        services.AddSingleton<IProtector, AesGcmProtector>();

        var storage = config.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        services.AddSingleton(storage);
        services.AddSingleton<IObjectStorage, LocalObjectStorage>();

        services.Configure<MockProviderOptions>(config.GetSection("Mock"));
        var providerName = config.GetValue("Payments:Provider", "Mock")!;
        if (providerName.Equals("OneKhusa", StringComparison.OrdinalIgnoreCase))
        {
            var ok = config.GetSection("OneKhusa").Get<OneKhusaOptions>() ?? new OneKhusaOptions();
            ok.Validate();
            services.AddSingleton(ok);
            services.AddSingleton<OneKhusaTokenCache>();
            services.AddHttpClient(OneKhusaProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
            services.AddSingleton<OneKhusaProvider>();
            services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<OneKhusaProvider>());
        }
        else
        {
            services.AddSingleton<MockPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<MockPaymentProvider>());
        }

        var sms = config.GetSection("Sms").Get<SmsOptions>() ?? new SmsOptions();
        services.AddSingleton(sms);
        if (sms.Provider.Equals("Http", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient("sms", c => c.Timeout = TimeSpan.FromSeconds(15));
            services.AddSingleton<ISmsSender, HttpSmsSender>();
        }
        else services.AddSingleton<ISmsSender, LogSmsSender>();

        services.AddSingleton<ISmsCapture, MemorySmsCapture>();
        services.AddScoped<EscrowCommit>();
        services.AddScoped<Notifier>();
        services.AddScoped<OrderService>();
        services.AddScoped<CourierService>();
        services.AddScoped<UserService>();
        services.AddScoped<CollectionService>();
        services.AddScoped<BuyerApprovalService>();
        services.AddScoped<DevSimulationService>();
        services.AddScoped<PayoutService>();
        services.AddScoped<HandoffService>();
        services.AddScoped<DisputeService>();
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<WebhookService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<AlertsService>();
        return services;
    }
}
