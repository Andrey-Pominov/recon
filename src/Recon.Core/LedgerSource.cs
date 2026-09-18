using System.Net.Http.Json;
using System.Text.Json;

namespace Recon.Core;

/// <summary>Where our side of the comparison comes from. The ledger's outbox in production; a list in tests.</summary>
public interface ILedgerSource
{
    /// <summary>Clearings announced after <paramref name="after"/>, and the cursor to continue from.</summary>
    Task<(IReadOnlyList<LedgerRecord> Records, long Next)> ReadClearingsAsync(long after, int limit, CancellationToken ct = default);
}

/// <summary>
/// Follows the ledger's <c>GET /events</c>. Only <c>entry.posted</c> events that settle a hold are
/// clearings; everything else on the feed (accounts, holds, funding) is skipped but still advances
/// the cursor. Currency is not on the event, so the account is looked up once and remembered.
/// </summary>
public sealed class LedgerHttpSource(HttpClient http) : ILedgerSource
{
    private readonly Dictionary<Guid, string> _currencyByAccount = new();

    public async Task<(IReadOnlyList<LedgerRecord> Records, long Next)> ReadClearingsAsync(long after, int limit, CancellationToken ct = default)
    {
        var page = await http.GetFromJsonAsync<EventPage>($"/events?after={after}&limit={limit}", Json, ct)
                   ?? throw new InvalidOperationException("ledger returned no body");
        var records = new List<LedgerRecord>();
        foreach (var e in page.Events)
        {
            if (e.Type != "entry.posted") continue;
            var p = e.Payload;
            if (!p.TryGetProperty("holdId", out var holdProp) || holdProp.ValueKind == JsonValueKind.Null) continue;

            // A capture is [-amount on the cardholder, +amount on the settlement account].
            var credit = p.GetProperty("postings").EnumerateArray().First(x => x.GetProperty("amount").GetInt64() > 0);
            var accountId = credit.GetProperty("accountId").GetGuid();
            records.Add(new LedgerRecord(
                p.GetProperty("id").GetGuid(),
                p.GetProperty("idempotencyKey").GetString()!,
                holdProp.GetGuid(),
                credit.GetProperty("amount").GetInt64(),
                await CurrencyAsync(accountId, ct),
                p.GetProperty("createdAt").GetDateTime(),
                e.Id));
        }
        return (records, page.Next);
    }

    private async Task<string> CurrencyAsync(Guid accountId, CancellationToken ct)
    {
        if (_currencyByAccount.TryGetValue(accountId, out var c)) return c;
        var account = await http.GetFromJsonAsync<AccountDto>($"/accounts/{accountId}", Json, ct)
                      ?? throw new InvalidOperationException($"ledger has no account {accountId}");
        return _currencyByAccount[accountId] = account.Currency;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record EventPage(IReadOnlyList<LedgerEvent> Events, long Next);
    private sealed record LedgerEvent(long Id, string Type, DateTime OccurredAt, JsonElement Payload);
    private sealed record AccountDto(Guid Id, string Currency);
}
