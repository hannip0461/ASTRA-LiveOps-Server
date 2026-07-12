using Astra.Contracts;
using Astra.Domain;

namespace Astra.Api;

internal static class EndpointValidation
{
    public const int MaxIdentifierLength = 128;
    public const int MaxReasonLength = 500;
    public const long MaxCurrencyAmount = 1_000_000_000_000;

    public static ValidationErrors PublishContent(PublishContentCommand command)
    {
        var errors = new ValidationErrors();
        errors.Identifier("version", command.Version, MaxIdentifierLength);
        errors.Text("reason", command.Reason, 1, MaxReasonLength);

        if (command.GachaBanners is null || command.GachaBanners.Count is < 1 or > 100)
        {
            errors.Add("gachaBanners", "Between 1 and 100 banners are required.");
            return errors;
        }

        for (var bannerIndex = 0; bannerIndex < command.GachaBanners.Count; bannerIndex++)
        {
            var banner = command.GachaBanners[bannerIndex];
            var prefix = $"gachaBanners[{bannerIndex}]";
            if (banner is null)
            {
                errors.Add(prefix, "Banner must not be null.");
                continue;
            }

            errors.Identifier($"{prefix}.bannerId", banner.BannerId, MaxIdentifierLength);
            errors.Enum($"{prefix}.costCurrency", banner.CostCurrency);
            errors.Range($"{prefix}.costAmount", banner.CostAmount, 1, MaxCurrencyAmount);
            errors.Range($"{prefix}.pityThreshold", banner.PityThreshold, 1, 10_000);
            if (banner.EndsAtUtc <= banner.StartsAtUtc)
            {
                errors.Add($"{prefix}.endsAtUtc", "End time must be after start time.");
            }

            ValidateRewardPool(errors, prefix, banner.RewardPool);
        }

        return errors;
    }

    public static ValidationErrors RollbackContent(string version, RollbackContentCommand command)
    {
        var errors = new ValidationErrors();
        errors.Identifier("version", version, MaxIdentifierLength);
        errors.Text("reason", command.Reason, 1, MaxReasonLength);
        return errors;
    }

    public static ValidationErrors IncidentMail(CreateIncidentMailCommand command)
    {
        var errors = new ValidationErrors();
        errors.Identifier("incidentId", command.IncidentId, MaxIdentifierLength);
        errors.Identifier("mailId", command.MailId, MaxIdentifierLength);
        errors.Text("title", command.Title, 1, 200);
        errors.Text("body", command.Body, 1, 4_000);
        errors.Text("reason", command.Reason, 1, MaxReasonLength);

        if (command.TargetPlayerIds is null || command.TargetPlayerIds.Count is < 1 or > 10_000)
        {
            errors.Add("targetPlayerIds", "Between 1 and 10,000 target players are required.");
        }
        else if (command.TargetPlayerIds.Any(playerId => playerId == Guid.Empty))
        {
            errors.Add("targetPlayerIds", "Target player IDs must not be empty GUIDs.");
        }

        if (command.Rewards is null || command.Rewards.Count is < 1 or > 20)
        {
            errors.Add("rewards", "Between 1 and 20 rewards are required.");
        }
        else
        {
            for (var rewardIndex = 0; rewardIndex < command.Rewards.Count; rewardIndex++)
            {
                var reward = command.Rewards[rewardIndex];
                var prefix = $"rewards[{rewardIndex}]";
                if (reward is null)
                {
                    errors.Add(prefix, "Reward must not be null.");
                    continue;
                }

                errors.Enum($"{prefix}.currency", reward.Currency);
                errors.Range($"{prefix}.amount", reward.Amount, 1, MaxCurrencyAmount);
            }
        }

        return errors;
    }

    public static ValidationErrors CurrencyCommand(
        CurrencyCode currency,
        long amount,
        string? reason,
        string? idempotencyKey)
    {
        var errors = new ValidationErrors();
        errors.Enum("currency", currency);
        errors.Range("amount", amount, 1, MaxCurrencyAmount);
        errors.Text("reason", reason, 1, MaxReasonLength);
        errors.Identifier("idempotencyKey", idempotencyKey, MaxIdentifierLength);
        return errors;
    }

    public static ValidationErrors DrawGacha(DrawGachaRequest request)
    {
        var errors = new ValidationErrors();
        errors.Identifier("bannerId", request.BannerId, MaxIdentifierLength);
        errors.Range("drawCount", request.DrawCount, 1, GachaCommandFactory.MaxDrawCount);
        errors.Identifier("idempotencyKey", request.IdempotencyKey, MaxIdentifierLength);
        return errors;
    }

    public static ValidationErrors ClaimMail(ClaimMailCommand command)
    {
        var errors = new ValidationErrors();
        errors.Identifier("mailId", command.MailId, MaxIdentifierLength);
        errors.Identifier("idempotencyKey", command.IdempotencyKey, MaxIdentifierLength);
        return errors;
    }

    public static ValidationErrors Identifier(string field, string? value)
    {
        var errors = new ValidationErrors();
        errors.Identifier(field, value, MaxIdentifierLength);
        return errors;
    }

