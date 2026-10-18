using CourierEscrow.Application.Interfaces;
using CourierEscrow.Application.Services;

namespace CourierEscrow.Api;

public sealed record LedgerEntryDto(string Account, string Direction, long Amount);
public sealed record LedgerTxDto(Guid Id, Guid EscrowId, string Kind, DateTimeOffset At, IReadOnlyList<LedgerEntryDto> Entries);
public sealed record DisputeDto(Guid Id, Guid EscrowId, string RaisedBy, string Reason, string Status, string PriorState, DateTimeOffset OpenedAt, DateTimeOffset DueAt, string? Outcome, long BuyerAmount, long SellerAmount, long CourierAmount, long PlatformAmount, string? DecidedBy, string? DecisionReason);
public sealed record CourierDto(Guid Id, string Name, string Phone, string Network, string Status, bool DeviceBound);
public sealed record SummaryDto(LedgerSummary Ledger, AlertSummary Alerts);
public sealed record ReconciliationRunDto(Guid Id, string ForDate, DateTimeOffset RanAt, int ProviderCount, int MatchedCount, int ExceptionCount);
public sealed record ReconciliationItemDto(string Kind, string Reference, string Detail);
