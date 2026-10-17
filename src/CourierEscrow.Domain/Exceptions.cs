namespace CourierEscrow.Domain;

public abstract class DomainException(string message) : Exception(message);

/// <summary>An input or invariant was violated (bad amount, fees larger than the order, ...).</summary>
public sealed class DomainValidationException(string message) : DomainException(message);

/// <summary>The requested action is not allowed from the escrow's current state.</summary>
public sealed class InvalidEscrowTransitionException(EscrowState from, string action)
    : DomainException($"Cannot {action} while the escrow is {from}.")
{
    public EscrowState From { get; } = from;
}