    public static ValidationErrors AuditLimit(int? limit)
    {
        var errors = new ValidationErrors();
        if (limit is < 1 or > 200)
        {
            errors.Add("limit", "Limit must be between 1 and 200.");
        }

        return errors;
    }

    public static ValidationErrors ReplayOutbox(
        Guid eventId,
        ReplayOutboxEventCommand? command)
    {
        var errors = new ValidationErrors();
        if (eventId == Guid.Empty)
        {
            errors.Add("eventId", "Event ID must not be empty.");
        }

        errors.Text("reason", command?.Reason, 1, MaxReasonLength);
        return errors;
    }

    public static PublishContentCommand Normalize(PublishContentCommand command) => command with
    {
        Version = command.Version.Trim(),
        Reason = command.Reason.Trim(),
        GachaBanners = command.GachaBanners.Select(banner => banner with
        {
            BannerId = banner.BannerId.Trim(),
            RewardPool = banner.RewardPool.Select(reward => reward with
            {
                RewardId = reward.RewardId.Trim(),
                DuplicateItemId = string.IsNullOrWhiteSpace(reward.DuplicateItemId)
                    ? null
                    : reward.DuplicateItemId.Trim()
            }).ToArray()
        }).ToArray()
    };

    public static CreateIncidentMailCommand Normalize(CreateIncidentMailCommand command) => command with
    {
        IncidentId = command.IncidentId.Trim(),
        MailId = command.MailId.Trim(),
        Title = command.Title.Trim(),
        Body = command.Body.Trim(),
        Reason = command.Reason.Trim(),
        TargetPlayerIds = command.TargetPlayerIds.Distinct().ToArray(),
        Rewards = command.Rewards.ToArray()
    };

    public static IResult Invalid(HttpContext context, ValidationErrors errors) =>
        ApiProblemDetails.Validation(context, errors.ToDictionary());

    public static IResult ContentRejected(
        HttpContext context,
        IReadOnlyList<ContentValidationIssue> issues)
    {
        var errors = issues.Count == 0
            ? new Dictionary<string, string[]> { ["content"] = ["Content publish was rejected."] }
            : issues.GroupBy(issue => issue.Code, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(issue => issue.Message).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
        return ApiProblemDetails.Validation(
            context,
            errors,
            "content_rejected",
            "Content publish rejected");
    }

    private static void ValidateRewardPool(
        ValidationErrors errors,
        string bannerPrefix,
        IReadOnlyList<GachaRewardPoolEntryDto>? rewards)
    {
        if (rewards is null || rewards.Count is < 1 or > 500)
        {
            errors.Add($"{bannerPrefix}.rewardPool", "Between 1 and 500 rewards are required.");
            return;
        }

        for (var rewardIndex = 0; rewardIndex < rewards.Count; rewardIndex++)
        {
            var reward = rewards[rewardIndex];
            var prefix = $"{bannerPrefix}.rewardPool[{rewardIndex}]";
            if (reward is null)
            {
                errors.Add(prefix, "Reward must not be null.");
                continue;
            }

            errors.Enum($"{prefix}.kind", reward.Kind);
            errors.Identifier($"{prefix}.rewardId", reward.RewardId, MaxIdentifierLength);
            errors.Range($"{prefix}.quantity", reward.Quantity, 1, 1_000_000);
            errors.Range($"{prefix}.rarity", reward.Rarity, 1, 100);
            errors.Range($"{prefix}.weight", reward.Weight, 1, int.MaxValue);

            if (reward.Kind == GachaRewardKind.Character)
            {
                errors.Identifier(
                    $"{prefix}.duplicateItemId",
                    reward.DuplicateItemId,
                    MaxIdentifierLength);
                errors.Range($"{prefix}.duplicateItemQuantity", reward.DuplicateItemQuantity, 1, 1_000_000);
            }
            else if (!string.IsNullOrWhiteSpace(reward.DuplicateItemId) || reward.DuplicateItemQuantity != 0)
            {
                errors.Add(
                    $"{prefix}.duplicateItemId",
                    "Only character rewards can define duplicate conversion.");
            }
        }
    }
}

internal sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = [];
            _errors.Add(field, messages);
        }

        messages.Add(message);
    }

    public void Identifier(string field, string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length is < 1 || normalized.Length > maxLength)
        {
            Add(field, $"Value must contain between 1 and {maxLength} characters.");
            return;
        }

        if (normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':'))
        {
            Add(field, "Value contains unsupported characters.");
        }
    }

    public void Text(string field, string? value, int minLength, int maxLength)
    {
        var length = value?.Trim().Length ?? 0;
        if (length < minLength || length > maxLength)
        {
            Add(field, $"Value must contain between {minLength} and {maxLength} characters.");
        }
    }

    public void Enum<T>(string field, T value) where T : struct, Enum
    {
        if (!System.Enum.IsDefined(value))
        {
            Add(field, "Value is not supported.");
        }
    }

    public void Range(string field, long value, long minimum, long maximum)
    {
        if (value < minimum || value > maximum)
        {
            Add(field, $"Value must be between {minimum} and {maximum}.");
        }
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
}
