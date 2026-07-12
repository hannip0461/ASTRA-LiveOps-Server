using Astra.Contracts;
using Astra.Domain;

namespace Astra.Infrastructure;

public sealed class InMemoryContentSnapshotStore : IContentSnapshotStore
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, ContentSnapshotDto> _snapshots = new(StringComparer.Ordinal);
    private ContentSnapshotDto? _active;

    public Task<ContentSnapshotDto?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            return Task.FromResult(_active);
        }
    }

    public Task<ContentSnapshotDto> PublishAsync(
        ContentSnapshotDto snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            if (_snapshots.TryGetValue(snapshot.Version, out var existing))
            {
                EnsureSameChecksum(existing, snapshot);
                if (!StringComparer.Ordinal.Equals(_active?.Version, existing.Version))
                {
                    throw new ContentVersionInactiveException(
                        $"Content version '{snapshot.Version}' already exists but is not active; use rollback to reactivate it.");
                }

                return Task.FromResult(existing);
            }

            _snapshots.Add(snapshot.Version, snapshot);
            _active = snapshot;
            return Task.FromResult(snapshot);
        }
    }

    public Task<ContentSnapshotDto?> ActivateAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            if (!_snapshots.TryGetValue(version, out var snapshot))
            {
                return Task.FromResult<ContentSnapshotDto?>(null);
            }

            if (!StringComparer.Ordinal.Equals(_active?.Version, snapshot.Version))
            {
                _active = snapshot;
            }

            return Task.FromResult<ContentSnapshotDto?>(snapshot);
        }
    }

    private static void EnsureSameChecksum(ContentSnapshotDto existing, ContentSnapshotDto candidate)
    {
        if (!StringComparer.Ordinal.Equals(existing.Checksum, candidate.Checksum))
        {
            throw new ContentVersionConflictException(
                $"Content version '{candidate.Version}' already exists with a different checksum.");
        }
    }
}
