using CourierEscrow.Application.Services;
using CourierEscrow.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CourierEscrow.Infrastructure.Workers;

/// <summary>
/// Runs one unit of work on a timer, each time in a fresh DI scope (so a failed iteration never leaves a dirty
/// database context behind). The same code runs inside the API process or, later, in a separate worker host.
/// </summary>
public abstract class PeriodicWorker(IServiceScopeFactory scopes, ILogger logger, TimeSpan interval) : BackgroundService
{
    protected IServiceScopeFactory Scopes => scopes;
    protected ILogger Log => logger;

    protected abstract Task RunOnceAsync(IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ContinueWith(_ => { });
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "{Worker} iteration failed; will retry.", GetType().Name); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Run a step so that one failing step does not stop the others.</summary>
    protected async Task Step(string name, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "{Worker}: {Step} failed.", GetType().Name, name); }
    }
}

/// <summary>Cancels unpaid orders whose window ended, and asks the provider about payments whose webhook never came.</summary>
public sealed class PaymentWorker(IServiceScopeFactory scopes, ILogger<PaymentWorker> log) : PeriodicWorker(scopes, log, TimeSpan.FromSeconds(15))
{
    protected override async Task RunOnceAsync(IServiceProvider sp, CancellationToken ct)
    {
        var collections = sp.GetRequiredService<CollectionService>();
        await Step("expire", async () => { var n = await collections.ExpireDueAsync(ct); if (n > 0) Log.LogInformation("Expired {Count} unpaid orders.", n); });
        await Step("poll", async () => await collections.PollPendingAsync(ct));
    }
}

/// <summary>Applies stored provider callbacks that were not processed inline (crash, deadlock, or a bug that has since been fixed).</summary>
public sealed class WebhookInboxWorker(IServiceScopeFactory scopes, ILogger<WebhookInboxWorker> log) : PeriodicWorker(scopes, log, TimeSpan.FromSeconds(3))
{
    protected override async Task RunOnceAsync(IServiceProvider sp, CancellationToken ct)
    {
        var pending = await sp.GetRequiredService<WebhookService>().ListPendingAsync(25, ct);
        foreach (var item in pending)
        {
            try
            {
                using var scope = Scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<WebhookService>().ProcessAsync(item.Id, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Webhook {Key} failed.", item.EventKey);
                using var scope = Scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<WebhookService>().RecordFailureAsync(item.Id, ex.Message, ct);
            }
        }
    }
}

public sealed class PayoutWorker(IServiceScopeFactory scopes, ILogger<PayoutWorker> log) : PeriodicWorker(scopes, log, TimeSpan.FromSeconds(5))
{
    protected override async Task RunOnceAsync(IServiceProvider sp, CancellationToken ct)
    {
        var payouts = sp.GetRequiredService<PayoutService>();
        await Step("execute", async () => await payouts.ExecuteDueAsync(ct));
        await Step("poll", async () => await payouts.PollSubmittedAsync(ct));
    }
}

public sealed class NotificationWorker(IServiceScopeFactory scopes, ILogger<NotificationWorker> log) : PeriodicWorker(scopes, log, TimeSpan.FromSeconds(2))
{
    protected override Task RunOnceAsync(IServiceProvider sp, CancellationToken ct) =>
        sp.GetRequiredService<NotificationDispatcher>().DispatchDueAsync(ct);
}

/// <summary>Reconciles yesterday once it has not been done, after 02:00 UTC.</summary>
public sealed class ReconciliationWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReconciliationWorker> log)
    : PeriodicWorker(scopes, log, TimeSpan.FromMinutes(10))
{
    protected override async Task RunOnceAsync(IServiceProvider sp, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (now.Hour < 2) return;
        var yesterday = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-1));
        var service = sp.GetRequiredService<ReconciliationService>();
        var last = (await service.ListRunsAsync(1, ct)).FirstOrDefault();
        if (last is not null && last.ForDate >= yesterday) return;

        var result = await service.RunAsync(yesterday, ct);
        if (result.Run.ExceptionCount > 0) Log.LogWarning("Reconciliation for {Day}: {Count} exceptions.", yesterday, result.Run.ExceptionCount);
        else Log.LogInformation("Reconciliation for {Day}: clean ({Matched} matched).", yesterday, result.Run.MatchedCount);
    }
}

public static class WorkerRegistration
{
    /// <summary>Adds the background workers unless <c>Workers:Enabled</c> is false (for example when a separate host runs them).</summary>
    public static IServiceCollection AddEscrowWorkers(this IServiceCollection services, IConfiguration config)
    {
        if (!config.GetValue("Workers:Enabled", true)) return services;
        services.AddHostedService<PaymentWorker>();
        services.AddHostedService<WebhookInboxWorker>();
        services.AddHostedService<PayoutWorker>();
        services.AddHostedService<NotificationWorker>();
        if (config.GetValue("Workers:Reconciliation", true)) services.AddHostedService<ReconciliationWorker>();
        return services;
    }
}
