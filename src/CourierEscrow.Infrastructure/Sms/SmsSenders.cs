using System.Net.Http.Json;
using CourierEscrow.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CourierEscrow.Infrastructure.Sms;

public sealed class SmsOptions
{
    /// <summary>"Log" writes messages to the log (development). "Http" posts to an aggregator.</summary>
    public string Provider { get; set; } = "Log";
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string SenderId { get; set; } = "NexaEscrow";
}

/// <summary>Development sender: nothing leaves the machine. The number is masked, the text is logged so you can read the OTP.</summary>
public sealed class LogSmsSender(ILogger<LogSmsSender> log) : ISmsSender
{
    public Task<string?> SendAsync(string toPhone, string body, CancellationToken ct)
    {
        log.LogInformation("SMS to {Phone}: {Body}", Domain.PhoneNumber.Mask(toPhone), body);
        return Task.FromResult<string?>($"log-{Guid.NewGuid():N}");
    }
}

/// <summary>Posts {to, from, message} to an SMS aggregator with a bearer key. Adjust the payload to your aggregator's API.</summary>
public sealed class HttpSmsSender(IHttpClientFactory factory, SmsOptions options) : ISmsSender
{
    public async Task<string?> SendAsync(string toPhone, string body, CancellationToken ct)
    {
        using var http = factory.CreateClient("sms");
        using var req = new HttpRequestMessage(HttpMethod.Post, options.Url) { Content = JsonContent.Create(new { to = "+" + toPhone, from = options.SenderId, message = body }) };
        req.Headers.Authorization = new("Bearer", options.ApiKey);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return res.Headers.TryGetValues("X-Message-Id", out var ids) ? ids.FirstOrDefault() : null;
    }
}
