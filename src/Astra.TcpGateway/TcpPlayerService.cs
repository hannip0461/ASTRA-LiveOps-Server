using Astra.Contracts;
using Orleans;

namespace Astra.TcpGateway;

internal interface ITcpPlayerService
{
    Task<WalletSnapshotDto> GetWalletAsync(Guid playerId, CancellationToken cancellationToken);

    Task<PlayerCommandReceipt> DrawGachaAsync(
        Guid playerId,
        DrawGachaRequest request,
        CancellationToken cancellationToken);
}

internal sealed class OrleansTcpPlayerService(IClusterClient clusterClient) : ITcpPlayerService
{
    public Task<WalletSnapshotDto> GetWalletAsync(Guid playerId, CancellationToken cancellationToken) =>
        clusterClient.GetGrain<IPlayerAccountGrain>(playerId).GetSnapshotAsync().WaitAsync(cancellationToken);

    public Task<PlayerCommandReceipt> DrawGachaAsync(
        Guid playerId,
        DrawGachaRequest request,
        CancellationToken cancellationToken) =>
        clusterClient.GetGrain<IPlayerAccountGrain>(playerId).DrawGachaAsync(request).WaitAsync(cancellationToken);
}
