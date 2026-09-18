using System.Globalization;

namespace Recon.Core;

/// <summary>
/// Reads the provider's CSV. The format is small and fixed on purpose — a real provider's file is
/// wider and stranger, and an adapter per provider would sit in front of this.
///
/// <code>provider_ref,our_ref,amount,currency,booked_at</code> — amount in minor units, booked_at ISO-8601.
/// </summary>
public static class ProviderReportParser
{
    private static readonly string[] Header = ["provider_ref", "our_ref", "amount", "currency", "booked_at"];

    public static IReadOnlyList<ProviderRecord> Parse(string csv)
    {
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) throw new InvalidReportException("report is empty");
        var header = lines[0].Split(',').Select(h => h.Trim().ToLowerInvariant()).ToArray();
        if (!header.SequenceEqual(Header))
            throw new InvalidReportException($"expected header '{string.Join(",", Header)}', got '{lines[0]}'");

        var rows = new List<ProviderRecord>(lines.Count - 1);
        for (var i = 1; i < lines.Count; i++)
        {
            var cols = lines[i].Split(',');
            if (cols.Length != Header.Length) throw new InvalidReportException($"line {i + 1}: expected {Header.Length} columns, got {cols.Length}");
            var providerRef = cols[0].Trim(); var ourRef = cols[1].Trim(); var currency = cols[3].Trim().ToUpperInvariant();
            if (providerRef.Length == 0 || ourRef.Length == 0) throw new InvalidReportException($"line {i + 1}: provider_ref and our_ref are required");
            if (!long.TryParse(cols[2].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
                throw new InvalidReportException($"line {i + 1}: amount '{cols[2]}' is not an integer in minor units");
            if (currency.Length != 3) throw new InvalidReportException($"line {i + 1}: currency '{cols[3]}' is not a 3-letter code");
            if (!DateTime.TryParse(cols[4].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var bookedAt))
                throw new InvalidReportException($"line {i + 1}: booked_at '{cols[4]}' is not a date");
            rows.Add(new ProviderRecord(providerRef, ourRef, amount, currency, bookedAt));
        }
        return rows;
    }
}
