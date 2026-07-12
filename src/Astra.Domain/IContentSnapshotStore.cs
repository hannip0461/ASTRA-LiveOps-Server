using Astra.Contracts;

namespace Astra.Domain;

public interface IContentSnapshotStore
{
    Task<ContentSnapshotDto?> GetActiveAsync(CancellationToken cancellationToken = default);

    Task<ContentSnapshotDto> PublishAsync(
        ContentSnapshotDto snapshot,
        CancellationToken cancellationToken = default);

    Task<ContentSnapshotDto?> ActivateAsync(
        string version,
        CancellationToken cancellationToken = default);
}
