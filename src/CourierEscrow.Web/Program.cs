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

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program { }
