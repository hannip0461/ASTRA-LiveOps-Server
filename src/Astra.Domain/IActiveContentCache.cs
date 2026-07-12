using Astra.Contracts;

namespace Astra.Domain;

public interface IActiveContentCache
{
    ContentSnapshotDto? GetActiveSnapshot();

    void Update(ContentSnapshotDto? snapshot);
}
