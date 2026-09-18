namespace Recon.Core;

/// <summary>A clearing as the ledger announced it. <paramref name="Ref"/> is the idempotency key — the reference the provider echoes back.</summary>
public sealed record LedgerRecord(Guid EntryId, string Ref, Guid HoldId, long Amount, string Currency, DateTime OccurredAt, long EventId);

/// <summary>One line of a provider report.</summary>
public sealed record ProviderRecord(string ProviderRef, string OurRef, long Amount, string Currency, DateTime BookedAt);

public sealed record ProviderReport(Guid Id, string Name, DateTime ImportedAt, int RowCount);

public enum FindingCategory
{
    AmountMismatch, CurrencyMismatch, DateMismatch,
    MissingOnProvider, MissingInLedger, DuplicateOnProvider,
}

/// <summary>One discrepancy, as found by one run. Immutable; a later run either finds it again or does not.</summary>
public sealed record Finding(FindingCategory Category, string Ref, long? LedgerAmount, long? ProviderAmount, string Detail);

public sealed record Run(Guid Id, DateTime StartedAt, long LedgerPosition, IReadOnlyList<Guid> ReportIds, int Matched, int Discrepancies);

/// <summary>How a run differs from the one before it: discrepancies that appeared, and ones that went away.</summary>
public sealed record RunTransition(Guid RunId, Guid? PreviousId, int Opened, int Resolved);

/// <summary>Where matching is lenient. Card clearings book a day or two after the authorization; that is not a discrepancy.</summary>
public sealed record MatchTolerance(TimeSpan Date)
{
    public static readonly MatchTolerance Default = new(TimeSpan.FromDays(2));
}

public abstract class ReconException(string message) : Exception(message);
public sealed class InvalidReportException(string message) : ReconException(message);
public sealed class NotFoundException(string what, Guid id) : ReconException($"{what} {id} not found");
