using System.Data;
using System.Text.Json;
using Astra.Contracts;
using Astra.Domain;
using Dapper;
using Npgsql;

namespace Astra.Infrastructure.Postgres;

public sealed class PostgresContentSnapshotStore(NpgsqlDataSource dataSource) : IContentSnapshotStore
{
    public const string ContentChangedChannel = "astra_content_changed";
    private const long LifecycleLockKey = 4_708_153_271_984_001;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ContentSnapshotDto?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT snapshot.version AS "Version",
                   snapshot.checksum AS "Checksum",
                   snapshot.snapshot_json::text AS "SnapshotJson"
            FROM active_content active
            JOIN content_snapshots snapshot ON snapshot.version = active.version
            WHERE active.singleton_id = 1;
            """;

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return row is null ? null : Deserialize(row);
    }

    public async Task<ContentSnapshotDto> PublishAsync(
        ContentSnapshotDto snapshot,
        CancellationToken cancellationToken = default)
    {
        const string insertSql = """
            INSERT INTO content_snapshots(version, checksum, snapshot_json, published_at)
            VALUES (@Version, @Checksum, CAST(@SnapshotJson AS jsonb), @PublishedAtUtc)
            ON CONFLICT (version) DO NOTHING;
            """;
        const string selectSql = """
            SELECT version AS "Version",
                   checksum AS "Checksum",
                   snapshot_json::text AS "SnapshotJson"
            FROM content_snapshots
            WHERE version = @Version
            FOR UPDATE;
            """;

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLifecycleLockAsync(connection, transaction, cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            insertSql,
            new
            {
                snapshot.Version,
                snapshot.Checksum,
                SnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions),
                snapshot.PublishedAtUtc
            },
            transaction,
            cancellationToken: cancellationToken));

        var row = await connection.QuerySingleAsync<SnapshotRow>(new CommandDefinition(
            selectSql,
            new { snapshot.Version },
            transaction,
            cancellationToken: cancellationToken));
        if (!StringComparer.Ordinal.Equals(row.Checksum, snapshot.Checksum))
        {
            throw new ContentVersionConflictException(
                $"Content version '{snapshot.Version}' already exists with a different checksum.");
        }

        if (inserted == 0)
        {
            var activeVersion = await GetActiveVersionAsync(connection, transaction, cancellationToken);
            if (!StringComparer.Ordinal.Equals(activeVersion, snapshot.Version))
            {
                throw new ContentVersionInactiveException(
                    $"Content version '{snapshot.Version}' already exists but is not active; use rollback to reactivate it.");
            }

            await transaction.CommitAsync(cancellationToken);
            return Deserialize(row);
        }

        await ActivateAsync(connection, transaction, snapshot.Version, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Deserialize(row);
    }

    public async Task<ContentSnapshotDto?> ActivateAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        const string selectSql = """
            SELECT version AS "Version",
                   checksum AS "Checksum",
                   snapshot_json::text AS "SnapshotJson"
            FROM content_snapshots
            WHERE version = @Version;
            """;

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireLifecycleLockAsync(connection, transaction, cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(
            selectSql,
            new { Version = version },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        var activeVersion = await GetActiveVersionAsync(connection, transaction, cancellationToken);
        if (StringComparer.Ordinal.Equals(activeVersion, row.Version))
        {
            await transaction.CommitAsync(cancellationToken);
            return Deserialize(row);
        }

        await ActivateAsync(connection, transaction, row.Version, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Deserialize(row);
    }

    private static Task AcquireLifecycleLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@LockKey);",
            new { LockKey = LifecycleLockKey },
            transaction,
            cancellationToken: cancellationToken));

    private static Task<string?> GetActiveVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT version FROM active_content WHERE singleton_id = 1;",
            transaction: transaction,
            cancellationToken: cancellationToken));

    private static async Task ActivateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string version,
        CancellationToken cancellationToken)
    {
        const string activateSql = """
            INSERT INTO active_content(singleton_id, version, generation, activated_at)
            VALUES (1, @Version, 1, now())
            ON CONFLICT (singleton_id) DO UPDATE
            SET version = EXCLUDED.version,
                generation = active_content.generation + 1,
                activated_at = now();

            SELECT pg_notify(@Channel, @Version);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            activateSql,
            new { Version = version, Channel = ContentChangedChannel },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static ContentSnapshotDto Deserialize(SnapshotRow row)
    {
        var snapshot = JsonSerializer.Deserialize<ContentSnapshotDto>(row.SnapshotJson, JsonOptions)
            ?? throw new InvalidDataException($"Content snapshot JSON is empty for version '{row.Version}'.");
        if (!StringComparer.Ordinal.Equals(snapshot.Version, row.Version) ||
            !StringComparer.Ordinal.Equals(snapshot.Checksum, row.Checksum))
        {
            throw new InvalidDataException($"Content snapshot metadata mismatch for version '{row.Version}'.");
        }

        return snapshot;
    }

    private sealed class SnapshotRow
    {
        public string Version { get; init; } = "";

        public string Checksum { get; init; } = "";

        public string SnapshotJson { get; init; } = "";
    }
}
