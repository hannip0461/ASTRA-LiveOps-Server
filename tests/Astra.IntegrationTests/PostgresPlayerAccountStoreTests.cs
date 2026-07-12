using Astra.Contracts;
using Astra.Domain;
using Astra.Infrastructure.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Diagnostics;

namespace Astra.IntegrationTests;

public sealed class PostgresPlayerAccountStoreTests
{
    [Fact]
    public async Task ExecuteAsync_WithPostgreSql_CommitsStateAndIdempotencyTogether()
    {
        if (!ShouldRunPostgresTests())
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        await ClearPendingOutboxAsync(dataSource);

        var playerId = Guid.NewGuid();
        var store = new PostgresPlayerAccountStore(dataSource);
        var processor = new PlayerAccountCommandProcessor(gachaRandomSource: new ZeroRandomSource());
        var draw = new DrawGachaCommand(
            "pickup-fatima",
            "content-2026-07-09-a",
            "checksum-a",
            CurrencyCode.Elif,
            100,
            1,
            [
                new GachaRewardPoolEntryDto(
                    GachaRewardKind.Character,
                    "char-001",
                    1,
                    2,
                    90,
                    false,
                    "memory-char-001",
                    5),
                new GachaRewardPoolEntryDto(
                    GachaRewardKind.Character,
                    "char-pickup",
                    1,
                    3,
                    10,
                    true,
                    "memory-char-pickup",
                    20)
            ],
            90,
            "draw-1",
            "draw-hash-1");

        await store.ExecuteAsync(
            playerId,
            state => processor.Grant(
                state,
                new GrantCurrencyCommand(CurrencyCode.Elif, 500, "seed", "grant-1", "grant-hash")));

        var first = await store.ExecuteAsync(
            playerId,
            state => processor.DrawGacha(state, draw));

        var replay = await store.ExecuteAsync(
            playerId,
            state => processor.DrawGacha(state, draw));

        var snapshot = await store.ReadSnapshotAsync(playerId);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.ResponseBody, replay.ResponseBody);
        Assert.Equal(400, snapshot.Balances.Single(x => x.Currency == CurrencyCode.Elif).Amount);
        Assert.Equal(2, snapshot.LedgerVersion);
        Assert.Single(snapshot.Characters);
        Assert.Equal(1, snapshot.PityByBanner["pickup-fatima"]);
        Assert.Equal(1, await CountGachaHistoryAsync(dataSource, playerId, "draw-1"));

        var mailStore = new PostgresMailStore(dataSource);
        var mail = await mailStore.CreateIncidentMailAsync(new CreateIncidentMailCommand(
            "incident-001",
            $"mail-{Guid.NewGuid():N}",
            "Compensation",
            "Bad gacha table compensation",
            [playerId],
            [new MailRewardDto(CurrencyCode.Elif, 300)],
            "bad-gacha-table"));

        var mailClaim = new ClaimMailCommand(mail.MailId, "mail-claim-1", "mail-hash-1");
        var firstClaim = await store.ExecuteAsync(
            playerId,
            state => processor.ClaimMail(state, mailClaim, mail));
        var claimReplay = await store.ExecuteAsync(
            playerId,
            state => processor.ClaimMail(state, mailClaim, mail));
        var delayedDrawReplay = await store.ExecuteAsync(
            playerId,
            state => processor.DrawGacha(state, draw));

        var afterClaim = await store.ReadSnapshotAsync(playerId);

        Assert.False(firstClaim.Replayed);
        Assert.True(claimReplay.Replayed);
        Assert.Equal(firstClaim.ResponseBody, claimReplay.ResponseBody);
        Assert.True(delayedDrawReplay.Replayed);
        Assert.Equal(2, delayedDrawReplay.Snapshot.LedgerVersion);
        Assert.Equal(
            400,
            delayedDrawReplay.Snapshot.Balances.Single(balance => balance.Currency == CurrencyCode.Elif).Amount);
        Assert.Equal(700, afterClaim.Balances.Single(x => x.Currency == CurrencyCode.Elif).Amount);
        Assert.Equal(3, afterClaim.LedgerVersion);

