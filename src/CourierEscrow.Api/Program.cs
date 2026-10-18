using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CourierEscrow.Api;
using CourierEscrow.Api.Auth;
using CourierEscrow.Api.Endpoints;
using CourierEscrow.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);   // your own secrets; git-ignored
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddEscrowInfrastructure(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddAuthentication(EscrowAuthHandler.Scheme)
    .AddScheme<AuthenticationSchemeOptions, EscrowAuthHandler>(EscrowAuthHandler.Scheme, _ => { });
// Sign-in attempts per client address. The console is the only caller, so this is a ceiling for abuse;
// the per-account lockout in UserService is what stops someone guessing one person's password.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Staff, p => p.RequireRole("ops_agent", "ops_admin", "finance"))
    .AddPolicy(Policies.Ops, p => p.RequireRole("ops_agent", "ops_admin"))
    .AddPolicy(Policies.OpsAdmin, p => p.RequireRole("ops_admin"))
    .AddPolicy(Policies.Finance, p => p.RequireRole("ops_admin", "finance"))
    .AddPolicy(Policies.Courier, p => p.RequireRole("courier"));

var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>();

// The courier web app (PWA) is plain static files served from /courier-app/. See wwwroot/courier.
var courierFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(app.Environment.ContentRootPath, "wwwroot", "courier"));
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = courierFiles, RequestPath = "/courier-app" });
app.UseStaticFiles(new StaticFileOptions { FileProvider = courierFiles, RequestPath = "/courier-app" });
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<PasswordChangeGuard>();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapCourierEndpoints();
app.MapAdminEndpoints();
app.MapPublicEndpoints(app.Environment.IsDevelopment() || app.Configuration.GetValue("Dev:Endpoints", false));

app.Run();

public partial class Program;
