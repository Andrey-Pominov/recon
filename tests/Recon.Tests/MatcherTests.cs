using Recon.Core;

namespace Recon.Tests;

/// <summary>Every category, with no database: two lists in, findings out.</summary>
public sealed class MatcherTests
{
    private static readonly DateTime T0 = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
    private static LedgerRecord Ours(string r, long amt, string cur = "EUR", DateTime? at = null) => new(Guid.NewGuid(), r, Guid.NewGuid(), amt, cur, at ?? T0, 1);
    private static ProviderRecord Theirs(string r, long amt, string cur = "EUR", DateTime? at = null) => new("P-" + r, r, amt, cur, at ?? T0);
    private static readonly MatchTolerance Tol = new(TimeSpan.FromDays(2));

    [Fact]
    public void Identical_sides_match_with_no_findings()
    {
        var (matched, findings) = Matcher.Compare([Ours("a", 100), Ours("b", 250)], [Theirs("a", 100), Theirs("b", 250)], Tol);
        Assert.Equal(2, matched);
        Assert.Empty(findings);
    }

    [Fact]
    public void Each_category_is_reported_once_with_the_amounts_from_both_sides()
    {
        var ours = new[]
        {
            Ours("amount", 100), Ours("currency", 100), Ours("late", 100, at: T0),
            Ours("only-ours", 100), Ours("dup", 100), Ours("ok", 100),
        };
        var theirs = new[]
        {
            Theirs("amount", 130), Theirs("currency", 100, "USD"), Theirs("late", 100, at: T0.AddDays(3)),
            Theirs("only-theirs", 100), Theirs("dup", 60), Theirs("dup", 40), Theirs("ok", 100),
        };

        var (matched, findings) = Matcher.Compare(ours, theirs, Tol);

        Assert.Equal(1, matched);
        var byRef = findings.ToDictionary(f => f.Ref);
        Assert.Equal(6, findings.Count);
        Assert.Equal((FindingCategory.AmountMismatch, 100L, 130L), (byRef["amount"].Category, byRef["amount"].LedgerAmount, byRef["amount"].ProviderAmount));
        Assert.Equal(FindingCategory.CurrencyMismatch, byRef["currency"].Category);
        Assert.Equal(FindingCategory.DateMismatch, byRef["late"].Category);
        Assert.Equal((FindingCategory.MissingOnProvider, (long?)null), (byRef["only-ours"].Category, byRef["only-ours"].ProviderAmount));
        Assert.Equal((FindingCategory.MissingInLedger, (long?)null), (byRef["only-theirs"].Category, byRef["only-theirs"].LedgerAmount));
        Assert.Equal((FindingCategory.DuplicateOnProvider, 100L), (byRef["dup"].Category, byRef["dup"].ProviderAmount));
    }

    [Fact]
    public void Date_tolerance_is_inclusive_and_symmetric()
    {
        var edge = Tol.Date;
        Assert.Empty(Matcher.Compare([Ours("a", 1, at: T0)], [Theirs("a", 1, at: T0 + edge)], Tol).Findings);
        Assert.Empty(Matcher.Compare([Ours("a", 1, at: T0)], [Theirs("a", 1, at: T0 - edge)], Tol).Findings);
        Assert.Single(Matcher.Compare([Ours("a", 1, at: T0)], [Theirs("a", 1, at: T0 + edge + TimeSpan.FromSeconds(1))], Tol).Findings);
    }

    [Fact]
    public void Currency_is_checked_before_amount_so_a_wrong_currency_is_not_also_a_wrong_amount()
    {
        var (_, findings) = Matcher.Compare([Ours("a", 100, "EUR")], [Theirs("a", 100, "USD")], Tol);
        Assert.Single(findings);
        Assert.Equal(FindingCategory.CurrencyMismatch, findings[0].Category);
    }

    [Fact]
    public void Findings_come_out_in_a_stable_order()
    {
        var (_, a) = Matcher.Compare([Ours("z", 1), Ours("a", 1)], [Theirs("m", 1)], Tol);
        var (_, b) = Matcher.Compare([Ours("a", 1), Ours("z", 1)], [Theirs("m", 1)], Tol);
        Assert.Equal(a.Select(f => (f.Ref, f.Category)), b.Select(f => (f.Ref, f.Category)));
        Assert.Equal(["a", "m", "z"], a.Select(f => f.Ref));
    }
}

public sealed class ParserTests
{
    [Fact]
    public void Parses_a_well_formed_report()
    {
        var rows = ProviderReportParser.Parse(Csv.Of("P1,clr-1,1000,eur,2026-09-18T10:00:00Z", "P2,clr-2,-50,EUR,2026-09-18T11:00:00+02:00"));
        Assert.Equal(2, rows.Count);
        Assert.Equal(("P1", "clr-1", 1000L, "EUR"), (rows[0].ProviderRef, rows[0].OurRef, rows[0].Amount, rows[0].Currency));
        Assert.Equal(new DateTime(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc), rows[1].BookedAt);   // offset normalised to UTC
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("ref,amount\nx,1\n", "expected header")]
    [InlineData(Csv.Header + "\nP1,clr-1,ten,EUR,2026-09-18T10:00:00Z\n", "not an integer")]
    [InlineData(Csv.Header + "\nP1,clr-1,10,EURO,2026-09-18T10:00:00Z\n", "3-letter")]
    [InlineData(Csv.Header + "\nP1,clr-1,10,EUR,yesterday\n", "not a date")]
    [InlineData(Csv.Header + "\nP1,,10,EUR,2026-09-18T10:00:00Z\n", "required")]
    [InlineData(Csv.Header + "\nP1,clr-1,10,EUR\n", "columns")]
    public void Rejects_a_malformed_report_with_a_line_number(string csv, string reason)
    {
        var ex = Assert.Throws<InvalidReportException>(() => ProviderReportParser.Parse(csv));
        Assert.Contains(reason, ex.Message);
    }
}