        var outboxStore = new PostgresOutboxEventStore(dataSource);
        var leased = await outboxStore.LeaseBatchAsync("test-worker", 10, TimeSpan.FromSeconds(30));
        var playerEvents = leased.Where(outboxEvent => outboxEvent.AggregateId == playerId).ToArray();
        var retryEvent = playerEvents.Single(outboxEvent => outboxEvent.EventType == "mail.claimed");

        Assert.Contains(playerEvents, outboxEvent => outboxEvent.EventType == "wallet.currency_granted");
        Assert.Contains(playerEvents, outboxEvent => outboxEvent.EventType == "gacha.draw_completed");
        var grantEvent = playerEvents.Single(outboxEvent => outboxEvent.EventType == "wallet.currency_granted");
        Assert.Contains("\"schemaVersion\":1", grantEvent.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"balances\"", grantEvent.Payload, StringComparison.Ordinal);

        foreach (var outboxEvent in playerEvents.Where(outboxEvent => outboxEvent.EventId != retryEvent.EventId))
        {
            await outboxStore.MarkPublishedAsync(outboxEvent.EventId, "test-worker");
        }

        await outboxStore.MarkFailedAsync(retryEvent.EventId, "test-worker", "transient test failure", TimeSpan.Zero);

        var retried = await outboxStore.LeaseBatchAsync("test-worker", 10, TimeSpan.FromSeconds(30));
        var retriedEvent = retried.Single(outboxEvent => outboxEvent.EventId == retryEvent.EventId);

