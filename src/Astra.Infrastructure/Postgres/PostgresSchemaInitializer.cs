using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace Astra.Infrastructure.Postgres;

public sealed class PostgresSchemaInitializer(NpgsqlDataSource dataSource)
{
    private const long SchemaLockKey = 4_708_153_271_984_000;

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "sql");
        var paths = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.sql").Order(StringComparer.Ordinal).ToArray()
            : [];
        if (paths.Length == 0)
        {
            throw new FileNotFoundException("PostgreSQL schema files were not copied to the output directory.", directory);
        }

        var migrations = new List<Migration>(paths.Length);
        foreach (var path in paths)
        {
            var sql = await File.ReadAllTextAsync(path, cancellationToken);
            migrations.Add(new Migration(
                Path.GetFileName(path),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant(),
                sql));
        }

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(@LockKey);",
            new { LockKey = SchemaLockKey },
            cancellationToken: cancellationToken));
        try
        {
            await EnsureMigrationTableAsync(connection, cancellationToken);
            foreach (var migration in migrations)
            {
                await ApplyMigrationAsync(connection, migration, cancellationToken);
            }
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(@LockKey);",
                new { LockKey = SchemaLockKey },
                cancellationToken: CancellationToken.None));
        }
    }

    private static Task EnsureMigrationTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS astra_schema_migrations (
                name text PRIMARY KEY,
                checksum text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );
            """,
            cancellationToken: cancellationToken));

    private static async Task ApplyMigrationAsync(
        NpgsqlConnection connection,
        Migration migration,
        CancellationToken cancellationToken)
    {
        var appliedChecksum = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT checksum FROM astra_schema_migrations WHERE name = @Name;",
            new { migration.Name },
            cancellationToken: cancellationToken));
        if (appliedChecksum is not null)
        {
            if (!StringComparer.Ordinal.Equals(appliedChecksum, migration.Checksum))
            {
                throw new InvalidOperationException(
                    $"Applied PostgreSQL migration '{migration.Name}' has a different checksum.");
            }

            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            migration.Sql,
            transaction: transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO astra_schema_migrations(name, checksum)
            VALUES (@Name, @Checksum);
            """,
            new { migration.Name, migration.Checksum },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record Migration(string Name, string Checksum, string Sql);
}
