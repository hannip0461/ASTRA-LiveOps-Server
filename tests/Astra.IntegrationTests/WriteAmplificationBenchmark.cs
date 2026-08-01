using Astra.Contracts;
using Astra.Domain;
using Astra.Infrastructure.Postgres;
using Npgsql;
using System.Diagnostics;
using System.Globalization;

namespace Astra.IntegrationTests;

/// <summary>
/// Guards that account writes do not scale with the number of owned assets.
/// </summary>
public sealed class WriteAmplificationBenchmark
{
    private const int InventoryItems = 200;
    private const int Characters = 100;
    private const int Banners = 5;
    private const int SequentialCommands = 100;
    private const int ConcurrentWorkers = 8;
    private const int CommandsPerWorker = 25;

    [RequiresEnvironmentFact("ASTRA_RUN_BENCHMARK")]
    public async Task Measure()
    {
        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();

        var store = new PostgresPlayerAccountStore(dataSource);
        var processor = new PlayerAccountCommandProcessor(gachaRandomSource: new ZeroRandomSource());
        var playerId = await SeedPlayerAsync(store, processor);

        // Isolate per-transaction cost.
        var sequentialBefore = await ReadRowWritesAsync(dataSource);
        var sequential = Stopwatch.StartNew();
        for (var i = 0; i < SequentialCommands; i++)
        {
            await store.ExecuteAsync(
                playerId,
                state => processor.Grant(
                    state,
                    new GrantCurrencyCommand(CurrencyCode.Gold, 1, "bench", $"seq-{i}-{Guid.NewGuid():N}", "h")));
        }

        sequential.Stop();
        var sequentialRows = await ReadRowWritesAsync(dataSource) - sequentialBefore;

        // Commands for one player contend on the same row lock.
        var concurrentBefore = await ReadRowWritesAsync(dataSource);
        var concurrent = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, ConcurrentWorkers).Select(async worker =>
        {
            for (var i = 0; i < CommandsPerWorker; i++)
            {
                await store.ExecuteAsync(
                    playerId,
                    state => processor.Grant(
                        state,
                        new GrantCurrencyCommand(
                            CurrencyCode.Gold,
                            1,
                            "bench",
                            $"con-{worker}-{i}-{Guid.NewGuid():N}",
                            "h")));
            }
        }));
        concurrent.Stop();
        var concurrentRows = await ReadRowWritesAsync(dataSource) - concurrentBefore;

        var totalConcurrent = ConcurrentWorkers * CommandsPerWorker;
        Console.WriteLine("BENCH|assets|" + InventoryItems + "|" + Characters + "|" + Banners);
        Console.WriteLine(
            "BENCH|sequential|" + SequentialCommands +
            "|ms=" + sequential.ElapsedMilliseconds +
            "|perCmdMs=" + (sequential.Elapsed.TotalMilliseconds / SequentialCommands).ToString("F2", CultureInfo.InvariantCulture) +
            "|ins=" + sequentialRows.Inserted +
            "|upd=" + sequentialRows.Updated +
            "|rowsPerCmd=" + (sequentialRows.Total / (double)SequentialCommands).ToString("F1", CultureInfo.InvariantCulture));
        Console.WriteLine(
            "BENCH|concurrent|" + totalConcurrent +
            "|workers=" + ConcurrentWorkers +
            "|ms=" + concurrent.ElapsedMilliseconds +
            "|throughputPerSec=" + (totalConcurrent / concurrent.Elapsed.TotalSeconds).ToString("F1", CultureInfo.InvariantCulture) +
            "|ins=" + concurrentRows.Inserted +
            "|upd=" + concurrentRows.Updated +
            "|rowsPerCmd=" + (concurrentRows.Total / (double)totalConcurrent).ToString("F1", CultureInfo.InvariantCulture));

        // A currency grant must not rewrite unrelated owned assets.
        var rowsPerCommand = sequentialRows.Total / (double)SequentialCommands;
        Assert.InRange(rowsPerCommand, 1, 20);
        Assert.True(
            rowsPerCommand < (InventoryItems + Characters) / 10.0,
            $"A grant wrote {rowsPerCommand:F1} rows for a player holding " +
            $"{InventoryItems} items and {Characters} characters, so writes still scale with owned assets.");
    }

    private sealed class ZeroRandomSource : IGachaRandomSource
    {
        public int Next(int exclusiveUpperBound) => 0;
    }

    private static async Task<Guid> SeedPlayerAsync(
        PostgresPlayerAccountStore store,
        PlayerAccountCommandProcessor processor)
    {
        var playerId = Guid.NewGuid();
        await store.ExecuteAsync(
            playerId,
            state => processor.Grant(
                state,
                new GrantCurrencyCommand(CurrencyCode.Gold, 100_000_000, "seed", $"seed-g-{playerId:N}", "h")));
        await store.ExecuteAsync(
            playerId,
            state => processor.Grant(
                state,
                new GrantCurrencyCommand(CurrencyCode.Elif, 100_000_000, "seed", $"seed-e-{playerId:N}", "h")));
        await store.ExecuteAsync(
            playerId,
            state => processor.Grant(
                state,
                new GrantCurrencyCommand(CurrencyCode.StarCandy, 1_000, "seed", $"seed-s-{playerId:N}", "h")));

        // Seed inventory, characters and pity state through the domain path.
        for (var banner = 0; banner < Banners; banner++)
        {
            var perBanner = Math.Max(InventoryItems, Characters) / Banners;
            for (var draw = 0; draw < perBanner; draw++)
            {
                var index = banner * perBanner + draw;
                await store.ExecuteAsync(
                    playerId,
                    state => processor.DrawGacha(state, SeedDraw($"bench-banner-{banner}", index)));
            }
        }

        return playerId;
    }

    private static DrawGachaCommand SeedDraw(string bannerId, int index) =>
        new(
            bannerId,
            "content-bench",
            "checksum-bench",
            CurrencyCode.Elif,
            1,
            1,
            [
                new GachaRewardPoolEntryDto(
                    GachaRewardKind.Character,
                    $"bench-char-{index}",
                    1,
                    3,
                    100,
                    true,
                    $"bench-item-{index}",
                    1)
            ],
            90,
            $"bench-draw-{index}-{bannerId}",
            "h");

    /// <summary>Cumulative row writes, compared as workload deltas.</summary>
    private readonly record struct RowWrites(long Inserted, long Updated)
    {
        public long Total => Inserted + Updated;

        public static RowWrites operator -(RowWrites left, RowWrites right) =>
            new(left.Inserted - right.Inserted, left.Updated - right.Updated);
    }

    private static async Task<RowWrites> ReadRowWritesAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(n_tup_ins), 0), COALESCE(SUM(n_tup_upd), 0)
            FROM pg_stat_user_tables;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new RowWrites(reader.GetInt64(0), reader.GetInt64(1));
    }
}
