using CourierEscrow.Web.Components;
using CourierEscrow.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// People sign in with a username and password against the API; the console keeps their access token in an encrypted cookie.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "nexa.console";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.LoginPath = "/login";
        o.SlidingExpiration = false;   // the cookie lives exactly as long as the API token inside it
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// The console is a thin client of the escrow API; it never touches the database or the webhook secret.
// Each request carries the signed-in person's own token (see EscrowApiClient).
builder.Services.AddHttpClient<EscrowApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5080");
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHttpClient("OneKhusaWebhookProxy");

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Signing out only ends the caller's own session, so there is nothing for a forged request to gain.
app.MapPost("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

// The API said the session is over (user disabled, password changed, token expired): clear the cookie and ask for a fresh sign-in.
app.MapGet("/account/expired", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login?expired=1");
});

app.MapGet("/health", async (HttpContext context, IHttpClientFactory clients, IConfiguration configuration,
    ILogger<Program> logger, CancellationToken ct) =>
{
    var apiBaseUrl = (configuration["Api:BaseUrl"] ?? "http://localhost:5080").TrimEnd('/');
    try
    {
        using var response = await clients.CreateClient("OneKhusaWebhookProxy")
            .GetAsync($"{apiBaseUrl}/health", HttpCompletionOption.ResponseHeadersRead, ct);
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } contentType)
            context.Response.ContentType = contentType.ToString();
        await response.Content.CopyToAsync(context.Response.Body, ct);
    }
    catch (HttpRequestException ex)
    {
        logger.LogError(ex, "Could not reach the API health endpoint at {ApiBaseUrl}", apiBaseUrl);
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
    }
}).AllowAnonymous();

app.MapPost("/webhooks/{provider}", async (string provider, HttpRequest request, HttpContext context,
    IHttpClientFactory clients, IConfiguration configuration, ILogger<Program> logger, CancellationToken ct) =>
{
    var apiBaseUrl = (configuration["Api:BaseUrl"] ?? "http://localhost:5080").TrimEnd('/');
    var target = new Uri($"{apiBaseUrl}{request.Path}{request.QueryString}");
    using var forwardedRequest = new HttpRequestMessage(HttpMethod.Post, target)
    {
        Content = new StreamContent(request.Body)
    };

    foreach (var header in request.Headers)
    {
        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            continue;

        if (!forwardedRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            forwardedRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
    }

    try
    {
        using var response = await clients.CreateClient("OneKhusaWebhookProxy")
            .SendAsync(forwardedRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } contentType)
            context.Response.ContentType = contentType.ToString();
        await response.Content.CopyToAsync(context.Response.Body, ct);
    }
    catch (HttpRequestException ex)
    {
        logger.LogError(ex, "Could not forward {Provider} webhook to the API at {ApiBaseUrl}", provider, apiBaseUrl);
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
    }
}).AllowAnonymous().DisableAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program { }
