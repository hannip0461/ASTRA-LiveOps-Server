using Orleans;

namespace Astra.Contracts;

[GenerateSerializer]
public sealed record MailRewardDto(
    [property: Id(0)] CurrencyCode Currency,
    [property: Id(1)] long Amount);

[GenerateSerializer]
public sealed record CreateIncidentMailCommand(
    [property: Id(0)] string IncidentId,
    [property: Id(1)] string MailId,
    [property: Id(2)] string Title,
    [property: Id(3)] string Body,
    [property: Id(4)] IReadOnlyList<Guid> TargetPlayerIds,
    [property: Id(5)] IReadOnlyList<MailRewardDto> Rewards,
    [property: Id(6)] string Reason);

[GenerateSerializer]
public sealed record MailDefinitionDto(
    [property: Id(0)] string IncidentId,
    [property: Id(1)] string MailId,
    [property: Id(2)] string Title,
    [property: Id(3)] string Body,
    [property: Id(4)] IReadOnlyList<MailRewardDto> Rewards,
    [property: Id(5)] string Reason,
    [property: Id(6)] DateTimeOffset CreatedAt);

[GenerateSerializer]
public sealed record ClaimMailCommand(
    [property: Id(0)] string MailId,
    [property: Id(1)] string IdempotencyKey,
    [property: Id(2)] string RequestHash);

[GenerateSerializer]
public sealed record MailClaimResultDto(
    [property: Id(0)] string IncidentId,
    [property: Id(1)] string MailId,
    [property: Id(2)] IReadOnlyList<MailRewardDto> Rewards,
    [property: Id(3)] WalletSnapshotDto Wallet);

public interface IMailboxGrain : IGrainWithStringKey
{
    Task<MailDefinitionDto> CreateIncidentMailAsync(CreateIncidentMailCommand command);

    Task<MailDefinitionDto?> GetDefinitionAsync(string mailId);

    Task<bool> IsTargetAsync(string mailId, Guid playerId);
}
