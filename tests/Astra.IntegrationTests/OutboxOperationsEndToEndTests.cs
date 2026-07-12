using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Astra.Contracts;
using Npgsql;

namespace Astra.IntegrationTests;

[Collection(EndToEndCollection.Name)]
public sealed class OutboxOperationsEndToEndTests
{
    [Fact]
    public async Task OutboxRoutes_ProtectPayload_EnforceSupervisor_AndAuditReplay()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ASTRA_RUN_API_E2E"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var connectionString = Environment.GetEnvironmentVariable("ASTRA_POSTGRES_CONNECTION")
            ?? "Host=localhost;Port=54329;Database=astra;Username=astra;Password=astra_dev_password";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var eventId = await InsertDeadLetterAsync(dataSource, timeout.Token);

        try
        {
            using var viewer = await ApiE2E.AuthenticatedClientAsync("local-viewer", timeout.Token);
            using var operatorClient = await ApiE2E.AuthenticatedClientAsync("local-operator", timeout.Token);
            using var supervisor = await ApiE2E.AuthenticatedClientAsync("local-supervisor", timeout.Token);

            var overview = await viewer.GetFromJsonAsync<OutboxOverviewDto>(
                "/api/admin/outbox/overview",
                timeout.Token);
            Assert.NotNull(overview);
            Assert.True(overview.DeadLetterCount >= 1);

            var deadLetterJson = await viewer.GetStringAsync(
                "/api/admin/outbox/dead-letters?limit=200",
                timeout.Token);
            var deadLetters = JsonSerializer.Deserialize<OutboxDeadLetterDto[]>(
                deadLetterJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            Assert.Contains(deadLetters, entry => entry.EventId == eventId);
            Assert.DoesNotContain("private-outbox-payload", deadLetterJson, StringComparison.Ordinal);
            Assert.DoesNotContain("private-idempotency-key", deadLetterJson, StringComparison.Ordinal);

            using var forbidden = await operatorClient.PostAsJsonAsync(
                $"/api/admin/outbox/dead-letters/{eventId}/replay",
                new ReplayOutboxEventCommand("operator-must-not-replay"),
                timeout.Token);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            using var invalid = await supervisor.PostAsJsonAsync(
                $"/api/admin/outbox/dead-letters/{eventId}/replay",
                new ReplayOutboxEventCommand(" "),
                timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            const string replayReason = "consumer-fix-deployed-e2e";
            using var replayResponse = await supervisor.PostAsJsonAsync(
                $"/api/admin/outbox/dead-letters/{eventId}/replay",
                new ReplayOutboxEventCommand(replayReason),
                timeout.Token);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            var replay = await replayResponse.Content.ReadFromJsonAsync<OutboxReplayResultDto>(timeout.Token);
            Assert.NotNull(replay);
            Assert.Equal("pending", replay.Status);
            Assert.Equal(1, replay.ManualReplayCount);

            var audits = await supervisor.GetFromJsonAsync<OperationAuditDto[]>(
                "/api/admin/audit?limit=200",
                timeout.Token) ?? [];
            var audit = Assert.Single(
                audits,
                entry => entry.Action == "outbox.dead_letter.replay" && entry.TargetId == eventId.ToString());
            Assert.Equal("local-supervisor", audit.ActorId);
            Assert.Equal(replayReason, audit.Reason);
            Assert.Equal(OperationAuditStatus.Succeeded, audit.Status);
        }
        finally
        {
            await MarkPublishedAsync(dataSource, eventId, CancellationToken.None);
        }
    }

    private static async Task<Guid> InsertDeadLetterAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO outbox_events(
                event_id, event_type, aggregate_type, aggregate_id, idempotency_key,
                payload, status, attempts, max_attempts, last_error, available_at,
                created_at, dead_lettered_at)
            VALUES (
                $1, 'wallet.currency_granted', 'player', $2, 'private-idempotency-key',
                '{"schemaVersion":1,"currency":1,"amount":100,"balanceAfter":100,"ledgerVersion":1,"secret":"private-outbox-payload"}',
                'dead_letter', 5, 5,
                'outbox_payload_invalid', now(), now(), now());
            """;
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(Guid.NewGuid());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return eventId;
    }

    private static async Task MarkPublishedAsync(
        NpgsqlDataSource dataSource,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox_events
            SET status = 'published', published_at = now(), last_error = NULL,
                dead_lettered_at = NULL, leased_by = NULL, lease_until = NULL
            WHERE event_id = $1;
            """;
        command.Parameters.AddWithValue(eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
