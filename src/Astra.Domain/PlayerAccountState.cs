using Astra.Contracts;

namespace Astra.Domain;

public sealed class PlayerAccountState(Guid playerId, long baseLedgerVersion = 0)
{
    private readonly Dictionary<CurrencyCode, long> _balances = [];
    private readonly Dictionary<string, CompletedIdempotencyRequest> _completedRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _inventory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedCharacter> _characters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pityByBanner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _claimedMailIdempotencyKeys = new(StringComparer.Ordinal);
    private readonly List<PendingGachaDraw> _pendingGachaDraws = [];
    private readonly List<PendingOutboxEvent> _pendingOutboxEvents = [];
    private readonly List<LedgerEntry> _ledger = [];

    public Guid PlayerId { get; } = playerId;

    public IReadOnlyDictionary<CurrencyCode, long> Balances => _balances;

    public IReadOnlyDictionary<string, CompletedIdempotencyRequest> CompletedRequests => _completedRequests;

    public IReadOnlyDictionary<string, long> Inventory => _inventory;

    public IReadOnlyDictionary<string, OwnedCharacter> Characters => _characters;

    public IReadOnlyDictionary<string, int> PityByBanner => _pityByBanner;

    public IReadOnlyDictionary<string, string> ClaimedMailIdempotencyKeys => _claimedMailIdempotencyKeys;

    public IReadOnlyList<PendingGachaDraw> PendingGachaDraws => _pendingGachaDraws;

    public IReadOnlyList<PendingOutboxEvent> PendingOutboxEvents => _pendingOutboxEvents;

    public IReadOnlyList<LedgerEntry> Ledger => _ledger;

    public long LedgerVersion => baseLedgerVersion + _ledger.Count;

    public long GetBalance(CurrencyCode currency) => _balances.GetValueOrDefault(currency);

    public int GetPity(string bannerId) => _pityByBanner.GetValueOrDefault(bannerId);

    internal void SetBalance(CurrencyCode currency, long amount) => _balances[currency] = amount;

    internal void AddItem(string itemId, long quantity) =>
        _inventory[itemId] = checked(_inventory.GetValueOrDefault(itemId) + quantity);

    internal bool AddCharacter(string characterId, int rarity)
    {
        if (_characters.TryGetValue(characterId, out var owned))
        {
            _characters[characterId] = owned with { DuplicateCount = owned.DuplicateCount + 1 };
            return true;
        }

        _characters[characterId] = new OwnedCharacter(characterId, rarity, 0);
        return false;
    }

    internal void SetCharacter(string characterId, int rarity, int duplicateCount) =>
        _characters[characterId] = new OwnedCharacter(characterId, rarity, duplicateCount);

    internal int AddPity(string bannerId, int amount)
    {
        var next = checked(_pityByBanner.GetValueOrDefault(bannerId) + amount);
        _pityByBanner[bannerId] = next;
        return next;
    }

    internal void SetPity(string bannerId, int value) => _pityByBanner[bannerId] = value;

    internal void AddCompletedRequest(CompletedIdempotencyRequest request) =>
        _completedRequests.Add(request.IdempotencyKey, request);

    internal void RemoveCompletedRequest(string idempotencyKey) =>
        _completedRequests.Remove(idempotencyKey);

    internal bool HasClaimedMail(string mailId) => _claimedMailIdempotencyKeys.ContainsKey(mailId);

    internal void MarkMailClaimed(string mailId, string idempotencyKey) =>
        _claimedMailIdempotencyKeys[mailId] = idempotencyKey;

    internal void AddPendingGachaDraw(PendingGachaDraw draw) => _pendingGachaDraws.Add(draw);

    internal void ClearPendingGachaDraws() => _pendingGachaDraws.Clear();

    internal void AddPendingOutboxEvent(PendingOutboxEvent outboxEvent) =>
        _pendingOutboxEvents.Add(outboxEvent);

    internal void ClearPendingOutboxEvents() => _pendingOutboxEvents.Clear();

    internal void AddLedgerEntry(LedgerEntry entry) => _ledger.Add(entry);

    internal PlayerAccountState Clone()
    {
        var clone = new PlayerAccountState(PlayerId, baseLedgerVersion);

        foreach (var pair in _balances)
        {
            clone._balances.Add(pair.Key, pair.Value);
        }

        foreach (var pair in _completedRequests)
        {
            clone._completedRequests.Add(pair.Key, pair.Value);
        }

        foreach (var pair in _inventory)
        {
            clone._inventory.Add(pair.Key, pair.Value);
        }

        foreach (var pair in _characters)
        {
            clone._characters.Add(pair.Key, pair.Value);
        }

        foreach (var pair in _pityByBanner)
        {
            clone._pityByBanner.Add(pair.Key, pair.Value);
        }

        foreach (var pair in _claimedMailIdempotencyKeys)
        {
            clone._claimedMailIdempotencyKeys.Add(pair.Key, pair.Value);
        }

        clone._pendingGachaDraws.AddRange(_pendingGachaDraws);
        clone._pendingOutboxEvents.AddRange(_pendingOutboxEvents);
        clone._ledger.AddRange(_ledger);
        return clone;
    }
}

public sealed record CompletedIdempotencyRequest(
    string IdempotencyKey,
    string RequestHash,
    string ResponseBody,
    string? SnapshotBody,
    DateTimeOffset CompletedAt,
    DateTimeOffset ExpiresAt);

public sealed record OwnedCharacter(string CharacterId, int Rarity, int DuplicateCount);

public sealed record LedgerEntry(
    long Version,
    CurrencyCode Currency,
    long Delta,
    long BalanceAfter,
    string Reason,
    string IdempotencyKey,
    DateTimeOffset CreatedAt);

public sealed record PendingGachaDraw(
    Guid DrawId,
    Guid PlayerId,
    string BannerId,
    string ContentVersion,
    string ContentChecksum,
    int DrawCount,
    CurrencyCode CostCurrency,
    long CostAmount,
    string RewardsJson,
    int PityBefore,
    int PityAfter,
    string IdempotencyKey,
    DateTimeOffset CreatedAt);
