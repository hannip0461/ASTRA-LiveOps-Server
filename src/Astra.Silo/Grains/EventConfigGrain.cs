using Astra.Contracts;
using Astra.Domain;

namespace Astra.Silo.Grains;

public sealed class EventConfigGrain(
    IActiveContentCache activeContentCache,
    IContentSnapshotStore contentStore,
    ContentValidationService validationService) : Grain, IEventConfigGrain
{
    public async Task<ContentSnapshotDto?> GetActiveSnapshotAsync()
    {
        var snapshot = activeContentCache.GetActiveSnapshot();
        if (snapshot is not null)
        {
            return snapshot;
        }

        snapshot = await contentStore.GetActiveAsync();
        activeContentCache.Update(snapshot);
        return snapshot;
    }

    public async Task<ContentPublishResult> PublishAsync(PublishContentCommand command)
    {
        var result = validationService.ValidateAndCreateSnapshot(command);
        if (!result.Published || result.Snapshot is null)
        {
            return result;
        }

        try
        {
            var snapshot = await contentStore.PublishAsync(result.Snapshot);
            activeContentCache.Update(snapshot);
            return new ContentPublishResult(true, snapshot, []);
        }
        catch (ContentVersionConflictException exception)
        {
            return new ContentPublishResult(
                false,
                null,
                [new ContentValidationIssue("content.version.conflict", exception.Message)]);
        }
        catch (ContentVersionInactiveException exception)
        {
            return new ContentPublishResult(
                false,
                null,
                [new ContentValidationIssue("content.version.inactive", exception.Message)]);
        }
    }

    public async Task<ContentPublishResult> RollbackAsync(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return new ContentPublishResult(
                false,
                null,
                [new ContentValidationIssue("content.version.required", "Rollback version is required.")]);
        }

        var normalizedVersion = version.Trim();
        var snapshot = await contentStore.ActivateAsync(normalizedVersion);
        if (snapshot is null)
        {
            return new ContentPublishResult(
                false,
                null,
                [new ContentValidationIssue(
                    "content.version.not_found",
                    $"Content version was not found: {normalizedVersion}.")]);
        }

        activeContentCache.Update(snapshot);
        return new ContentPublishResult(true, snapshot, []);
    }
}
