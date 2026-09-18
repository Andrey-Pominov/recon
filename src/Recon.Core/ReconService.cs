using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace Recon.Core;

public sealed class ReconService(NpgsqlDataSource db, ILedgerSource ledger)
{
    // ---------- our side ----------

    /// <summary>Pull everything new from the ledger's outbox into ledger_records. Safe to call any time; re-reading is a no-op.</summary>
    public async Task<int> SyncLedgerAsync(CancellationToken ct = default)
    {
        var added = 0;
        while (true)
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            var position = await conn.ExecuteScalarAsync<long>("select position from ledger_cursor");
            var (records, next) = await ledger.ReadClearingsAsync(position, 500, ct);
            if (next == position) return added;

            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var r in records)
            {
                added += await conn.ExecuteAsync("""
                    insert into ledger_records (entry_id, ref, hold_id, amount, currency, occurred_at, event_id)
                    values (@EntryId, @Ref, @HoldId, @Amount, @Currency, @OccurredAt, @EventId)
                    on conflict (entry_id) do nothing
                    """, r, tx);
            }
            await conn.ExecuteAsync("update ledger_cursor set position = @next, updated_at = now()", new { next }, tx);
            await tx.CommitAsync(ct);
        }
    }

    // ---------- their side ----------

    /// <summary>Import a provider report. The same bytes imported twice are the same report.</summary>
    public async Task<ProviderReport> ImportReportAsync(string name, string csv, CancellationToken ct = default)
    {
        var rows = ProviderReportParser.Parse(csv);
        var sha = SHA256.HashData(Encoding.UTF8.GetBytes(csv));

        await using var conn = await db.OpenConnectionAsync(ct);
        if (await conn.QuerySingleOrDefaultAsync<ProviderReport>(
                "select id, name, imported_at as ImportedAt, row_count as RowCount from provider_reports where sha256 = @sha", new { sha }) is { } existing)
            return existing;

        var report = new ProviderReport(Guid.NewGuid(), name, DateTime.UtcNow, rows.Count);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync("insert into provider_reports (id, name, sha256, imported_at, row_count) values (@Id, @Name, @sha, @ImportedAt, @RowCount)",
                new { report.Id, report.Name, sha, report.ImportedAt, report.RowCount }, tx);
            await conn.ExecuteAsync("""
                insert into provider_records (report_id, provider_ref, our_ref, amount, currency, booked_at)
                values (@ReportId, @ProviderRef, @OurRef, @Amount, @Currency, @BookedAt)
                """, rows.Select(r => new { ReportId = report.Id, r.ProviderRef, r.OurRef, r.Amount, r.Currency, r.BookedAt }), tx);
            await tx.CommitAsync(ct);
            return report;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);   // someone imported the same file a moment ago
            return (await conn.QuerySingleAsync<ProviderReport>(
                "select id, name, imported_at as ImportedAt, row_count as RowCount from provider_reports where sha256 = @sha", new { sha }));
        }
    }

    public async Task<IReadOnlyList<ProviderReport>> ReportsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<ProviderReport>("select id, name, imported_at as ImportedAt, row_count as RowCount from provider_reports order by imported_at")).ToList();
    }

    // ---------- the comparison ----------

    /// <summary>
    /// Compare everything known on both sides and record every discrepancy as a snapshot.
    /// The inputs — ledger position, set of reports, tolerance — are hashed; the same inputs
    /// return the run that already exists instead of writing a second one.
    /// </summary>
    public async Task<Run> RunAsync(MatchTolerance? tolerance = null, CancellationToken ct = default)
    {
        tolerance ??= MatchTolerance.Default;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // One run at a time: two concurrent runs over the same inputs must produce one row.
        await conn.ExecuteAsync("select pg_advisory_xact_lock(hashtext('recon.run'))", transaction: tx);

        var position = await conn.ExecuteScalarAsync<long>("select position from ledger_cursor", transaction: tx);
        var reportIds = (await conn.QueryAsync<Guid>("select id from provider_reports order by id", transaction: tx)).ToArray();
        var inputSha = SHA256.HashData(Encoding.UTF8.GetBytes($"{position}|{string.Join(",", reportIds)}|{tolerance.Date.Ticks}"));

        if (await LoadRunAsync(conn, tx, "input_sha256 = @inputSha", new { inputSha }) is { } existing) return existing;

        var ours = (await conn.QueryAsync<LedgerRecord>(
            "select entry_id as EntryId, ref, hold_id as HoldId, amount, currency, occurred_at as OccurredAt, event_id as EventId from ledger_records", transaction: tx)).ToList();
        var theirs = (await conn.QueryAsync<ProviderRecord>(
            "select provider_ref as ProviderRef, our_ref as OurRef, amount, currency, booked_at as BookedAt from provider_records", transaction: tx)).ToList();

        var (matched, findings) = Matcher.Compare(ours, theirs, tolerance);
        var run = new Run(Guid.NewGuid(), DateTime.UtcNow, position, reportIds, matched, findings.Count);

        await conn.ExecuteAsync("""
            insert into runs (id, started_at, ledger_position, report_ids, input_sha256, matched, discrepancies)
            values (@Id, @StartedAt, @LedgerPosition, @reportIds, @inputSha, @Matched, @Discrepancies)
            """, new { run.Id, run.StartedAt, run.LedgerPosition, reportIds, inputSha, run.Matched, run.Discrepancies }, tx);
        await conn.ExecuteAsync("""
            insert into findings (run_id, category, ref, ledger_amount, provider_amount, detail)
            values (@runId, @category::finding_category, @Ref, @LedgerAmount, @ProviderAmount, @Detail)
            """, findings.Select(f => new { runId = run.Id, category = ToDb(f.Category), f.Ref, f.LedgerAmount, f.ProviderAmount, f.Detail }), tx);
        await tx.CommitAsync(ct);
        return run;
    }

    public async Task<Run> GetRunAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await LoadRunAsync(conn, null, "id = @id", new { id }) ?? throw new NotFoundException("run", id);
    }

    public async Task<Run?> LatestRunAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.QuerySingleOrDefaultAsync<Guid?>("select id from runs order by started_at desc limit 1");
        return id is null ? null : await LoadRunAsync(conn, null, "id = @id", new { id });
    }

    public async Task<IReadOnlyList<Run>> RunsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<(Guid id, DateTime started_at, long ledger_position, Guid[] report_ids, int matched, int discrepancies)>(
            "select id, started_at, ledger_position, report_ids, matched, discrepancies from runs order by started_at"))
            .Select(r => new Run(r.id, r.started_at, r.ledger_position, r.report_ids, r.matched, r.discrepancies)).ToList();
    }

    public async Task<IReadOnlyList<Finding>> FindingsAsync(Guid runId, FindingCategory? category = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        _ = await LoadRunAsync(conn, null, "id = @runId", new { runId }) ?? throw new NotFoundException("run", runId);
        var rows = await conn.QueryAsync<(string category, string @ref, long? ledger_amount, long? provider_amount, string detail)>("""
            select category::text, ref, ledger_amount, provider_amount, detail from findings
            where run_id = @runId and (@category is null or category = @category::finding_category)
            order by ref, category
            """, new { runId, category = category is { } c ? ToDb(c) : null });
        return rows.Select(r => new Finding(FromDb(r.category), r.@ref, r.ledger_amount, r.provider_amount, r.detail)).ToList();
    }

    /// <summary>For each run, how many discrepancies appeared and how many from the previous run went away.</summary>
    public async Task<IReadOnlyList<RunTransition>> TransitionsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<(Guid run_id, Guid? previous_id, long opened, long resolved)>(
            "select t.run_id, t.previous_id, t.opened, t.resolved from run_transitions t join runs r on r.id = t.run_id order by r.started_at"))
            .Select(t => new RunTransition(t.run_id, t.previous_id, (int)t.opened, (int)t.resolved)).ToList();
    }

    // ---------- internals ----------

    private static async Task<Run?> LoadRunAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string where, object args)
    {
        var r = await conn.QuerySingleOrDefaultAsync<(Guid id, DateTime started_at, long ledger_position, Guid[] report_ids, int matched, int discrepancies)>(
            $"select id, started_at, ledger_position, report_ids, matched, discrepancies from runs where {where}", args, tx);
        return r.id == default ? null : new Run(r.id, r.started_at, r.ledger_position, r.report_ids, r.matched, r.discrepancies);
    }

    private static string ToDb(FindingCategory c) => c switch
    {
        FindingCategory.AmountMismatch => "amount_mismatch",
        FindingCategory.CurrencyMismatch => "currency_mismatch",
        FindingCategory.DateMismatch => "date_mismatch",
        FindingCategory.MissingOnProvider => "missing_on_provider",
        FindingCategory.MissingInLedger => "missing_in_ledger",
        FindingCategory.DuplicateOnProvider => "duplicate_on_provider",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    private static FindingCategory FromDb(string s) => s switch
    {
        "amount_mismatch" => FindingCategory.AmountMismatch,
        "currency_mismatch" => FindingCategory.CurrencyMismatch,
        "date_mismatch" => FindingCategory.DateMismatch,
        "missing_on_provider" => FindingCategory.MissingOnProvider,
        "missing_in_ledger" => FindingCategory.MissingInLedger,
        "duplicate_on_provider" => FindingCategory.DuplicateOnProvider,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };
}