        Assert.Equal(1, retriedEvent.Attempts);
        await outboxStore.MarkPublishedAsync(retriedEvent.EventId, "test-worker");
    }

    private static bool ShouldRunPostgresTests() =>
        string.Equals(Environment.GetEnvironmentVariable("ASTRA_RUN_POSTGRES_TESTS"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task Outbox_WithMaxAttempts_MarksDeadLetterTerminal()
    {
        if (!ShouldRunPostgresTests())
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        await ClearPendingOutboxAsync(dataSource);

        var eventId = await InsertOutboxEventAsync(dataSource, maxAttempts: 1);
        var store = new PostgresOutboxEventStore(dataSource);
        var leased = await store.LeaseBatchAsync("terminal-test-worker", 1, TimeSpan.FromSeconds(30));

        Assert.Equal(eventId, leased.Single().EventId);

        await store.MarkFailedAsync(eventId, "terminal-test-worker", "terminal failure", TimeSpan.Zero);

        var status = await ReadOutboxStatusAsync(dataSource, eventId);
        Assert.Equal("dead_letter", status.Status);
        Assert.Equal(1, status.Attempts);
        Assert.Equal("terminal failure", status.LastError);
        await DeleteOutboxForCleanupAsync(dataSource, eventId);
    }

    [Fact]
    public async Task Gacha_AfterIdempotencyExpiry_AllowsKeyReuseAndSecondHistory()
    {
        if (!ShouldRunPostgresTests())
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();

        var playerId = Guid.NewGuid();
        var store = new PostgresPlayerAccountStore(dataSource);
        var processor = new PlayerAccountCommandProcessor(gachaRandomSource: new ZeroRandomSource());
        var draw = new DrawGachaCommand(
            "pickup-a",
            "content-a",
            "checksum-a",
            CurrencyCode.Elif,
            100,
            1,
            [
                new GachaRewardPoolEntryDto(
                    GachaRewardKind.Character,
                    "char-pickup",
                    1,
                    3,
                    100,
                    true,
                    "memory-char-pickup",
                    20)
            ],
            90,
            "draw-expiring",
            "draw-hash");

        await store.ExecuteAsync(
            playerId,
            state => processor.Grant(
                state,
                new GrantCurrencyCommand(CurrencyCode.Elif, 500, "seed", "grant-expiring", "grant-hash")));
        await store.ExecuteAsync(playerId, state => processor.DrawGacha(state, draw));
        await ExpireIdempotencyAsync(dataSource, playerId, draw.IdempotencyKey);

        var reused = await store.ExecuteAsync(playerId, state => processor.DrawGacha(state, draw));
        var snapshot = await store.ReadSnapshotAsync(playerId);

        Assert.False(reused.Replayed);
        Assert.Equal(300, snapshot.Balances.Single(balance => balance.Currency == CurrencyCode.Elif).Amount);
        Assert.Equal(2, await CountGachaHistoryAsync(dataSource, playerId, draw.IdempotencyKey));
    }

    [Fact]
    public async Task Worker_WithPostgreSql_PublishesLeasedEvent()
    {
        if (!ShouldRunPostgresTests())
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        await ClearPendingOutboxAsync(dataSource);

        var eventId = await InsertOutboxEventAsync(dataSource, maxAttempts: 3);
        var worker = new global::Astra.Worker.Worker(
            new PostgresOutboxEventStore(dataSource),
            new NoOpOutboxHandler(),
            new global::Astra.Worker.OutboxWorkerOptions
            {
                WorkerId = "e2e-worker",
                BatchSize = 1,
                PollInterval = TimeSpan.FromMilliseconds(25),
                LeaseDuration = TimeSpan.FromSeconds(5)
            },
            NullLogger<global::Astra.Worker.Worker>.Instance);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(timeout.Token);
        var status = await WaitForOutboxStatusAsync(dataSource, eventId, "published", timeout.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("published", status.Status);
        Assert.Equal(0, status.Attempts);
        await DeleteOutboxForCleanupAsync(dataSource, eventId);
    }

    [Fact]
    public async Task OperationalConsumer_DuplicateDelivery_WritesOneSanitizedProjection()
    {
        if (!ShouldRunPostgresTests())
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();

        var outboxEvent = new OutboxEventRecord(
            Guid.NewGuid(),
            "wallet.currency_granted",
            Guid.NewGuid(),
            "sensitive-idempotency-key",
            """
            {
              "schemaVersion": 1,
              "currency": 1,
              "amount": 100,
              "balanceAfter": 100,
              "ledgerVersion": 4,
              "secretToken": "must-not-be-copied"
            }
            """,
            0,
            5);
        var handler = new global::Astra.Worker.PostgresOperationalEventHandler(dataSource);

        await handler.HandleAsync(outboxEvent);
        await handler.HandleAsync(outboxEvent);

        var delivery = await ReadDeliveryAsync(dataSource, outboxEvent.EventId);
        Assert.Equal(1, delivery.Count);
        Assert.Contains("\"ledgerVersion\": 4", delivery.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("secretToken", delivery.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-idempotency-key", delivery.Summary, StringComparison.Ordinal);
        await DeleteDeliveryForCleanupAsync(dataSource, outboxEvent.EventId);
    }

    [Fact]
    public async Task PersistenceCleanup_DeletesOnlyExpiredPublishedAndOrphanDeliveries()
    {
        if (!ShouldRunPostgresTests())
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();

        var now = DateTimeOffset.UtcNow;
        var expiredPublished = Guid.NewGuid();
        var recentPublished = Guid.NewGuid();
        var oldDeadLetter = Guid.NewGuid();
        var oldPending = Guid.NewGuid();
        var orphanDelivery = Guid.NewGuid();
        var allEventIds = new[]
        {
            expiredPublished,
            recentPublished,
            oldDeadLetter,
            oldPending,
            orphanDelivery
        };

        try
        {
            await InsertOutboxForRetentionAsync(dataSource, expiredPublished, "published", now.AddDays(-8));
            await InsertOutboxForRetentionAsync(dataSource, recentPublished, "published", now.AddHours(-1));
            await InsertOutboxForRetentionAsync(dataSource, oldDeadLetter, "dead_letter", now.AddDays(-40));
            await InsertOutboxForRetentionAsync(dataSource, oldPending, "pending", now.AddDays(-40));
            await InsertDeliveryForRetentionAsync(dataSource, expiredPublished, now.AddDays(-8));
            await InsertDeliveryForRetentionAsync(dataSource, recentPublished, now.AddHours(-1));
            await InsertDeliveryForRetentionAsync(dataSource, orphanDelivery, now.AddDays(-31));

            var result = await new PostgresPersistenceMaintenanceStore(dataSource).CleanupAsync(
                now.AddDays(-7),
                now.AddDays(-30),
                DateTimeOffset.MinValue,
                100,
                TimeSpan.FromSeconds(5));

            Assert.Equal(1, result.PublishedEventsDeleted);
            Assert.Equal(2, result.DeliveriesDeleted);
            Assert.Equal(0, result.IdempotencyRequestsDeleted);
            Assert.False(await OutboxExistsAsync(dataSource, expiredPublished));
            Assert.False(await DeliveryExistsAsync(dataSource, expiredPublished));
            Assert.False(await DeliveryExistsAsync(dataSource, orphanDelivery));
            Assert.True(await OutboxExistsAsync(dataSource, recentPublished));
            Assert.True(await DeliveryExistsAsync(dataSource, recentPublished));
            Assert.True(await OutboxExistsAsync(dataSource, oldDeadLetter));
            Assert.True(await OutboxExistsAsync(dataSource, oldPending));
        }
        finally
        {
            await DeleteRetentionRecordsAsync(dataSource, allEventIds);
        }
    }

    [Fact]
    public async Task PersistenceCleanup_ConcurrentCallsRespectIdempotencyBatchAndCutoff()
    {
        if (!ShouldRunPostgresTests())
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        var playerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var expiredAt = DateTimeOffset.UnixEpoch;
        var expiredCutoff = expiredAt.AddDays(1);

        try
        {
            await InsertIdempotencyRowsAsync(dataSource, playerId, "expired-load-", 25, expiredAt);
            await InsertIdempotencyRowsAsync(dataSource, playerId, "active-load-", 3, now.AddHours(1));
            var store = new PostgresPersistenceMaintenanceStore(dataSource);

            var results = await Task.WhenAll(
                store.CleanupAsync(
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MinValue,
                    expiredCutoff,
                    10,
                    TimeSpan.FromSeconds(5)),
                store.CleanupAsync(
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MinValue,
                    expiredCutoff,
                    10,
                    TimeSpan.FromSeconds(5)));

            Assert.All(results, result => Assert.InRange(result.IdempotencyRequestsDeleted, 0, 10));
            Assert.Equal(20, results.Sum(result => result.IdempotencyRequestsDeleted));
            Assert.Equal(5, await CountIdempotencyRowsAsync(dataSource, playerId, "expired-load-%"));
            Assert.Equal(3, await CountIdempotencyRowsAsync(dataSource, playerId, "active-load-%"));

            var final = await store.CleanupAsync(
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue,
                expiredCutoff,
                10,
                TimeSpan.FromSeconds(5));
            Assert.Equal(5, final.IdempotencyRequestsDeleted);
            Assert.Equal(0, await CountIdempotencyRowsAsync(dataSource, playerId, "expired-load-%"));
            Assert.Equal(3, await CountIdempotencyRowsAsync(dataSource, playerId, "active-load-%"));
        }
        finally
        {
            await DeletePlayerForCleanupAsync(dataSource, playerId);
        }
    }

    [Fact]
    public async Task Worker_UnsupportedEvent_DeadLettersAndAllowsManualReplay()
    {
        if (!ShouldRunPostgresTests())
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        await ClearPendingOutboxAsync(dataSource);

        var eventId = await InsertOutboxEventAsync(dataSource, maxAttempts: 1);
        var worker = new global::Astra.Worker.Worker(
            new PostgresOutboxEventStore(dataSource),
            new global::Astra.Worker.PostgresOperationalEventHandler(dataSource),
            new global::Astra.Worker.OutboxWorkerOptions
            {
                WorkerId = "dead-letter-e2e-worker",
                BatchSize = 1,
                PollInterval = TimeSpan.FromMilliseconds(25),
                LeaseDuration = TimeSpan.FromSeconds(5),
                MaxConcurrency = 1
            },
            NullLogger<global::Astra.Worker.Worker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(timeout.Token);
        var terminal = await WaitForOutboxStatusAsync(dataSource, eventId, "dead_letter", timeout.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("outbox_event_unsupported", terminal.LastError);
        var operations = new PostgresOutboxOperationsStore(dataSource);
        var deadLetter = Assert.Single(
            await operations.ListDeadLettersAsync(200),
            entry => entry.EventId == eventId);
        Assert.Equal(1, deadLetter.Attempts);

        var replayed = await operations.ReplayDeadLetterAsync(eventId);
        Assert.NotNull(replayed);
        Assert.Equal("pending", replayed.Status);
        Assert.Equal(1, replayed.ManualReplayCount);

        await DeleteOutboxForCleanupAsync(dataSource, eventId);
    }

    [Fact]
    public async Task Worker_HardKillAfterConsumerCommit_RecoversWithoutDuplicateDelivery()
    {
        if (!ShouldRunPostgresTests())
        {
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresSchemaInitializer(dataSource).ApplyAsync();
        await ClearPendingOutboxAsync(dataSource);
        var eventId = await InsertCrashRecoveryEventAsync(dataSource);
        Process? harness = null;

        try
        {
            var harnessDll = Path.Combine(
                FindRepositoryRoot(),
                "tests",
                "Astra.WorkerCrashHarness",
                "bin",
                CurrentBuildConfiguration(),
                "net10.0",
                "Astra.WorkerCrashHarness.dll");
            Assert.True(File.Exists(harnessDll), $"Crash harness was not built: {harnessDll}");

            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(harnessDll);
            startInfo.Environment["ASTRA_POSTGRES_CONNECTION"] = connectionString;
            startInfo.Environment["ASTRA_CRASH_EVENT_ID"] = eventId.ToString("D");
            harness = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the crash harness.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitForDeliveryCountAsync(dataSource, eventId, 1, harness, timeout.Token);
            Assert.False(harness.HasExited);

            harness.Kill(entireProcessTree: true);
            await harness.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, harness.ExitCode);

            var worker = new global::Astra.Worker.Worker(
                new PostgresOutboxEventStore(dataSource),
                new global::Astra.Worker.PostgresOperationalEventHandler(dataSource),
                new global::Astra.Worker.OutboxWorkerOptions
                {
                    WorkerId = "hard-kill-recovery-worker",
                    BatchSize = 1,
                    PollInterval = TimeSpan.FromMilliseconds(25),
                    LeaseDuration = TimeSpan.FromSeconds(5),
                    MaxConcurrency = 1
                },
                NullLogger<global::Astra.Worker.Worker>.Instance);
            await worker.StartAsync(timeout.Token);
            var recovered = await WaitForOutboxStatusAsync(dataSource, eventId, "published", timeout.Token);
            await worker.StopAsync(CancellationToken.None);

            Assert.Equal(0, recovered.Attempts);
            Assert.Equal(1, await CountDeliveriesAsync(dataSource, eventId));
        }
        finally
        {
            if (harness is { HasExited: false })
            {
                harness.Kill(entireProcessTree: true);
                await harness.WaitForExitAsync();
            }

            harness?.Dispose();
            await DeleteRetentionRecordsAsync(dataSource, [eventId]);
        }
    }

    private static async Task ClearPendingOutboxAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox_events
            SET status = 'published',
                published_at = now(),
                leased_by = NULL,
                lease_until = NULL
            WHERE status IN ('pending', 'processing');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> InsertOutboxEventAsync(NpgsqlDataSource dataSource, int maxAttempts)
    {
        var eventId = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO outbox_events(
                event_id,
                event_type,
                aggregate_type,
                aggregate_id,
                idempotency_key,
                payload,
                status,
                max_attempts,
                available_at)
            VALUES (
                $1,
                'test.terminal',
                'player',
                $2,
                'idem-terminal',
                '{}',
                'pending',
                $3,
                now());
            """;
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(maxAttempts);
        await command.ExecuteNonQueryAsync();
        return eventId;
    }

    private static async Task<Guid> InsertCrashRecoveryEventAsync(NpgsqlDataSource dataSource)
    {
        var eventId = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO outbox_events(
                event_id, event_type, aggregate_type, aggregate_id, idempotency_key,
                payload, status, max_attempts, available_at)
            VALUES (
                $1, 'wallet.currency_granted', 'player', $2, $3,
                '{"schemaVersion":1,"currency":1,"amount":100,"balanceAfter":100,"ledgerVersion":1}',
                'pending', 3, now());
            """;
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue($"hard-kill-{eventId:N}");
        await command.ExecuteNonQueryAsync();
        return eventId;
    }

    private static async Task<(int Count, string Summary)> ReadDeliveryAsync(
        NpgsqlDataSource dataSource,
        Guid eventId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) OVER (), summary::text
            FROM operational_event_deliveries
            WHERE consumer_name = $1 AND event_id = $2;
            """;
        command.Parameters.AddWithValue(global::Astra.Worker.PostgresOperationalEventHandler.ConsumerName);
        command.Parameters.AddWithValue(eventId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetString(1));
    }

    private static async Task DeleteOutboxForCleanupAsync(
        NpgsqlDataSource dataSource,
        Guid eventId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM outbox_events WHERE event_id = $1;";
        command.Parameters.AddWithValue(eventId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteDeliveryForCleanupAsync(
        NpgsqlDataSource dataSource,
        Guid eventId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM operational_event_deliveries WHERE event_id = $1;";
        command.Parameters.AddWithValue(eventId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertOutboxForRetentionAsync(
        NpgsqlDataSource dataSource,
        Guid eventId,
        string status,
        DateTimeOffset timestamp)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO outbox_events(
                event_id, event_type, aggregate_type, aggregate_id, idempotency_key,
                payload, status, attempts, max_attempts, available_at, created_at,
                published_at, dead_lettered_at)
            VALUES (
                $1, 'test.retention', 'player', $2, $3, '{}', $4, 0, 5, $5, $5,
                CASE WHEN $4 = 'published' THEN $5 ELSE NULL END,
                CASE WHEN $4 = 'dead_letter' THEN $5 ELSE NULL END);
            """;
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue($"retention-{eventId:N}");
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(timestamp);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertDeliveryForRetentionAsync(
        NpgsqlDataSource dataSource,
        Guid eventId,
        DateTimeOffset consumedAt)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operational_event_deliveries(
                consumer_name, event_id, event_type, aggregate_id, summary, consumed_at)
            VALUES ('retention-test', $1, 'test.retention', $2, '{}', $3);
            """;
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(consumedAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> OutboxExistsAsync(NpgsqlDataSource dataSource, Guid eventId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM outbox_events WHERE event_id = $1);";
        command.Parameters.AddWithValue(eventId);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<bool> DeliveryExistsAsync(NpgsqlDataSource dataSource, Guid eventId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operational_event_deliveries WHERE event_id = $1);";
        command.Parameters.AddWithValue(eventId);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<int> CountDeliveriesAsync(NpgsqlDataSource dataSource, Guid eventId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM operational_event_deliveries WHERE event_id = $1;";
        command.Parameters.AddWithValue(eventId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task WaitForDeliveryCountAsync(
        NpgsqlDataSource dataSource,
        Guid eventId,
        int expectedCount,
        Process harness,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await CountDeliveriesAsync(dataSource, eventId) == expectedCount)
            {
                return;
            }

            if (harness.HasExited)
            {
                var error = await harness.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Crash harness exited before delivery. exitCode={harness.ExitCode} error={error[..Math.Min(error.Length, 1_000)]}");
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException("Crash harness did not commit its delivery projection.");
    }

    private static string CurrentBuildConfiguration() =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
        ?? throw new InvalidOperationException("Unable to determine the test build configuration.");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Astra.LiveOps.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the ASTRA repository root.");
    }

    private static async Task DeleteRetentionRecordsAsync(
        NpgsqlDataSource dataSource,
        Guid[] eventIds)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var deliveryCommand = connection.CreateCommand())
        {
            deliveryCommand.CommandText = "DELETE FROM operational_event_deliveries WHERE event_id = ANY($1);";
            deliveryCommand.Parameters.AddWithValue(eventIds);
            await deliveryCommand.ExecuteNonQueryAsync();
        }

        await using var outboxCommand = connection.CreateCommand();
        outboxCommand.CommandText = "DELETE FROM outbox_events WHERE event_id = ANY($1);";
        outboxCommand.Parameters.AddWithValue(eventIds);
        await outboxCommand.ExecuteNonQueryAsync();
    }

    private static async Task InsertIdempotencyRowsAsync(
        NpgsqlDataSource dataSource,
        Guid playerId,
        string keyPrefix,
        int count,
        DateTimeOffset expiresAt)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var playerCommand = connection.CreateCommand())
        {
            playerCommand.CommandText = "INSERT INTO players(player_id) VALUES ($1) ON CONFLICT DO NOTHING;";
            playerCommand.Parameters.AddWithValue(playerId);
            await playerCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO idempotency_requests(
                player_id, idempotency_key, request_hash, response_body,
                snapshot_body, completed_at, expires_at)
            SELECT $1,
                   $2 || value::text,
                   'retention-test-hash',
                   '{}',
                   '{}',
                   now(),
                   $4
            FROM generate_series(1, $3) AS value;
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(keyPrefix);
        command.Parameters.AddWithValue(count);
        command.Parameters.AddWithValue(expiresAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountIdempotencyRowsAsync(
        NpgsqlDataSource dataSource,
        Guid playerId,
        string keyPattern)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM idempotency_requests
            WHERE player_id = $1 AND idempotency_key LIKE $2;
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(keyPattern);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task DeletePlayerForCleanupAsync(NpgsqlDataSource dataSource, Guid playerId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM players WHERE player_id = $1;";
        command.Parameters.AddWithValue(playerId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountGachaHistoryAsync(
        NpgsqlDataSource dataSource,
        Guid playerId,
        string idempotencyKey)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM gacha_draw_history
            WHERE player_id = $1 AND idempotency_key = $2;
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(idempotencyKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task ExpireIdempotencyAsync(
        NpgsqlDataSource dataSource,
        Guid playerId,
        string idempotencyKey)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE idempotency_requests
            SET expires_at = now() - interval '1 second'
            WHERE player_id = $1 AND idempotency_key = $2;
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(idempotencyKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<OutboxStatusRow> ReadOutboxStatusAsync(NpgsqlDataSource dataSource, Guid eventId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, attempts, last_error
            FROM outbox_events
            WHERE event_id = $1;
            """;
        command.Parameters.AddWithValue(eventId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new OutboxStatusRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? "" : reader.GetString(2));
    }

    private sealed record OutboxStatusRow(string Status, int Attempts, string LastError);

    private static async Task<OutboxStatusRow> WaitForOutboxStatusAsync(
        NpgsqlDataSource dataSource,
        Guid eventId,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var status = await ReadOutboxStatusAsync(dataSource, eventId);
            if (StringComparer.Ordinal.Equals(status.Status, expectedStatus))
            {
                return status;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException($"Outbox event did not reach status {expectedStatus}.");
    }

    private sealed class NoOpOutboxHandler : global::Astra.Worker.IOutboxEventHandler
    {
        public Task HandleAsync(OutboxEventRecord outboxEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ZeroRandomSource : IGachaRandomSource
    {
        public int Next(int exclusiveUpperBound) => 0;
    }
}
