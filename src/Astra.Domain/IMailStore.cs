using Astra.Contracts;

namespace Astra.Domain;

public interface IMailStore
{
    Task<MailDefinitionDto> CreateIncidentMailAsync(
        CreateIncidentMailCommand command,
        CancellationToken cancellationToken = default);

    Task<MailDefinitionDto?> GetDefinitionAsync(
        string mailId,
        CancellationToken cancellationToken = default);

    Task<bool> IsTargetAsync(
        string mailId,
        Guid playerId,
        CancellationToken cancellationToken = default);
}
