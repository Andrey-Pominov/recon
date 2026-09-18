using Dapper;
using Npgsql;
using Recon.Core;

namespace Recon.Tests;

/// <summary>The service against a real PostgreSQL, with the ledger's outbox faked in memory. Each test starts from an empty schema.</summary>
[Collection("postgres")]
public sealed class ReconServiceTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly FakeLedgerSource _ledger = new();
    private ReconService _recon = null!;

    public async Task InitializeAsync() { await pg.ResetAsync(); _recon = new ReconService(pg.Db, _ledger); }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sync_keeps_only_clearings_advances_the_cursor_and_is_a_no_op_afterwards()
    {
        _ledger.Noise(); _ledger.Noise();
        _ledger.Clearing("clr-1", 700);
        _ledger.Noise();
        _ledger.Clearing("clr-2", 300);

        Assert.Equal(2, await _recon.SyncLedgerAsync());
        Assert.Equal(0, await _recon.SyncLedgerAsync());

        await using var conn = await pg.Db.OpenConnectionAsync();
        Assert.Equal(5, await conn.ExecuteScalarAsync<long>("select position from ledger_cursor"));
        Assert.Equal(2, await conn.ExecuteScalarAsync<long>("select count(*) from ledger_records"));

        _ledger.Clearing("clr-3", 100);
        Assert.Equal(1, await _recon.SyncLedgerAsync());
    }

    [Fact]
    public async Task The_same_file_imported_twice_is_one_report()
    {
        var csv = Csv.Of(Csv.Row("P1", "clr-1", 700));
        var a = await _recon.ImportReportAsync("monday.csv", csv);
        var b = await _recon.ImportReportAsync("monday-again.csv", csv);
        Assert.Equal(a.Id, b.Id);
        Assert.Single(await _recon.ReportsAsync());
    }

    [Fact]
    public async Task A_run_records_its_findings_and_the_same_inputs_return_the_same_run()
    {
        _ledger.Clearing("clr-1", 700); _ledger.Clearing("clr-2", 300); _ledger.Clearing("clr-3", 500);
        await _recon.SyncLedgerAsync();
        await _recon.ImportReportAsync("r1", Csv.Of(Csv.Row("P1", "clr-1", 700), Csv.Row("P2", "clr-2", 330), Csv.Row("P9", "clr-9", 10)));

        var run = await _recon.RunAsync();
        Assert.Equal(1, run.Matched);
        Assert.Equal(3, run.Discrepancies);
        var findings = await _recon.FindingsAsync(run.Id);
        Assert.Equal(
            [(FindingCategory.AmountMismatch, "clr-2"), (FindingCategory.MissingOnProvider, "clr-3"), (FindingCategory.MissingInLedger, "clr-9")],
            findings.Select(f => (f.Category, f.Ref)));
        Assert.Single(await _recon.FindingsAsync(run.Id, FindingCategory.AmountMismatch));

        var again = await _recon.RunAsync();
        Assert.Equal(run.Id, again.Id);
        Assert.Single(await _recon.RunsAsync());
    }

    [Fact]
    public async Task A_late_report_resolves_a_discrepancy_in_the_next_run_and_the_transition_says_so()
    {
        _ledger.Clearing("clr-1", 700); _ledger.Clearing("clr-2", 300);
        await _recon.SyncLedgerAsync();
        await _recon.ImportReportAsync("day-1", Csv.Of(Csv.Row("P1", "clr-1", 700)));
        var first = await _recon.RunAsync();
        Assert.Equal(1, first.Discrepancies);                                              // clr-2 not yet reported

        await _recon.ImportReportAsync("day-2", Csv.Of(Csv.Row("P2", "clr-2", 300)));   // it arrives a day later
        var second = await _recon.RunAsync();
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(0, second.Discrepancies);

        var transitions = await _recon.TransitionsAsync();
        Assert.Equal(2, transitions.Count);
        Assert.Equal((null, 1, 0), (transitions[0].PreviousId, transitions[0].Opened, transitions[0].Resolved));
        Assert.Equal((first.Id, 0, 1), (transitions[1].PreviousId, transitions[1].Opened, transitions[1].Resolved));
    }

    [Fact]
    public async Task Changing_the_tolerance_is_a_different_run()
    {
        var t0 = DateTime.UtcNow;
        _ledger.Clearing("clr-1", 700, at: t0);
        await _recon.SyncLedgerAsync();
        await _recon.ImportReportAsync("r", Csv.Of(Csv.Row("P1", "clr-1", 700, at: t0.AddDays(3))));

        var strict = await _recon.RunAsync(new MatchTolerance(TimeSpan.FromDays(2)));
        var lenient = await _recon.RunAsync(new MatchTolerance(TimeSpan.FromDays(5)));
        Assert.Equal(1, strict.Discrepancies);
        Assert.Equal(0, lenient.Discrepancies);
        Assert.NotEqual(strict.Id, lenient.Id);
    }

    [Fact]
    public async Task Concurrent_runs_over_the_same_inputs_produce_one_row()
    {
        _ledger.Clearing("clr-1", 700);
        await _recon.SyncLedgerAsync();
        await _recon.ImportReportAsync("r", Csv.Of(Csv.Row("P1", "clr-1", 700)));

        var runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _recon.RunAsync()));
        Assert.Single(runs.Select(r => r.Id).Distinct());
        Assert.Single(await _recon.RunsAsync());
    }

    [Fact]
    public async Task Everything_written_is_immutable_at_the_database_level()
    {
        _ledger.Clearing("clr-1", 700);
        await _recon.SyncLedgerAsync();
        await _recon.ImportReportAsync("r", Csv.Of(Csv.Row("P1", "clr-1", 999)));
        var run = await _recon.RunAsync();

        await using var conn = await pg.Db.OpenConnectionAsync();
        foreach (var sql in new[]
        {
            "update ledger_records set amount = 1",
            "delete from provider_records",
            "update runs set discrepancies = 0 where id = @id",
            "delete from findings where run_id = @id",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, new { id = run.Id }));
            Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
        }
    }
}
