using System.Text.Json;
using Astra.Domain;
using Astra.Infrastructure.Postgres;
using Dapper;
using Npgsql;

namespace Astra.Worker;

public sealed class PostgresOperationalEventHandler(NpgsqlDataSource dataSource) : IOutboxEventHandler
{
    public const string ConsumerName = "operational-feed-v1";

    public async Task HandleAsync(
        OutboxEventRecord outboxEvent,
        CancellationToken cancellationToken = default)
    {
        var summary = OperationalEventSummaryFactory.Create(outboxEvent);

        const string sql = """
            INSERT INTO operational_event_deliveries(
                consumer_name,
                event_id,
                event_type,
                aggregate_id,
                summary,
                consumed_at)
            VALUES (
                @ConsumerName,
                @EventId,
                @EventType,
                @AggregateId,
                CAST(@Summary AS jsonb),
                now())
            ON CONFLICT (consumer_name, event_id) DO NOTHING;
            """;

        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                ConsumerName,
                outboxEvent.EventId,
                outboxEvent.EventType,
                outboxEvent.AggregateId,
                Summary = summary
            },
            cancellationToken: cancellationToken));
    }
}

internal static class OperationalEventSummaryFactory
{
    private const int MaxPayloadCharacters = 2_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Create(OutboxEventRecord outboxEvent)
    {
        if (outboxEvent.Payload.Length > MaxPayloadCharacters)
        {
            throw new InvalidOutboxPayloadException("Outbox payload exceeds the consumer limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                outboxEvent.Payload,
                new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOutboxPayloadException("Outbox payload must be a JSON object.");
            }

            var root = document.RootElement;
            object summary = outboxEvent.EventType switch
            {
                "wallet.currency_granted" or "wallet.currency_spent" => WalletSummary(root),
                "gacha.draw_completed" => GachaSummary(root),
                "mail.claimed" => MailSummary(root),
                _ => throw new UnsupportedOutboxEventException(outboxEvent.EventType)
            };

            return JsonSerializer.Serialize(summary, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOutboxPayloadException("Outbox payload is not valid JSON.", exception);
        }
        catch (InvalidOperationException exception)
            when (exception is not InvalidOutboxPayloadException and not UnsupportedOutboxEventException)
        {
            throw new InvalidOutboxPayloadException("Outbox payload does not match its event type.", exception);
        }
    }

    private static object WalletSummary(JsonElement root)
    {
        if (TrySchemaVersion(root, out var schemaVersion))
        {
            return new
            {
                schemaVersion,
                currency = RequiredInt32(root, "currency"),
                amount = RequiredInt64(root, "amount"),
                balanceAfter = RequiredInt64(root, "balanceAfter"),
                ledgerVersion = RequiredInt64(root, "ledgerVersion")
            };
        }

        return new
        {
            schemaVersion = 0,
            ledgerVersion = RequiredInt64(root, "ledgerVersion"),
            balanceCount = RequiredArray(root, "balances").GetArrayLength()
        };
    }

    private static object GachaSummary(JsonElement root)
    {
        var schemaVersion = TrySchemaVersion(root, out var version) ? version : 0;
        return new
        {
            schemaVersion,
            bannerId = RequiredString(root, "bannerId"),
            contentVersion = RequiredString(root, "contentVersion"),
            contentChecksum = RequiredString(root, "contentChecksum"),
            drawCount = schemaVersion == 0 ? RequiredArray(root, "rewards").GetArrayLength() : RequiredInt32(root, "drawCount"),
            rewardCount = schemaVersion == 0 ? RequiredArray(root, "rewards").GetArrayLength() : RequiredInt32(root, "rewardCount"),
            pityAfter = RequiredInt32(root, "pityAfter"),
            ledgerVersion = schemaVersion == 0
                ? RequiredInt64(RequiredObject(root, "snapshot"), "ledgerVersion")
                : RequiredInt64(root, "ledgerVersion")
        };
    }

    private static object MailSummary(JsonElement root)
    {
        var schemaVersion = TrySchemaVersion(root, out var version) ? version : 0;
        return new
        {
            schemaVersion,
            incidentId = RequiredString(root, "incidentId"),
            mailId = RequiredString(root, "mailId"),
            rewardCount = schemaVersion == 0 ? RequiredArray(root, "rewards").GetArrayLength() : RequiredInt32(root, "rewardCount"),
            ledgerVersion = schemaVersion == 0
                ? RequiredInt64(RequiredObject(root, "snapshot"), "ledgerVersion")
                : RequiredInt64(root, "ledgerVersion")
        };
    }

    private static bool TrySchemaVersion(JsonElement root, out int schemaVersion)
    {
        if (!root.TryGetProperty("schemaVersion", out var value))
        {
            schemaVersion = 0;
            return false;
        }

        if (!value.TryGetInt32(out schemaVersion) || schemaVersion != 1)
        {
            throw new InvalidOutboxPayloadException("Outbox payload schema version is unsupported.");
        }

        return true;
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidOutboxPayloadException($"'{name}' must be an object.");
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        return value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidOutboxPayloadException($"'{name}' must be an array.");
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text) && text.Length <= 128
            ? text
            : throw new InvalidOutboxPayloadException($"'{name}' must be a valid identifier.");
    }

    private static long RequiredInt64(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        return value.TryGetInt64(out var number) && number >= 0
            ? number
            : throw new InvalidOutboxPayloadException($"'{name}' must be a non-negative integer.");
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        return value.TryGetInt32(out var number) && number >= 0
            ? number
            : throw new InvalidOutboxPayloadException($"'{name}' must be a non-negative integer.");
    }

    private static JsonElement Required(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidOutboxPayloadException($"Required property '{name}' is missing.");
}

internal sealed class UnsupportedOutboxEventException(string eventType) : InvalidOperationException(
    $"Outbox event type '{eventType}' is not supported by this consumer.");

internal sealed class InvalidOutboxPayloadException : InvalidOperationException
{
    public InvalidOutboxPayloadException(string message)
        : base(message)
    {
    }

    public InvalidOutboxPayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
