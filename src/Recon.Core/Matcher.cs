namespace Recon.Core;

/// <summary>
/// The comparison itself, with no database in sight: two lists in, matched count and findings out.
/// Keeping it pure is what lets every category be tested with three lines of setup.
/// </summary>
public static class Matcher
{
    public static (int Matched, IReadOnlyList<Finding> Findings) Compare(
        IReadOnlyList<LedgerRecord> ours, IReadOnlyList<ProviderRecord> theirs, MatchTolerance tolerance)
    {
        var findings = new List<Finding>();
        var byRef = ours.ToDictionary(r => r.Ref, StringComparer.Ordinal);
        var theirsByRef = theirs.GroupBy(t => t.OurRef, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var matched = 0;

        foreach (var (ourRef, lines) in theirsByRef)
        {
            if (lines.Count > 1)
            {
                findings.Add(new Finding(FindingCategory.DuplicateOnProvider, ourRef, byRef.GetValueOrDefault(ourRef)?.Amount, lines.Sum(l => l.Amount),
                    $"provider reported {ourRef} {lines.Count} times: {string.Join(", ", lines.Select(l => l.ProviderRef))}"));
                continue;   // a duplicate is its own problem; do not also grade each copy
            }

            var t = lines[0];
            if (!byRef.TryGetValue(ourRef, out var o))
            {
                findings.Add(new Finding(FindingCategory.MissingInLedger, ourRef, null, t.Amount,
                    $"provider booked {t.Amount} {t.Currency} as {t.ProviderRef} on {t.BookedAt:u}; no such clearing in the ledger"));
                continue;
            }

            if (!string.Equals(o.Currency, t.Currency, StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding(FindingCategory.CurrencyMismatch, ourRef, o.Amount, t.Amount, $"ledger {o.Currency}, provider {t.Currency}"));
            else if (o.Amount != t.Amount)
                findings.Add(new Finding(FindingCategory.AmountMismatch, ourRef, o.Amount, t.Amount, $"ledger {o.Amount}, provider {t.Amount}, difference {t.Amount - o.Amount}"));
            else if ((t.BookedAt - o.OccurredAt).Duration() > tolerance.Date)
                findings.Add(new Finding(FindingCategory.DateMismatch, ourRef, o.Amount, t.Amount, $"ledger {o.OccurredAt:u}, provider {t.BookedAt:u}, apart by {(t.BookedAt - o.OccurredAt).Duration():g}"));
            else
                matched++;
        }

        foreach (var o in ours)
        {
            if (theirsByRef.ContainsKey(o.Ref)) continue;
            findings.Add(new Finding(FindingCategory.MissingOnProvider, o.Ref, o.Amount, null,
                $"ledger cleared {o.Amount} {o.Currency} on {o.OccurredAt:u}; provider has not reported it"));
        }

        return (matched, findings.OrderBy(f => f.Ref, StringComparer.Ordinal).ThenBy(f => f.Category).ToList());
    }
}
