using Recon.Core;

namespace Recon.Api;

/// <summary>Every interval: pull what is new from the ledger, then compare. A run over unchanged inputs is a no-op.</summary>
public sealed class ReconWorker(ReconService recon, IConfiguration config, ILogger<ReconWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var minutes = config.GetValue("Recon:IntervalMinutes", 60);
        if (minutes <= 0) { log.LogInformation("scheduled reconciliation disabled"); return; }
        var tolerance = new MatchTolerance(TimeSpan.FromHours(config.GetValue("Recon:DateToleranceHours", 48)));
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));

        do
        {
            try
            {
                var added = await recon.SyncLedgerAsync(ct);
                var run = await recon.RunAsync(tolerance, ct);
                log.LogInformation("synced {Added} clearings; run {Run}: {Matched} matched, {Discrepancies} discrepancies", added, run.Id, run.Matched, run.Discrepancies);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "scheduled reconciliation failed; will retry next interval");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
