using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Recon.Core;

namespace Recon.Tests;

[Collection("postgres")]
public sealed class ApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly FakeLedgerSource _ledger = new();
    private WebApplicationFactory<Program> _factory = null!;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Recon", pg.ConnectionString);
            b.UseSetting("Recon:IntervalMinutes", "0");              // no scheduled runs under test
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<ILedgerSource>(_ledger)));
        });
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Sync_import_run_and_read_findings_over_http()
    {
        var http = _factory.CreateClient();
        _ledger.Clearing("clr-1", 700); _ledger.Clearing("clr-2", 300);

        var sync = await http.PostAsync("/ledger/sync", null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(2, (await sync.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetInt32());

        var import = await http.PostAsync("/reports?name=day-1.csv", new StringContent(Csv.Of(Csv.Row("P1", "clr-1", 700), Csv.Row("P2", "clr-2", 350))));
        Assert.Equal(HttpStatusCode.Created, import.StatusCode);

        var bad = await http.PostAsync("/reports", new StringContent("nope"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var run = await http.PostAsync("/runs", null);
        Assert.Equal(HttpStatusCode.Created, run.StatusCode);
        var body = (await run.Content.ReadFromJsonAsync<Run>(Json))!;
        Assert.Equal((1, 1), (body.Matched, body.Discrepancies));

        var findings = (await http.GetFromJsonAsync<List<Finding>>($"/runs/{body.Id}/findings?category=AmountMismatch", Json))!;
        Assert.Single(findings);
        Assert.Equal(("clr-2", 300L, 350L), (findings[0].Ref, findings[0].LedgerAmount, findings[0].ProviderAmount));
        Assert.Contains("\"category\":\"AmountMismatch\"", await http.GetStringAsync($"/runs/{body.Id}/findings"));

        var latest = (await http.GetFromJsonAsync<Run>("/runs/latest", Json))!;
        Assert.Equal(body.Id, latest.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/runs/{Guid.NewGuid()}")).StatusCode);
    }
}
