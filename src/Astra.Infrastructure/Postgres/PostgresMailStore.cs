using System.Text.Json;
using Astra.Contracts;
using Astra.Domain;
using Dapper;
using Npgsql;

namespace Astra.Infrastructure.Postgres;

public sealed class PostgresMailStore(NpgsqlDataSource dataSource) : IMailStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MailDefinitionDto> CreateIncidentMailAsync(
        CreateIncidentMailCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command);

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var inserted = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            INSERT INTO mail_definitions(mail_id, incident_id, title, body, rewards_json, reason, created_at)
            VALUES (@MailId, @IncidentId, @Title, @Body, @RewardsJson, @Reason, now())
            ON CONFLICT (mail_id) DO NOTHING
            RETURNING 1;
            """,
            new
            {
                command.MailId,
                command.IncidentId,
                command.Title,
                command.Body,
                RewardsJson = JsonSerializer.Serialize(command.Rewards, JsonOptions),
                Reason = string.IsNullOrWhiteSpace(command.Reason) ? "incident-compensation" : command.Reason
            },
            transaction,
            cancellationToken: cancellationToken));

        if (inserted == 1)
        {
            foreach (var playerId in command.TargetPlayerIds.Distinct())
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO mail_targets(mail_id, player_id)
                    VALUES (@MailId, @PlayerId)
                    ON CONFLICT DO NOTHING;
                    """,
                    new { command.MailId, PlayerId = playerId },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetDefinitionAsync(command.MailId, cancellationToken)
            ?? throw new MailNotFoundException($"Mail not found after create: {command.MailId}.");
    }

    public async Task<MailDefinitionDto?> GetDefinitionAsync(
        string mailId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MailDefinitionRow>(new CommandDefinition(
            """
            SELECT
                incident_id AS IncidentId,
                mail_id AS MailId,
                title AS Title,
                body AS Body,
                rewards_json AS RewardsJson,
                reason AS Reason,
                created_at AS CreatedAt
            FROM mail_definitions
            WHERE mail_id = @MailId;
            """,
            new { MailId = mailId },
            cancellationToken: cancellationToken));

        return row is null
            ? null
            : new MailDefinitionDto(
                row.IncidentId,
                row.MailId,
                row.Title,
                row.Body,
                JsonSerializer.Deserialize<MailRewardDto[]>(row.RewardsJson, JsonOptions) ?? [],
                row.Reason,
                row.CreatedAt);
    }

    public async Task<bool> IsTargetAsync(
        string mailId,
        Guid playerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
                SELECT 1
                FROM mail_targets
                WHERE mail_id = @MailId
                  AND player_id = @PlayerId
            );
            """,
            new { MailId = mailId, PlayerId = playerId },
            cancellationToken: cancellationToken));

        return exists;
    }

    private static void Validate(CreateIncidentMailCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.IncidentId))
        {
            throw new InvalidAccountCommandException("Incident id is required.");
        }

        if (string.IsNullOrWhiteSpace(command.MailId))
        {
            throw new InvalidAccountCommandException("Mail id is required.");
        }

        if (command.TargetPlayerIds.Count == 0)
        {
            throw new InvalidAccountCommandException("Mail target snapshot is empty.");
        }

        if (command.Rewards.Count == 0)
        {
            throw new InvalidAccountCommandException("Mail reward is required.");
        }

        foreach (var reward in command.Rewards)
        {
            if (!Enum.IsDefined(reward.Currency) || reward.Amount <= 0)
            {
                throw new InvalidAccountCommandException("Invalid mail reward.");
            }
        }
    }

    private sealed class MailDefinitionRow
    {
        public string IncidentId { get; init; } = "";

        public string MailId { get; init; } = "";

        public string Title { get; init; } = "";

        public string Body { get; init; } = "";

        public string RewardsJson { get; init; } = "";

        public string Reason { get; init; } = "";

        public DateTimeOffset CreatedAt { get; init; }
    }
}
