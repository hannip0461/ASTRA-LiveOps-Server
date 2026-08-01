using System.Data;
using Astra.Contracts;
using Astra.Domain;
using Dapper;
using Npgsql;

namespace Astra.Infrastructure.Postgres;

public sealed class PostgresPlayerAccountStore(NpgsqlDataSource dataSource) : IPlayerAccountStore
{
    public async Task<WalletSnapshotDto> ReadSnapshotAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SET TRANSACTION READ ONLY;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        var state = await LoadStateAsync(connection, transaction, playerId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PlayerAccountCommandProcessor.ToSnapshot(state);
    }

    public async Task<PlayerCommandReceipt> ExecuteAsync(
        Guid playerId,
        Func<PlayerAccountState, PlayerCommandReceipt> operation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionObservedAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await EnsurePlayerLockedAsync(connection, transaction, playerId, cancellationToken);
        var state = await LoadStateAsync(connection, transaction, playerId, cancellationToken);
        var knownClaimedMailIds = state.ClaimedMailIdempotencyKeys.Keys.ToHashSet(StringComparer.Ordinal);

        var receipt = operation(state);
        if (!receipt.Replayed)
        {
            await SaveStateAsync(
                connection,
                transaction,
                state,
                knownClaimedMailIds,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        state.ClearPendingGachaDraws();
        state.ClearPendingOutboxEvents();
        state.ClearPendingCompletedRequests();
        return receipt;
    }

    private static async Task EnsurePlayerLockedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO players(player_id) VALUES (@PlayerId) ON CONFLICT DO NOTHING;",
            new { PlayerId = playerId },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT player_id FROM players WHERE player_id = @PlayerId FOR UPDATE;",
            new { PlayerId = playerId },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM idempotency_requests WHERE player_id = @PlayerId AND expires_at <= now();",
            new { PlayerId = playerId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<PlayerAccountState> LoadStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var exists = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT player_id FROM players WHERE player_id = @PlayerId;",
            new { PlayerId = playerId },
            transaction,
            cancellationToken: cancellationToken));

        var ledgerVersion = exists is null
            ? 0
            : await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COALESCE(MAX(version), 0) FROM ledger_entries WHERE player_id = @PlayerId;",
                new { PlayerId = playerId },
                transaction,
                cancellationToken: cancellationToken));

        var state = new PlayerAccountState(playerId, ledgerVersion);
        if (exists is null)
        {
            state.MarkHydrated();
            return state;
        }

        var balances = await connection.QueryAsync<BalanceRow>(new CommandDefinition(
            """
            SELECT currency AS Currency, amount AS Amount
            FROM wallet_balances
            WHERE player_id = @PlayerId;
            """,
            new { PlayerId = playerId },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var row in balances)
        {
            state.SetBalance((CurrencyCode)row.Currency, row.Amount);
        }

        var inventory = await connection.QueryAsync<InventoryRow>(new CommandDefinition(
            """
            SELECT item_id AS ItemId, quantity AS Quantity
            FROM inventory_items
            WHERE player_id = @PlayerId;
            """,
            new { PlayerId = playerId },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var row in inventory)
        {
            state.AddItem(row.ItemId, row.Quantity);
        }

        var characters = await connection.QueryAsync<CharacterRow>(new CommandDefinition(
            """
            SELECT character_id AS CharacterId, rarity AS Rarity, duplicate_count AS DuplicateCount
            FROM owned_characters
            WHERE player_id = @PlayerId;
            """,
            new { PlayerId = playerId },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var row in characters)
        {
            state.SetCharacter(row.CharacterId, row.Rarity, row.DuplicateCount);
        }

        var pity = await connection.QueryAsync<PityRow>(new CommandDefinition(
            """
            SELECT banner_id AS BannerId, pity AS Pity
            FROM pity_states
            WHERE player_id = @PlayerId;
            """,
            new { PlayerId = playerId },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var row in pity)
        {
            state.AddPity(row.BannerId, row.Pity);
        }

        var idempotency = await connection.QueryAsync<IdempotencyRow>(new CommandDefinition(
            """
            SELECT
                idempotency_key AS IdempotencyKey,
                request_hash AS RequestHash,
                response_body AS ResponseBody,
                snapshot_body AS SnapshotBody,
                completed_at AS CompletedAt,
                expires_at AS ExpiresAt
            FROM idempotency_requests
            WHERE player_id = @PlayerId
              AND expires_at > now();
            """,
            new { PlayerId = playerId },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var row in idempotency)
        {
            state.HydrateCompletedRequest(new CompletedIdempotencyRequest(
                row.IdempotencyKey,
                row.RequestHash,
                row.ResponseBody,
                row.SnapshotBody,
                row.CompletedAt,
                row.ExpiresAt));
        }

        var claimedMails = await connection.QueryAsync<MailClaimRow>(new CommandDefinition(
            """
            SELECT mail_id AS MailId, idempotency_key AS IdempotencyKey
            FROM mail_claims
            WHERE player_id = @PlayerId;
            """,
            new { PlayerId = playerId },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var row in claimedMails)
        {
            state.MarkMailClaimed(row.MailId, row.IdempotencyKey);
        }

        state.MarkHydrated();
        return state;
    }

    private static async Task SaveStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlayerAccountState state,
        HashSet<string> knownClaimedMailIds,
        CancellationToken cancellationToken)
    {
        foreach (var balance in state.ChangedBalances)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO wallet_balances(player_id, currency, amount, updated_at)
                VALUES (@PlayerId, @Currency, @Amount, now())
                ON CONFLICT (player_id, currency)
                DO UPDATE SET amount = EXCLUDED.amount, updated_at = now();
                """,
                new { state.PlayerId, Currency = (short)balance.Key, Amount = balance.Value },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var item in state.ChangedInventory)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO inventory_items(player_id, item_id, quantity, updated_at)
                VALUES (@PlayerId, @ItemId, @Quantity, now())
                ON CONFLICT (player_id, item_id)
                DO UPDATE SET quantity = EXCLUDED.quantity, updated_at = now();
                """,
                new { state.PlayerId, ItemId = item.Key, Quantity = item.Value },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var character in state.ChangedCharacters)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO owned_characters(player_id, character_id, rarity, duplicate_count, updated_at)
                VALUES (@PlayerId, @CharacterId, @Rarity, @DuplicateCount, now())
                ON CONFLICT (player_id, character_id)
                DO UPDATE SET rarity = EXCLUDED.rarity,
                              duplicate_count = EXCLUDED.duplicate_count,
                              updated_at = now();
                """,
                new
                {
                    state.PlayerId,
                    character.CharacterId,
                    character.Rarity,
                    character.DuplicateCount
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var pity in state.ChangedPity)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO pity_states(player_id, banner_id, pity, updated_at)
                VALUES (@PlayerId, @BannerId, @Pity, now())
                ON CONFLICT (player_id, banner_id)
                DO UPDATE SET pity = EXCLUDED.pity, updated_at = now();
                """,
                new { state.PlayerId, BannerId = pity.Key, Pity = pity.Value },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var draw in state.PendingGachaDraws)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO gacha_draw_history(
                    draw_id,
                    player_id,
                    banner_id,
                    content_version,
                    content_checksum,
                    draw_count,
                    cost_currency,
                    cost_amount,
                    rewards_json,
                    pity_before,
                    pity_after,
                    idempotency_key,
                    created_at)
                VALUES (
                    @DrawId,
                    @PlayerId,
                    @BannerId,
                    @ContentVersion,
                    @ContentChecksum,
                    @DrawCount,
                    @CostCurrency,
                    @CostAmount,
                    @RewardsJson,
                    @PityBefore,
                    @PityAfter,
                    @IdempotencyKey,
                    @CreatedAt);
                """,
                new
                {
                    draw.DrawId,
                    draw.PlayerId,
                    draw.BannerId,
                    draw.ContentVersion,
                    draw.ContentChecksum,
                    draw.DrawCount,
                    CostCurrency = (short)draw.CostCurrency,
                    draw.CostAmount,
                    draw.RewardsJson,
                    draw.PityBefore,
                    draw.PityAfter,
                    draw.IdempotencyKey,
                    draw.CreatedAt
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var entry in state.Ledger)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ledger_entries(player_id, version, currency, delta, balance_after, reason, idempotency_key, created_at)
                VALUES (@PlayerId, @Version, @Currency, @Delta, @BalanceAfter, @Reason, @IdempotencyKey, @CreatedAt);
                """,
                new
                {
                    state.PlayerId,
                    entry.Version,
                    Currency = (short)entry.Currency,
                    entry.Delta,
                    entry.BalanceAfter,
                    entry.Reason,
                    entry.IdempotencyKey,
                    entry.CreatedAt
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        // 만료된 키를 다시 처리하면 같은 transaction에서 응답을 교체한다.
        foreach (var request in state.PendingCompletedRequests)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO idempotency_requests(
                    player_id,
                    idempotency_key,
                    request_hash,
                    response_body,
                    snapshot_body,
                    completed_at,
                    expires_at)
                VALUES (
                    @PlayerId,
                    @IdempotencyKey,
                    @RequestHash,
                    @ResponseBody,
                    @SnapshotBody,
                    @CompletedAt,
                    @ExpiresAt)
                ON CONFLICT (player_id, idempotency_key)
                DO UPDATE SET request_hash = EXCLUDED.request_hash,
                              response_body = EXCLUDED.response_body,
                              snapshot_body = EXCLUDED.snapshot_body,
                              completed_at = EXCLUDED.completed_at,
                              expires_at = EXCLUDED.expires_at;
                """,
                new
                {
                    state.PlayerId,
                    request.IdempotencyKey,
                    request.RequestHash,
                    request.ResponseBody,
                    request.SnapshotBody,
                    request.CompletedAt,
                    request.ExpiresAt
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        var newMailClaims = state.ClaimedMailIdempotencyKeys
            .Where(claim => !knownClaimedMailIds.Contains(claim.Key));

        foreach (var claim in newMailClaims)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO mail_claims(player_id, mail_id, idempotency_key, claimed_at)
                VALUES (@PlayerId, @MailId, @IdempotencyKey, now());
                """,
                new
                {
                    state.PlayerId,
                    MailId = claim.Key,
                    IdempotencyKey = claim.Value
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var outboxEvent in state.PendingOutboxEvents)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO outbox_events(
                    event_id,
                    event_type,
                    aggregate_type,
                    aggregate_id,
                    idempotency_key,
                    payload,
                    status,
                    created_at,
                    available_at)
                VALUES (
                    @EventId,
                    @EventType,
                    'player',
                    @AggregateId,
                    @IdempotencyKey,
                    @Payload,
                    'pending',
                    @CreatedAt,
                    now());
                """,
                outboxEvent,
                transaction,
                cancellationToken: cancellationToken));
        }
    }

    private sealed class BalanceRow
    {
        public short Currency { get; init; }

        public long Amount { get; init; }
    }

    private sealed class InventoryRow
    {
        public string ItemId { get; init; } = "";

        public long Quantity { get; init; }
    }

    private sealed class CharacterRow
    {
        public string CharacterId { get; init; } = "";

        public int Rarity { get; init; }

        public int DuplicateCount { get; init; }
    }

    private sealed class PityRow
    {
        public string BannerId { get; init; } = "";

        public int Pity { get; init; }
    }

    private sealed class IdempotencyRow
    {
        public string IdempotencyKey { get; init; } = "";

        public string RequestHash { get; init; } = "";

        public string ResponseBody { get; init; } = "";

        public string? SnapshotBody { get; init; }

        public DateTimeOffset CompletedAt { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }
    }

    private sealed class MailClaimRow
    {
        public string MailId { get; init; } = "";

        public string IdempotencyKey { get; init; } = "";
    }
}
