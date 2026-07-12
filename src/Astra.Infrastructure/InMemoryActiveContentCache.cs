using Astra.Contracts;
using Astra.Domain;

namespace Astra.Infrastructure;

public sealed class InMemoryActiveContentCache : IActiveContentCache
{
    private ContentSnapshotDto? _active;

    public ContentSnapshotDto? GetActiveSnapshot() => Volatile.Read(ref _active);

    public void Update(ContentSnapshotDto? snapshot) => Interlocked.Exchange(ref _active, snapshot);
}
