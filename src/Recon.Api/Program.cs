using System.Text.Json.Serialization;
using Npgsql;
using Recon.Api;
using Recon.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Recon") ?? throw new InvalidOperationException("ConnectionStrings:Recon is not configured")));
builder.Services.AddHttpClient<ILedgerSource, LedgerHttpSource>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Ledger:BaseUrl"] ?? throw new InvalidOperationException("Ledger:BaseUrl is not configured")));
builder.Services.AddSingleton<ReconService>();
builder.Services.AddHostedService<ReconWorker>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
await Migrator.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ReconException e)
    {
        var (status, code) = e switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "not_found"),
            InvalidReportException => (StatusCodes.Status400BadRequest, "invalid_report"),
            _ => (StatusCodes.Status500InternalServerError, "recon_error"),
        };
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error = code, message = e.Message });
    }
});

MatchTolerance Tolerance(int? hours) =>
    new(TimeSpan.FromHours(hours ?? app.Configuration.GetValue("Recon:DateToleranceHours", 48)));

app.MapPost("/ledger/sync", async (ReconService recon) => new { added = await recon.SyncLedgerAsync() });

app.MapPost("/reports", async (HttpRequest req, string? name, ReconService recon) =>
{
    using var reader = new StreamReader(req.Body);
    var report = await recon.ImportReportAsync(name ?? "report.csv", await reader.ReadToEndAsync());
    return Results.Created($"/reports/{report.Id}", report);
});
app.MapGet("/reports", async (ReconService recon) => await recon.ReportsAsync());

app.MapPost("/runs", async (int? dateToleranceHours, ReconService recon) =>
{
    var run = await recon.RunAsync(Tolerance(dateToleranceHours));
    return Results.Created($"/runs/{run.Id}", run);
});
app.MapGet("/runs", async (ReconService recon) => await recon.RunsAsync());
app.MapGet("/runs/latest", async (ReconService recon) => await recon.LatestRunAsync() is { } r ? Results.Ok(r) : Results.NotFound());
app.MapGet("/runs/transitions", async (ReconService recon) => await recon.TransitionsAsync());
app.MapGet("/runs/{id:guid}", async (Guid id, ReconService recon) => await recon.GetRunAsync(id));
app.MapGet("/runs/{id:guid}/findings", async (Guid id, FindingCategory? category, ReconService recon) => await recon.FindingsAsync(id, category));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
