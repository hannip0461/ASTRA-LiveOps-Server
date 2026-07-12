using Astra.Contracts;

namespace Astra.Domain;

public interface IOutboxOperationsStore
{
    Task<OutboxOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OutboxDeadLetterDto>> ListDeadLettersAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<OutboxReplayResultDto?> ReplayDeadLetterAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);
}
