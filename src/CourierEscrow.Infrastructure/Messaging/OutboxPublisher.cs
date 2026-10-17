using System.Text;
using CourierEscrow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CourierEscrow.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string Exchange { get; set; } = "escrow.events";
}

public static class RoutingKeys
{
    /// <summary>"EscrowFunded" becomes "escrow.funded".</summary>
    public static string For(string eventType) =>
        "escrow." + eventType.Replace("Escrow", "", StringComparison.Ordinal).ToLowerInvariant();
}

/// <summary>
/// Relays outbox rows to RabbitMQ. Delivery is at-least-once: a crash after publish but before the row is marked
/// published causes a re-publish, so consumers must dedupe on the message id (the domain event id).
/// FOR UPDATE SKIP LOCKED lets several instances run side by side without publishing the same row at once.
/// </summary>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopes,
    IOptions<RabbitMqOptions> options,
    TimeProvider clock,
    ILogger<OutboxPublisher> log) : BackgroundService
{
    private const int BatchSize = 50;
    private IConnection? _connection;
    private IModel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(1);
            try
            {
                var published = await PublishBatchAsync(stoppingToken);
                if (published == BatchSize) continue; // more waiting, go again immediately
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Outbox publish failed; will retry.");
                ResetConnection();
                delay = TimeSpan.FromSeconds(5);
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        ResetConnection();
    }

    private async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EscrowDbContext>();

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs this whole block, so drop anything tracked by a failed attempt.
            db.ChangeTracker.Clear();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var batch = await db.OutboxMessages
                .FromSqlRaw($"""
                SELECT * FROM outbox_messages
                 WHERE "PublishedAt" IS NULL
                 ORDER BY "OccurredAt"
                 LIMIT {BatchSize}
                 FOR UPDATE SKIP LOCKED
                """)
                .ToListAsync(ct);

            if (batch.Count == 0) return 0;

            var channel = EnsureChannel();
            var exchange = options.Value.Exchange;

            foreach (var message in batch)
            {
                var props = channel.CreateBasicProperties();
                props.MessageId = message.Id.ToString();
                props.Type = message.Type;
                props.ContentType = "application/json";
                props.Persistent = true;
                props.Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds());

                channel.BasicPublish(exchange, RoutingKeys.For(message.Type), mandatory: false, props, Encoding.UTF8.GetBytes(message.Payload));
                message.Attempts++;
            }

            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));

            var now = clock.GetUtcNow();
            foreach (var message in batch) message.PublishedAt = now;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return batch.Count;
        });
    }
    private IModel EnsureChannel()
    {
        if (_channel is { IsOpen: true }) return _channel;

        ResetConnection();
        var o = options.Value;
        var factory = new ConnectionFactory { HostName = o.Host, Port = o.Port, UserName = o.User, Password = o.Password };
        _connection = factory.CreateConnection("courier-escrow-outbox");
        _channel = _connection.CreateModel();
        _channel.ExchangeDeclare(o.Exchange, ExchangeType.Topic, durable: true);
        _channel.ConfirmSelect();
        return _channel;
    }

    private void ResetConnection()
    {
        try { _channel?.Dispose(); } catch { /* already broken */ }
        try { _connection?.Dispose(); } catch { /* already broken */ }
        _channel = null;
        _connection = null;
    }
}
