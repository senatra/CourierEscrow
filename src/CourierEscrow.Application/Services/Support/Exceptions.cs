namespace CourierEscrow.Application.Services.Support;

public sealed class NotFoundException(string message) : Exception(message);
public sealed class ConcurrencyConflictException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class DuplicateOperationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sign-in failed (unknown phone, wrong PIN, suspended account).</summary>
public sealed class AuthenticationFailedException(string message) : Exception(message);

/// <summary>Too many wrong PINs; the account is locked for a while.</summary>
public sealed class TooManyAttemptsException(string message) : Exception(message);

/// <summary>Signed in, but not allowed to do this (someone else's job, or a device that ops has not approved).</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>The payment provider failed or refused. <see cref="Transient"/> means trying again later may work.</summary>
public sealed class ProviderException(string message, bool transient = true, Exception? inner = null,
    int? statusCode = null, string? requestFingerprint = null, bool idempotencyConflict = false) : Exception(message, inner)
{
    public bool Transient { get; } = transient;
    public int? StatusCode { get; } = statusCode;
    public string? RequestFingerprint { get; } = requestFingerprint;
    public bool IdempotencyConflict { get; } = idempotencyConflict;
}
