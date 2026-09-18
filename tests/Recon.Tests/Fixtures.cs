using Dapper;
using Npgsql;
using Recon.Core;
using Testcontainers.PostgreSql;

namespace Recon.Tests;

/// <summary>A ledger outbox in memory: event ids with, for clearings, the record they carry.</summary>
public sealed class FakeLedgerSource : ILedgerSource
{
    private readonly List<(long EventId, LedgerRecord? Record)> _events = [];
    private long _nextId = 1;

    /// <summary>An event that is not a clearing (an account, a hold, a top-up). Advances the cursor, adds nothing.</summary>
    public void Noise() => _events.Add((_nextId++, null));

    public LedgerRecord Clearing(string @ref, long amount, string currency = "EUR", DateTime? at = null)
    {
        var id = _nextId++;
        var r = new LedgerRecord(Guid.NewGuid(), @ref, Guid.NewGuid(), amount, currency, at ?? DateTime.UtcNow, id);
        _events.Add((id, r));
        return r;
    }

    public Task<(IReadOnlyList<LedgerRecord> Records, long Next)> ReadClearingsAsync(long after, int limit, CancellationToken ct = default)
    {
        var page = _events.Where(e => e.EventId > after).Take(limit).ToList();
        var records = page.Where(e => e.Record is not null).Select(e => e.Record!).ToList();
        return Task.FromResult<(IReadOnlyList<LedgerRecord>, long)>((records, page.Count == 0 ? after : page[^1].EventId));
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public NpgsqlDataSource Db { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Db = NpgsqlDataSource.Create(ConnectionString);
        await Migrator.ApplyAsync(Db);
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>A fresh schema per test class that needs one: runs are global, so classes that create runs must not share.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await Db.OpenConnectionAsync();
        await conn.ExecuteAsync("drop schema public cascade; create schema public;");
        await Migrator.ApplyAsync(Db);
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public static class Csv
{
    public const string Header = "provider_ref,our_ref,amount,currency,booked_at";
    public static string Of(params string[] rows) => Header + "\n" + string.Join("\n", rows) + "\n";
    public static string Row(string providerRef, string ourRef, long amount, string currency = "EUR", DateTime? at = null)
        => $"{providerRef},{ourRef},{amount},{currency},{(at ?? DateTime.UtcNow):O}";
}
