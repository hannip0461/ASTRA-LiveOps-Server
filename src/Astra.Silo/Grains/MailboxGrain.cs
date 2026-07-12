using Astra.Contracts;
using Astra.Domain;

namespace Astra.Silo.Grains;

public sealed class MailboxGrain(IMailStore mailStore) : Grain, IMailboxGrain
{
    public Task<MailDefinitionDto> CreateIncidentMailAsync(CreateIncidentMailCommand command) =>
        mailStore.CreateIncidentMailAsync(command);

    public Task<MailDefinitionDto?> GetDefinitionAsync(string mailId) =>
        mailStore.GetDefinitionAsync(mailId);

    public Task<bool> IsTargetAsync(string mailId, Guid playerId) =>
        mailStore.IsTargetAsync(mailId, playerId);
}
