using CourierEscrow.Application.Services.Support;
using CourierEscrow.Domain;
using Microsoft.AspNetCore.Mvc;

namespace CourierEscrow.Api;

/// <summary>Turns business errors into the right HTTP status with a clear message. Anything else is a 500 with no details.</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            if (ctx.Response.HasStarted) throw;
            var (status, title) = ex switch
            {
                NotFoundException => (404, "Not found"),
                DomainValidationException => (422, "Invalid request"),
                InvalidEscrowTransitionException => (409, "Not allowed in the current state"),
                ConcurrencyConflictException => (409, "Modified by someone else. Retry."),
                DuplicateOperationException => (409, "Already recorded"),
                AuthenticationFailedException => (401, "Sign-in failed"),
                TooManyAttemptsException => (429, "Too many attempts"),
                ForbiddenException => (403, "Forbidden"),
                ProviderException { Transient: true } => (503, "Payment provider unavailable. Retry."),
                ProviderException => (502, "Payment provider refused the request"),
                _ => (500, "Unexpected error")
            };

            if (status == 500) log.LogError(ex, "Unhandled error on {Path}", ctx.Request.Path);
            else if (status >= 502) log.LogWarning(ex, "Provider problem on {Path}", ctx.Request.Path);

            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = status, Title = title, Detail = status == 500 ? null : ex.Message
            });
        }
    }
}
