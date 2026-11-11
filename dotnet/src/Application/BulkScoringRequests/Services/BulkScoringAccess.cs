using System.Text.Json;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests.Services;

// Shared by the bulk scoring handlers: feature gate, job authorization (404 for jobs the caller cannot
// see, 403 for read-only callers), limits snapshot, permitted-roots check, and DTO mapping.
public sealed class BulkScoringAccess(
    BulkScoringOptions options,
    IJobRepository jobs,
    ICurrentUserService currentUser,
    IUploadSettingsRepository uploadSettings,
    IBulkImportSettingsRepository bulkSettings,
    ISharePointSource source,
    IBulkRequestAuthorizationRepository authorizations,
    IOrganizationRepository? organizations = null,
    IUserRepository? users = null,
    IBulkScoringRepository? repository = null,
    IUserDirectory? userDirectory = null)
{
    public BulkScoringOptions Options => options;

    public string Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication is required.");

    public bool IsAdmin => currentUser.IsAdmin;

    public void EnsureEnabled()
    {
        if (!options.Enabled) throw BulkApiException.Unavailable();
    }

    public async Task<Job> RequireJobAsync(string jobId, bool mutate, CancellationToken ct)
    {
        EnsureEnabled();
        var job = await jobs.GetByIdWithoutApplicationsAsync(jobId, ct)
            ?? await jobs.GetByIdAsync(jobId, ct)
            ?? throw BulkApiException.NotFound("The job was not found.");
        try
        {
            if (mutate) await JobAuthorization.EnsureCanMutateAsync(job, currentUser, organizations, ct);
            else await JobAuthorization.EnsureCanReadAsync(job, currentUser, organizations, ct);
        }
        catch (UnauthorizedAccessException)
        {
            // A caller who can read but not mutate gets 403; anyone else must not learn the job exists.
            if (mutate && await CanReadAsync(job, ct)) throw BulkApiException.Forbidden("You can view this job but not change it.");
            throw BulkApiException.NotFound("The job was not found.");
        }
        catch (InvalidJobScopeException)
        {
            throw BulkApiException.NotFound("The job was not found.");
        }
        return job;
    }

    public SourceCallContext InteractiveContext(string? connectionId = null) =>
        new(SourcePhase.Interactive, Actor, ConnectionId: connectionId);

    public async Task<Job> RequireFreshConfirmationAccessAsync(
        BulkScoringRequest request,
        CancellationToken ct) =>
        await RequireJobAsync(request.JobId, mutate: true, ct);

    public async Task RequireFreshExactFolderAccessAsync(
        BulkScoringRequest request,
        CancellationToken ct)
    {
        var resolved = await ResolveAsync(
            request.SourceWebUrl,
            InteractiveContext(request.ConnectionId),
            ct);
        if (resolved.Kind != request.SourceKind
            || !string.Equals(resolved.TenantId, request.SourceTenantId, StringComparison.Ordinal)
            || !string.Equals(resolved.DriveId, request.SourceDriveId, StringComparison.Ordinal)
            || !string.Equals(resolved.ItemId, request.SourceFolderItemId, StringComparison.Ordinal))
            throw BulkApiException.AccessDenied();
    }

    public async Task<BulkRequestLimits> SnapshotLimitsAsync(CancellationToken ct)
    {
        var perFile = (await uploadSettings.GetPersistedAsync(ct))?.MaxIndividualFileBytes ?? UploadSettings.DefaultMaxIndividualFileBytes;
        var maxFiles = (await bulkSettings.GetPersistedAsync(ct))?.MaxFilesPerRequest ?? BulkImportSettings.DefaultMaxFilesPerRequest;
        return BulkRequestLimits.Snapshot(options, perFile, maxFiles);
    }

    [Obsolete("Delegated v3 verifies the exact folder with the connected user and has no deployment allow-list.")]
    public async Task EnsureWithinPermittedRootsAsync(SourceFolder folder, SourceCallContext context, CancellationToken ct)
    {
        await Task.CompletedTask;
    }

    public async Task<SourceFolder> ResolveAsync(string folderUrl, SourceCallContext context, CancellationToken ct)
    {
        try
        {
            return await source.ResolveFolderAsync(folderUrl, context, ct);
        }
        catch (SourceAccessDeniedException) { throw BulkApiException.AccessDenied(); }
        catch (SourceNotFoundException) { throw BulkApiException.Invalid("The folder could not be found."); }
        catch (UnsupportedSourceException) { throw BulkApiException.UnsupportedSource(); }
    }

    public static SourceFolder FolderOf(BulkScoringRequest request) => new(
        request.SourceKind, request.SourceDriveId, request.SourceFolderItemId,
        request.SourceFolderName, request.SourceFolderPath, request.SourceWebUrl, request.SourceTenantId);

    public BulkDiscoveryProgressDto Progress(BulkScoringRequest request, BulkInclusionTotals totals) => new(
        request.DiscoveryStatus.ToWire(),
        request.EntriesScanned,
        request.DiscoveryStepCount,
        totals.Count,
        totals.Bytes,
        totals.CountIfRasterized,
        totals.BytesIfRasterized,
        request.ExcludedCount,
        BulkExclusionCategories.All.ToDictionary(c => c.ToWire(), c => request.GetExcludedByReason().GetValueOrDefault(c)),
        request.InaccessibleCount);

    public static BulkLimitsDto LimitsDto(BulkRequestLimits limits) => new(
        limits.MaxFilesPerRequest, limits.MaxBytesPerFile, limits.MaxTotalBytes, limits.MaxPagesPerDocument, limits.DiscoveryStepFiles);

    public async Task<BulkRequestDetailDto> DetailAsync(BulkScoringRequest request, Job? job, BulkInclusionTotals? totals, CancellationToken ct)
        => await BulkScoringQueryProjections.DetailAsync(this, repository, request, job, totals, ct);

    public async Task<BulkRequestSummaryDto> SummaryAsync(
        BulkScoringRequest request,
        Job? job,
        CancellationToken ct) =>
        await BulkScoringQueryProjections.SummaryAsync(this, repository, request, job, ct);

    public static BulkRequestLimits FrozenLimits(BulkScoringRequest request) => new(
        request.MaxFilesPerRequest,
        request.MaxBytesPerFile,
        request.MaxTotalBytes,
        30,
        request.DiscoveryStepMaxFiles,
        request.DiscoveryStepMaxEntries,
        request.DiscoveryStepMaxSeconds,
        5,
        request.RasterDpi,
        80);

    internal async Task<IReadOnlyDictionary<string, string>> ActorDisplayNamesAsync(
        IEnumerable<string?> actorIds,
        CancellationToken ct)
    {
        var ids = actorIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (userDirectory is not null)
            return await userDirectory.ResolveDisplayNamesAsync(ids, ct);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var user = users is null ? null : await users.GetByIdAsync(id, ct);
            names[id] = !string.IsNullOrWhiteSpace(user?.FullName) ? user!.FullName : id;
        }
        return names;
    }

    // A lapse stays visible after the authorization is cleaned up, so the recruiter is still told to sign in again.
    internal async Task<string> AuthorizationStateAsync(BulkScoringRequest request, CancellationToken ct)
    {
        if (request.Status == BulkRequestStatus.Draft) return "pending";
        var authorization = await authorizations.GetAsync(request.Id, ct);
        if (authorization is null) return BulkAuthorizationStatus.UnavailableHistorical.ToWire();
        return authorization.LapsedAt is not null ? BulkAuthorizationStatus.Lapsed.ToWire() : authorization.Status.ToWire();
    }

    private async Task<bool> CanReadAsync(Job job, CancellationToken ct)
    {
        try { await JobAuthorization.EnsureCanReadAsync(job, currentUser, organizations, ct); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidJobScopeException) { return false; }
    }
}
