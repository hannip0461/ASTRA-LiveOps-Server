using System.Text.Json;
using Astra.Contracts;

namespace Astra.Domain;

public sealed class PlayerAccountCommandProcessor(
    TimeProvider? timeProvider = null,
    IGachaRandomSource? gachaRandomSource = null)
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IGachaRandomSource _gachaRandomSource = gachaRandomSource ?? new CryptographicGachaRandomSource();

    public PlayerCommandReceipt Grant(PlayerAccountState state, GrantCurrencyCommand command) =>
        Execute(
            state,
            command.IdempotencyKey,
            command.RequestHash,
            () =>
            {
                GrantCurrency(state, command.Currency, command.Amount, command.Reason, command.IdempotencyKey);
                return ToSnapshot(state);
            },
            static snapshot => snapshot,
            "wallet.currency_granted",
            snapshot => new WalletCurrencyOutboxPayload(
                1,
                command.Currency,
                command.Amount,
                snapshot.Balances.Single(balance => balance.Currency == command.Currency).Amount,
                snapshot.LedgerVersion));

    public PlayerCommandReceipt Spend(PlayerAccountState state, SpendCurrencyCommand command) =>
        Execute(
            state,
            command.IdempotencyKey,
            command.RequestHash,
            () =>
            {
                ValidateCurrencyDelta(command.Currency, command.Amount);
                SpendCurrency(state, command.Currency, command.Amount, command.Reason, command.IdempotencyKey);
                return ToSnapshot(state);
            },
            static snapshot => snapshot,
            "wallet.currency_spent",
            snapshot => new WalletCurrencyOutboxPayload(
                1,
                command.Currency,
                command.Amount,
                snapshot.Balances.Single(balance => balance.Currency == command.Currency).Amount,
                snapshot.LedgerVersion));

    public PlayerCommandReceipt DrawGacha(PlayerAccountState state, DrawGachaCommand command)
    {
        var grantedRewards = new List<GachaDrawRewardDto>(command.DrawCount);
        var pityBefore = 0;
        var pityAfter = 0;

        return Execute(
            state,
            command.IdempotencyKey,
            command.RequestHash,
            () =>
            {
                ValidateGachaCommand(command);
                SpendCurrency(state, command.CostCurrency, command.CostAmount, $"gacha:{command.BannerId}", command.IdempotencyKey);

                pityBefore = state.GetPity(command.BannerId);
                pityAfter = pityBefore;
                for (var drawIndex = 0; drawIndex < command.DrawCount; drawIndex++)
                {
                    var forcePity = pityAfter >= command.PityThreshold - 1;
                    var selected = SelectReward(command.RewardPool, forcePity);
                    grantedRewards.Add(ApplyReward(state, selected));
                    pityAfter = selected.IsPityTarget ? 0 : checked(pityAfter + 1);
                }

                state.SetPity(command.BannerId, pityAfter);
                state.AddPendingGachaDraw(new PendingGachaDraw(
                    Guid.NewGuid(),
                    state.PlayerId,
                    command.BannerId,
                    command.ContentVersion,
                    command.ContentChecksum,
                    command.DrawCount,
                    command.CostCurrency,
                    command.CostAmount,
                    JsonSerializer.Serialize(grantedRewards, ResponseJsonOptions),
                    pityBefore,
                    pityAfter,
                    command.IdempotencyKey,
                    _timeProvider.GetUtcNow()));

                return ToSnapshot(state);
            },
            snapshot => new GachaDrawResultDto(
                command.BannerId,
                command.ContentVersion,
                command.ContentChecksum,
                grantedRewards.ToArray(),
                pityAfter,
                snapshot),
            "gacha.draw_completed",
            snapshot => new GachaDrawCompletedOutboxPayload(
                1,
                command.BannerId,
                command.ContentVersion,
                command.ContentChecksum,
                command.DrawCount,
                grantedRewards.Count,
                pityAfter,
                snapshot.LedgerVersion));
    }

    public PlayerCommandReceipt ClaimMail(
        PlayerAccountState state,
        ClaimMailCommand command,
        MailDefinitionDto definition) =>
        Execute(
            state,
            command.IdempotencyKey,
            command.RequestHash,
            () =>
            {
                ValidateMailClaim(command, definition);
                if (state.HasClaimedMail(command.MailId))
                {
                    throw new MailAlreadyClaimedException($"Mail already claimed: {command.MailId}.");
                }

                foreach (var reward in definition.Rewards)
                {
                    GrantCurrency(
                        state,
                        reward.Currency,
                        reward.Amount,
                        $"mail:{definition.MailId}:{definition.Reason}",
                        command.IdempotencyKey);
                }

                state.MarkMailClaimed(command.MailId, command.IdempotencyKey);
                return ToSnapshot(state);
            },
            snapshot => new MailClaimResultDto(
                definition.IncidentId,
                definition.MailId,
                definition.Rewards,
                snapshot),
            "mail.claimed",
            snapshot => new MailClaimedOutboxPayload(
                1,
                definition.IncidentId,
                definition.MailId,
                definition.Rewards.Count,
                snapshot.LedgerVersion));

    public PlayerCommandReceipt? TryReplay(
        PlayerAccountState state,
        string idempotencyKey,
        string requestHash)
    {
        ValidateIdempotency(idempotencyKey, requestHash);
        if (!state.CompletedRequests.TryGetValue(idempotencyKey, out var completed))
        {
            return null;
        }

        if (completed.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            state.RemoveCompletedRequest(idempotencyKey);
            return null;
        }

        if (!StringComparer.Ordinal.Equals(completed.RequestHash, requestHash))
        {
            throw new IdempotencyConflictException("Same Idempotency-Key was reused with a different request hash.");
        }

        var snapshot = string.IsNullOrWhiteSpace(completed.SnapshotBody)
            ? ToSnapshot(state)
            : JsonSerializer.Deserialize<WalletSnapshotDto>(completed.SnapshotBody, ResponseJsonOptions)
                ?? throw new InvalidDataException("Stored idempotency snapshot is invalid.");

        return new PlayerCommandReceipt(true, completed.ResponseBody, snapshot);
    }

    public static WalletSnapshotDto ToSnapshot(PlayerAccountState state)
    {
        var balances = state.Balances
            .OrderBy(pair => pair.Key)
            .Select(pair => new CurrencyBalanceDto(pair.Key, pair.Value))
            .ToArray();

        var inventory = state.Inventory
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new InventoryItemDto(pair.Key, pair.Value))
            .ToArray();

        var characters = state.Characters.Values
            .OrderBy(character => character.CharacterId, StringComparer.Ordinal)
            .Select(character => new CharacterDto(character.CharacterId, character.Rarity, character.DuplicateCount))
            .ToArray();

        return new WalletSnapshotDto(
            state.PlayerId,
            balances,
            state.LedgerVersion,
            inventory,
            characters,
            new Dictionary<string, int>(state.PityByBanner, StringComparer.Ordinal));
    }

    private PlayerCommandReceipt Execute(
        PlayerAccountState state,
        string idempotencyKey,
        string requestHash,
        Func<WalletSnapshotDto> apply) =>
        Execute(
            state,
            idempotencyKey,
            requestHash,
            apply,
            static snapshot => snapshot,
            eventType: null,
            eventPayloadFactory: null);

    private PlayerCommandReceipt Execute<TResponse>(
        PlayerAccountState state,
        string idempotencyKey,
        string requestHash,
        Func<WalletSnapshotDto> apply,
        Func<WalletSnapshotDto, TResponse> responseFactory,
        string? eventType,
        Func<WalletSnapshotDto, object>? eventPayloadFactory)
    {
        var replay = TryReplay(state, idempotencyKey, requestHash);
        if (replay is not null)
        {
            return replay;
        }

        var snapshot = apply();
        var responseBody = JsonSerializer.Serialize(responseFactory(snapshot), ResponseJsonOptions);
        var now = _timeProvider.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            var eventPayload = eventPayloadFactory?.Invoke(snapshot)
                ?? throw new InvalidOperationException("Outbox event payload factory is required.");
            state.AddPendingOutboxEvent(new PendingOutboxEvent(
                Guid.NewGuid(),
                eventType,
                state.PlayerId,
                idempotencyKey,
                JsonSerializer.Serialize(eventPayload, eventPayload.GetType(), ResponseJsonOptions),
                now));
        }

        state.AddCompletedRequest(new CompletedIdempotencyRequest(
            idempotencyKey,
            requestHash,
            responseBody,
            JsonSerializer.Serialize(snapshot, ResponseJsonOptions),
            now,
            now.AddHours(24)));

        return new PlayerCommandReceipt(false, responseBody, snapshot);
    }

    private static void ValidateGachaCommand(DrawGachaCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.BannerId))
        {
            throw new InvalidAccountCommandException("Banner id is required.");
        }

        if (string.IsNullOrWhiteSpace(command.ContentVersion))
        {
            throw new InvalidAccountCommandException("Content version is required.");
        }

        if (string.IsNullOrWhiteSpace(command.ContentChecksum))
        {
            throw new InvalidAccountCommandException("Content checksum is required.");
        }

        ValidateCurrencyDelta(command.CostCurrency, command.CostAmount);

        if (command.DrawCount is <= 0 or > GachaCommandFactory.MaxDrawCount)
        {
            throw new InvalidAccountCommandException(
                $"Draw count must be between 1 and {GachaCommandFactory.MaxDrawCount}.");
        }

        if (command.PityThreshold <= 0)
        {
            throw new InvalidAccountCommandException("Pity threshold must be positive.");
        }

        if (command.RewardPool.Count == 0)
        {
            throw new InvalidAccountCommandException("Reward pool is required.");
        }

        var totalWeight = 0L;
        var hasPityTarget = false;
        foreach (var reward in command.RewardPool)
        {
            ValidateReward(reward);
            totalWeight += reward.Weight;
            hasPityTarget |= reward.IsPityTarget;
        }

        if (totalWeight > int.MaxValue)
        {
            throw new InvalidAccountCommandException("Total reward weight is too large.");
        }

        if (!hasPityTarget)
        {
            throw new InvalidAccountCommandException("Reward pool must contain a pity target.");
        }
    }

    private static void ValidateReward(GachaRewardPoolEntryDto reward)
    {
        if (!Enum.IsDefined(reward.Kind))
        {
            throw new InvalidAccountCommandException($"Unsupported reward kind: {reward.Kind}.");
        }

        if (string.IsNullOrWhiteSpace(reward.RewardId))
        {
            throw new InvalidAccountCommandException("Reward id is required.");
        }

        if (reward.Quantity <= 0)
        {
            throw new InvalidAccountCommandException("Reward quantity must be positive.");
        }

        if (reward.Rarity <= 0)
        {
            throw new InvalidAccountCommandException("Reward rarity must be positive.");
        }

        if (reward.Weight <= 0)
        {
            throw new InvalidAccountCommandException("Reward weight must be positive.");
        }

        if (reward.Kind == GachaRewardKind.Character)
        {
            if (reward.Quantity != 1)
            {
                throw new InvalidAccountCommandException("Character reward quantity must be one.");
            }

            if (string.IsNullOrWhiteSpace(reward.DuplicateItemId) || reward.DuplicateItemQuantity <= 0)
            {
                throw new InvalidAccountCommandException("Character duplicate conversion is required.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(reward.DuplicateItemId) || reward.DuplicateItemQuantity != 0)
        {
            throw new InvalidAccountCommandException("Item rewards cannot define duplicate conversion.");
        }
    }

    private static void ValidateMailClaim(ClaimMailCommand command, MailDefinitionDto definition)
    {
        if (string.IsNullOrWhiteSpace(command.MailId))
        {
            throw new InvalidAccountCommandException("Mail id is required.");
        }

        if (!StringComparer.Ordinal.Equals(command.MailId, definition.MailId))
        {
            throw new InvalidAccountCommandException("Mail command and definition mismatch.");
        }

        if (definition.Rewards.Count == 0)
        {
            throw new InvalidAccountCommandException("Mail must contain at least one reward.");
        }

        foreach (var reward in definition.Rewards)
        {
            ValidateCurrencyDelta(reward.Currency, reward.Amount);
        }
    }

    private GachaRewardPoolEntryDto SelectReward(
        IReadOnlyList<GachaRewardPoolEntryDto> rewardPool,
        bool forcePity)
    {
        var candidates = forcePity
            ? rewardPool.Where(entry => entry.IsPityTarget).ToArray()
            : rewardPool;
        var totalWeight = candidates.Sum(entry => entry.Weight);
        var roll = _gachaRandomSource.Next(totalWeight);

        foreach (var candidate in candidates)
        {
            if (roll < candidate.Weight)
            {
                return candidate;
            }

            roll -= candidate.Weight;
        }

        throw new InvalidOperationException("Weighted reward selection did not produce a result.");
    }

    private static GachaDrawRewardDto ApplyReward(PlayerAccountState state, GachaRewardPoolEntryDto reward)
    {
        if (reward.Kind == GachaRewardKind.Item)
        {
            state.AddItem(reward.RewardId, reward.Quantity);
            return new GachaDrawRewardDto(
                reward.Kind,
                reward.RewardId,
                reward.Quantity,
                reward.Rarity,
                false,
                null);
        }

        var wasDuplicate = state.AddCharacter(reward.RewardId, reward.Rarity);
        InventoryItemDto? conversion = null;
        if (wasDuplicate)
        {
            state.AddItem(reward.DuplicateItemId!, reward.DuplicateItemQuantity);
            conversion = new InventoryItemDto(reward.DuplicateItemId!, reward.DuplicateItemQuantity);
        }

        return new GachaDrawRewardDto(
            reward.Kind,
            reward.RewardId,
            reward.Quantity,
            reward.Rarity,
            wasDuplicate,
            conversion);
    }

    private static void ValidateCurrencyDelta(CurrencyCode currency, long amount)
    {
        if (!Enum.IsDefined(currency))
        {
            throw new InvalidAccountCommandException($"Unsupported currency: {currency}.");
        }

        if (amount <= 0)
        {
            throw new InvalidAccountCommandException("Currency amount must be positive.");
        }
    }

    private static void ValidateIdempotency(string idempotencyKey, string requestHash)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new InvalidAccountCommandException("Idempotency-Key is required.");
        }

        if (string.IsNullOrWhiteSpace(requestHash))
        {
            throw new InvalidAccountCommandException("Request hash is required.");
        }
    }

    private void AddLedger(
        PlayerAccountState state,
        CurrencyCode currency,
        long delta,
        long balanceAfter,
        string reason,
        string idempotencyKey)
    {
        state.AddLedgerEntry(new LedgerEntry(
            state.LedgerVersion + 1,
            currency,
            delta,
            balanceAfter,
            string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason,
            idempotencyKey,
            _timeProvider.GetUtcNow()));
    }

    private void GrantCurrency(
        PlayerAccountState state,
        CurrencyCode currency,
        long amount,
        string reason,
        string idempotencyKey)
    {
        ValidateCurrencyDelta(currency, amount);
        var newBalance = checked(state.GetBalance(currency) + amount);
        state.SetBalance(currency, newBalance);
        AddLedger(state, currency, amount, newBalance, reason, idempotencyKey);
    }

    private void SpendCurrency(
        PlayerAccountState state,
        CurrencyCode currency,
        long amount,
        string reason,
        string idempotencyKey)
    {
        var current = state.GetBalance(currency);
        if (current < amount)
        {
            throw new InsufficientCurrencyException(
                $"Insufficient {currency}: current={current}, required={amount}.");
        }

        var newBalance = current - amount;
        state.SetBalance(currency, newBalance);
        AddLedger(state, currency, -amount, newBalance, reason, idempotencyKey);
    }
}
