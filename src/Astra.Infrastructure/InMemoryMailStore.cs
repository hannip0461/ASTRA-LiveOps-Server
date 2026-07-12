using System.Collections.Concurrent;
using Astra.Contracts;
using Astra.Domain;

namespace Astra.Infrastructure;

public sealed class InMemoryMailStore : IMailStore
{
    private readonly ConcurrentDictionary<string, MailRecord> _mails = new(StringComparer.Ordinal);

    public Task<MailDefinitionDto> CreateIncidentMailAsync(
        CreateIncidentMailCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(command);

        var definition = new MailDefinitionDto(
            command.IncidentId,
            command.MailId,
            command.Title,
            command.Body,
            command.Rewards.ToArray(),
            command.Reason,
            DateTimeOffset.UtcNow);

        var targets = command.TargetPlayerIds.Distinct().ToHashSet();
        var record = new MailRecord(definition, targets);
        return Task.FromResult(_mails.GetOrAdd(command.MailId, record).Definition);
    }

    public Task<MailDefinitionDto?> GetDefinitionAsync(
        string mailId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_mails.TryGetValue(mailId, out var record) ? record.Definition : null);
    }

    public Task<bool> IsTargetAsync(
        string mailId,
        Guid playerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _mails.TryGetValue(mailId, out var record) && record.TargetPlayerIds.Contains(playerId));
    }

    private static void Validate(CreateIncidentMailCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.IncidentId))
        {
            throw new InvalidAccountCommandException("Incident id is required.");
        }

        if (string.IsNullOrWhiteSpace(command.MailId))
        {
            throw new InvalidAccountCommandException("Mail id is required.");
        }

        if (command.TargetPlayerIds.Count == 0)
        {
            throw new InvalidAccountCommandException("Mail target snapshot is empty.");
        }

        if (command.Rewards.Count == 0)
        {
            throw new InvalidAccountCommandException("Mail reward is required.");
        }

        foreach (var reward in command.Rewards)
        {
            if (!Enum.IsDefined(reward.Currency) || reward.Amount <= 0)
            {
                throw new InvalidAccountCommandException("Invalid mail reward.");
            }
        }
    }

    private sealed record MailRecord(MailDefinitionDto Definition, HashSet<Guid> TargetPlayerIds);
}
