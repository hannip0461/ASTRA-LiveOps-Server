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
    private readonly List<CompletedIdempotencyRequest> _pendingCompletedRequests = [];
    private readonly List<LedgerEntry> _ledger = [];
    private Dictionary<CurrencyCode, long>? _loadedBalances;
    private Dictionary<string, long>? _loadedInventory;
    private Dictionary<string, OwnedCharacter>? _loadedCharacters;
    private Dictionary<string, int>? _loadedPity;

    public Guid PlayerId { get; } = playerId;

    public IReadOnlyDictionary<CurrencyCode, long> Balances => _balances;

    public IReadOnlyDictionary<string, CompletedIdempotencyRequest> CompletedRequests => _completedRequests;

    public IReadOnlyDictionary<string, long> Inventory => _inventory;

    public IReadOnlyDictionary<string, OwnedCharacter> Characters => _characters;

    public IReadOnlyDictionary<string, int> PityByBanner => _pityByBanner;

    public IReadOnlyDictionary<string, string> ClaimedMailIdempotencyKeys => _claimedMailIdempotencyKeys;

    public IReadOnlyList<PendingGachaDraw> PendingGachaDraws => _pendingGachaDraws;

    public IReadOnlyList<PendingOutboxEvent> PendingOutboxEvents => _pendingOutboxEvents;

    /// <summary>영속 상태를 불러온 뒤 변경된 값이다.</summary>
    public IEnumerable<KeyValuePair<CurrencyCode, long>> ChangedBalances =>
        Changed(_balances, _loadedBalances);

    public IEnumerable<KeyValuePair<string, long>> ChangedInventory =>
        Changed(_inventory, _loadedInventory);

    public IEnumerable<OwnedCharacter> ChangedCharacters =>
        Changed(_characters, _loadedCharacters).Select(pair => pair.Value);

    public IEnumerable<KeyValuePair<string, int>> ChangedPity =>
        Changed(_pityByBanner, _loadedPity);

    /// <summary>현재 작업 단위에서 생성된 멱등성 기록이다.</summary>
    public IReadOnlyList<CompletedIdempotencyRequest> PendingCompletedRequests => _pendingCompletedRequests;

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

    internal void AddCompletedRequest(CompletedIdempotencyRequest request)
    {
        _completedRequests.Add(request.IdempotencyKey, request);
        _pendingCompletedRequests.Add(request);
    }

    internal void HydrateCompletedRequest(CompletedIdempotencyRequest request) =>
        _completedRequests.Add(request.IdempotencyKey, request);

    internal void RemoveCompletedRequest(string idempotencyKey) =>
        _completedRequests.Remove(idempotencyKey);

    internal void ClearPendingCompletedRequests() => _pendingCompletedRequests.Clear();

    /// <summary>변경 추적에 사용할 영속 상태 기준값을 저장한다.</summary>
    internal void MarkHydrated()
    {
        _loadedBalances = new Dictionary<CurrencyCode, long>(_balances);
        _loadedInventory = new Dictionary<string, long>(_inventory, StringComparer.Ordinal);
        _loadedCharacters = new Dictionary<string, OwnedCharacter>(_characters, StringComparer.Ordinal);
        _loadedPity = new Dictionary<string, int>(_pityByBanner, StringComparer.Ordinal);
    }

    private static IEnumerable<KeyValuePair<TKey, TValue>> Changed<TKey, TValue>(
        Dictionary<TKey, TValue> current,
        Dictionary<TKey, TValue>? loaded)
        where TKey : notnull =>
        loaded is null
            ? current
            : current.Where(pair =>
                !loaded.TryGetValue(pair.Key, out var previous) ||
                !EqualityComparer<TValue>.Default.Equals(previous, pair.Value));

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
        clone._pendingCompletedRequests.AddRange(_pendingCompletedRequests);
        clone._ledger.AddRange(_ledger);
        clone._loadedBalances = _loadedBalances is null ? null : new(_loadedBalances);
        clone._loadedInventory = _loadedInventory is null ? null : new(_loadedInventory, StringComparer.Ordinal);
        clone._loadedCharacters = _loadedCharacters is null ? null : new(_loadedCharacters, StringComparer.Ordinal);
        clone._loadedPity = _loadedPity is null ? null : new(_loadedPity, StringComparer.Ordinal);
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
