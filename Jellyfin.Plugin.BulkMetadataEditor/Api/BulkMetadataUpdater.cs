using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkMetadataEditor.Api;

/// <summary>
/// Applies a set of metadata changes to library items. Mirrors the behaviour of the stock
/// ItemUpdateController, but only touches fields that are part of the request.
/// </summary>
public class BulkMetadataUpdater
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BulkMetadataUpdater"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public BulkMetadataUpdater(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILogger logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Applies the request to every item in it.
    /// </summary>
    /// <param name="request">The bulk update request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The per-item results.</returns>
    public async Task<BulkUpdateResponse> ApplyAsync(BulkUpdateRequest request, CancellationToken cancellationToken)
    {
        var response = new BulkUpdateResponse();

        foreach (var id in request.ItemIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                response.Results.Add(new BulkItemResult { Id = id, Error = "Item not found" });
                continue;
            }

            var result = new BulkItemResult { Id = id, Name = item.Name };
            try
            {
                await ApplyToItemAsync(item, request, result, cancellationToken).ConfigureAwait(false);
                result.Success = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Bulk metadata update failed for {ItemName} ({ItemId})", item.Name, item.Id);
                result.Error = ex.Message;
            }

            response.Results.Add(result);
        }

        return response;
    }

    private static DateTime NormalizeDateTime(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] CleanValues(IEnumerable<string> values)
        => values.Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] ApplyList(IEnumerable<string>? current, ListChange change)
    {
        var values = CleanValues(change.Values);
        var existing = current ?? [];

        return change.Mode switch
        {
            ListMode.Replace => values,
            ListMode.Add => CleanValues(existing.Concat(values)),
            ListMode.Remove => existing.Where(c => !values.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray(),
            _ => existing.ToArray()
        };
    }

    private static T[] ApplyEnumList<T>(IEnumerable<T>? current, ListChange change, BulkItemResult result, string field)
        where T : struct, Enum
    {
        var values = new List<T>();
        foreach (var raw in change.Values)
        {
            if (Enum.TryParse<T>(raw, true, out var parsed))
            {
                values.Add(parsed);
            }
            else
            {
                result.Skipped.Add($"{field} value '{raw}'");
            }
        }

        var existing = current ?? [];
        return change.Mode switch
        {
            ListMode.Replace => values.Distinct().ToArray(),
            ListMode.Add => existing.Concat(values).Distinct().ToArray(),
            ListMode.Remove => existing.Where(c => !values.Contains(c)).ToArray(),
            _ => existing.ToArray()
        };
    }

    private async Task ApplyToItemAsync(BaseItem item, BulkUpdateRequest request, BulkItemResult result, CancellationToken cancellationToken)
    {
        var changes = request.Changes;
        var applied = result.Applied;
        var skipped = result.Skipped;
        var lockFields = new HashSet<MetadataField>();

        var wasLocked = item.IsLocked;
        var series = item as Series;
        var oldDisplayOrder = series?.DisplayOrder ?? string.Empty;

        // People are written first so metadata savers pick them up from the database, as in the stock editor.
        if (changes.People is not null)
        {
            ApplyPeople(item, changes.People);
            applied.Add("People");
            lockFields.Add(MetadataField.Cast);
        }

        if (changes.Name is not null)
        {
            var name = NullIfBlank(changes.Name.Value);
            if (name is null)
            {
                skipped.Add("Name (cannot be empty)");
            }
            else
            {
                item.Name = name;
                applied.Add("Name");
                lockFields.Add(MetadataField.Name);
            }
        }

        if (changes.OriginalTitle is not null)
        {
            item.OriginalTitle = NullIfBlank(changes.OriginalTitle.Value);
            applied.Add("OriginalTitle");
        }

        if (changes.ForcedSortName is not null)
        {
            item.ForcedSortName = NullIfBlank(changes.ForcedSortName.Value);
            applied.Add("ForcedSortName");
        }

        if (changes.OriginalLanguage is not null)
        {
            item.OriginalLanguage = NullIfBlank(changes.OriginalLanguage.Value);
            applied.Add("OriginalLanguage");
        }

        if (changes.DateCreated is not null)
        {
            if (changes.DateCreated.Value.HasValue)
            {
                item.DateCreated = NormalizeDateTime(changes.DateCreated.Value.Value);
                applied.Add("DateCreated");
            }
            else
            {
                skipped.Add("DateCreated (cannot be empty)");
            }
        }

        if (changes.PremiereDate is not null)
        {
            item.PremiereDate = changes.PremiereDate.Value.HasValue ? NormalizeDateTime(changes.PremiereDate.Value.Value) : null;
            applied.Add("PremiereDate");
        }

        if (changes.EndDate is not null)
        {
            item.EndDate = changes.EndDate.Value.HasValue ? NormalizeDateTime(changes.EndDate.Value.Value) : null;
            applied.Add("EndDate");
        }

        if (changes.ProductionYear is not null)
        {
            item.ProductionYear = changes.ProductionYear.Value;
            applied.Add("ProductionYear");
        }

        if (changes.CommunityRating is not null)
        {
            item.CommunityRating = changes.CommunityRating.Value;
            applied.Add("CommunityRating");
        }

        if (changes.CriticRating is not null)
        {
            item.CriticRating = changes.CriticRating.Value;
            applied.Add("CriticRating");
        }

        if (changes.Overview is not null)
        {
            item.Overview = NullIfBlank(changes.Overview.Value);
            applied.Add("Overview");
            lockFields.Add(MetadataField.Overview);
        }

        if (changes.Tagline is not null)
        {
            item.Tagline = NullIfBlank(changes.Tagline.Value);
            applied.Add("Tagline");
        }

        string? childOfficialRating = null;
        var officialRatingChanged = false;
        if (changes.OfficialRating is not null)
        {
            childOfficialRating = NullIfBlank(changes.OfficialRating.Value);
            item.OfficialRating = childOfficialRating;
            officialRatingChanged = true;
            applied.Add("OfficialRating");
            lockFields.Add(MetadataField.OfficialRating);
        }

        string? childCustomRating = null;
        var customRatingChanged = false;
        if (changes.CustomRating is not null)
        {
            childCustomRating = NullIfBlank(changes.CustomRating.Value);
            item.CustomRating = childCustomRating;
            customRatingChanged = true;
            applied.Add("CustomRating");
        }

        if (changes.Genres is not null)
        {
            item.Genres = ApplyList(item.Genres, changes.Genres);
            applied.Add("Genres");
            lockFields.Add(MetadataField.Genres);
        }

        string[] addedTags = [];
        string[] removedTags = [];
        if (changes.Tags is not null)
        {
            var oldTags = item.Tags ?? [];
            var newTags = ApplyList(oldTags, changes.Tags);
            addedTags = newTags.Except(oldTags, StringComparer.OrdinalIgnoreCase).ToArray();
            removedTags = oldTags.Except(newTags, StringComparer.OrdinalIgnoreCase).ToArray();
            item.Tags = newTags;
            applied.Add("Tags");
            lockFields.Add(MetadataField.Tags);
        }

        if (changes.Studios is not null)
        {
            item.Studios = ApplyList(item.Studios, changes.Studios);
            applied.Add("Studios");
            lockFields.Add(MetadataField.Studios);
        }

        if (changes.ProductionLocations is not null)
        {
            item.ProductionLocations = ApplyList(item.ProductionLocations, changes.ProductionLocations);
            applied.Add("ProductionLocations");
            lockFields.Add(MetadataField.ProductionLocations);
        }

        if (changes.PreferredMetadataLanguage is not null)
        {
            item.PreferredMetadataLanguage = NullIfBlank(changes.PreferredMetadataLanguage.Value);
            applied.Add("PreferredMetadataLanguage");
        }

        if (changes.PreferredMetadataCountryCode is not null)
        {
            item.PreferredMetadataCountryCode = NullIfBlank(changes.PreferredMetadataCountryCode.Value);
            applied.Add("PreferredMetadataCountryCode");
        }

        if (changes.ProviderIds is not null)
        {
            foreach (var key in changes.ProviderIds.Remove)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    item.ProviderIds.Remove(key.Trim());
                }
            }

            foreach (var (key, value) in changes.ProviderIds.Set)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    item.ProviderIds.Remove(key.Trim());
                }
                else
                {
                    item.SetProviderId(key.Trim(), value.Trim());
                }
            }

            applied.Add("ProviderIds");
        }

        if (changes.IndexNumber is not null)
        {
            item.IndexNumber = changes.IndexNumber.Value;
            applied.Add("IndexNumber");
        }

        if (changes.ParentIndexNumber is not null)
        {
            item.ParentIndexNumber = changes.ParentIndexNumber.Value;
            applied.Add("ParentIndexNumber");
        }

        ApplyTypeSpecific(item, changes, result, lockFields);

        if (changes.LockData is not null)
        {
            item.IsLocked = changes.LockData.Value;
            applied.Add("LockData");
        }

        if (changes.LockedFields is not null)
        {
            item.LockedFields = ApplyEnumList(item.LockedFields, changes.LockedFields, result, "LockedFields");
            applied.Add("LockedFields");
        }

        if (request.LockEditedFields && lockFields.Count > 0)
        {
            item.LockedFields = (item.LockedFields ?? []).Union(lockFields).ToArray();
        }

        if (applied.Count == 0)
        {
            return;
        }

        item.OnMetadataChanged();
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

        if (officialRatingChanged || customRatingChanged || addedTags.Length > 0 || removedTags.Length > 0)
        {
            await PropagateToChildrenAsync(
                item,
                officialRatingChanged,
                childOfficialRating,
                customRatingChanged,
                childCustomRating,
                addedTags,
                removedTags,
                cancellationToken).ConfigureAwait(false);
        }

        if (item.IsLocked != wasLocked && item is Folder folder)
        {
            foreach (var child in folder.GetRecursiveChildren())
            {
                child.IsLocked = item.IsLocked;
                await child.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
        }

        if (series is not null && !string.Equals(oldDisplayOrder, series.DisplayOrder ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            _providerManager.QueueRefresh(
                series.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = true
                },
                RefreshPriority.High);
        }
    }

    private void ApplyTypeSpecific(BaseItem item, MetadataChanges changes, BulkItemResult result, HashSet<MetadataField> lockFields)
    {
        var applied = result.Applied;
        var skipped = result.Skipped;

        if (changes.Status is not null)
        {
            if (item is Series s)
            {
                var raw = NullIfBlank(changes.Status.Value);
                if (raw is null)
                {
                    s.Status = null;
                    applied.Add("Status");
                }
                else if (Enum.TryParse<SeriesStatus>(raw, true, out var status))
                {
                    s.Status = status;
                    applied.Add("Status");
                }
                else
                {
                    skipped.Add($"Status (unknown value '{raw}')");
                }
            }
            else
            {
                skipped.Add("Status");
            }
        }

        if (changes.AirDays is not null)
        {
            if (item is Series s)
            {
                s.AirDays = ApplyEnumList(s.AirDays, changes.AirDays, result, "AirDays");
                applied.Add("AirDays");
            }
            else
            {
                skipped.Add("AirDays");
            }
        }

        if (changes.AirTime is not null)
        {
            if (item is Series s)
            {
                s.AirTime = NullIfBlank(changes.AirTime.Value);
                applied.Add("AirTime");
            }
            else
            {
                skipped.Add("AirTime");
            }
        }

        if (changes.RunTimeMinutes is not null)
        {
            // Runtimes for media files come from ffprobe; the stock editor only allows this for series.
            if (item is Series)
            {
                item.RunTimeTicks = changes.RunTimeMinutes.Value.HasValue
                    ? TimeSpan.FromMinutes(changes.RunTimeMinutes.Value.Value).Ticks
                    : null;
                applied.Add("RunTime");
                lockFields.Add(MetadataField.Runtime);
            }
            else
            {
                skipped.Add("RunTime");
            }
        }

        if (changes.DisplayOrder is not null)
        {
            if (item is IHasDisplayOrder hasDisplayOrder)
            {
                hasDisplayOrder.DisplayOrder = NullIfBlank(changes.DisplayOrder.Value);
                applied.Add("DisplayOrder");
            }
            else
            {
                skipped.Add("DisplayOrder");
            }
        }

        if (changes.AspectRatio is not null)
        {
            if (item is IHasAspectRatio hasAspectRatio)
            {
                hasAspectRatio.AspectRatio = NullIfBlank(changes.AspectRatio.Value);
                applied.Add("AspectRatio");
            }
            else
            {
                skipped.Add("AspectRatio");
            }
        }

        if (changes.Video3DFormat is not null)
        {
            if (item is Video video)
            {
                var raw = NullIfBlank(changes.Video3DFormat.Value);
                if (raw is null)
                {
                    video.Video3DFormat = null;
                    applied.Add("Video3DFormat");
                }
                else if (Enum.TryParse<Video3DFormat>(raw, true, out var format))
                {
                    video.Video3DFormat = format;
                    applied.Add("Video3DFormat");
                }
                else
                {
                    skipped.Add($"Video3DFormat (unknown value '{raw}')");
                }
            }
            else
            {
                skipped.Add("Video3DFormat");
            }
        }

        if (changes.Album is not null)
        {
            switch (item)
            {
                case Audio song:
                    song.Album = NullIfBlank(changes.Album.Value);
                    applied.Add("Album");
                    break;
                case MusicVideo musicVideo:
                    musicVideo.Album = NullIfBlank(changes.Album.Value);
                    applied.Add("Album");
                    break;
                default:
                    skipped.Add("Album");
                    break;
            }
        }

        if (changes.AlbumArtists is not null)
        {
            if (item is IHasAlbumArtist hasAlbumArtist)
            {
                hasAlbumArtist.AlbumArtists = ApplyList(hasAlbumArtist.AlbumArtists, changes.AlbumArtists);
                applied.Add("AlbumArtists");
            }
            else
            {
                skipped.Add("AlbumArtists");
            }
        }

        if (changes.Artists is not null)
        {
            if (item is IHasArtist hasArtist)
            {
                hasArtist.Artists = ApplyList(hasArtist.Artists, changes.Artists);
                applied.Add("Artists");
            }
            else
            {
                skipped.Add("Artists");
            }
        }

        var episode = item as Episode;
        ApplyEpisodeNumber(episode, changes.AirsBeforeSeasonNumber, "AirsBeforeSeasonNumber", result, (e, v) => e.AirsBeforeSeasonNumber = v);
        ApplyEpisodeNumber(episode, changes.AirsAfterSeasonNumber, "AirsAfterSeasonNumber", result, (e, v) => e.AirsAfterSeasonNumber = v);
        ApplyEpisodeNumber(episode, changes.AirsBeforeEpisodeNumber, "AirsBeforeEpisodeNumber", result, (e, v) => e.AirsBeforeEpisodeNumber = v);
    }

    private static void ApplyEpisodeNumber(Episode? episode, IntegerChange? change, string field, BulkItemResult result, Action<Episode, int?> setter)
    {
        if (change is null)
        {
            return;
        }

        if (episode is null)
        {
            result.Skipped.Add(field);
            return;
        }

        setter(episode, change.Value);
        result.Applied.Add(field);
    }

    private void ApplyPeople(BaseItem item, PeopleChange change)
    {
        var incoming = change.People
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new
            {
                Name = p.Name.Trim(),
                Kind = Enum.TryParse<PersonKind>(p.Type, true, out var kind) ? kind : (PersonKind?)null,
                Role = NullIfBlank(p.Role)
            })
            .ToList();

        var current = _libraryManager.GetPeople(item);
        List<PersonInfo> updated;

        switch (change.Mode)
        {
            case ListMode.Replace:
                updated = incoming
                    .Select(p => new PersonInfo { Name = p.Name, Type = p.Kind ?? PersonKind.Unknown, Role = p.Role })
                    .ToList();
                break;

            case ListMode.Add:
                updated = current.ToList();
                foreach (var p in incoming)
                {
                    var kind = p.Kind ?? PersonKind.Unknown;
                    var exists = updated.Any(c => c.Type == kind && string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                    {
                        updated.Add(new PersonInfo { Name = p.Name, Type = kind, Role = p.Role });
                    }
                }

                break;

            case ListMode.Remove:
                updated = current
                    .Where(c => !incoming.Any(p =>
                        string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase)
                        && (p.Kind is null || p.Kind == c.Type)))
                    .ToList();
                break;

            default:
                return;
        }

        _libraryManager.UpdatePeople(item, updated);
    }

    private static async Task PropagateToChildrenAsync(
        BaseItem item,
        bool officialRatingChanged,
        string? officialRating,
        bool customRatingChanged,
        string? customRating,
        string[] addedTags,
        string[] removedTags,
        CancellationToken cancellationToken)
    {
        // Same child propagation as the stock editor: series -> seasons/episodes, season -> episodes, album -> tracks.
        IEnumerable<BaseItem> children = item switch
        {
            Series s => s.Children.OfType<Season>().SelectMany(season => new BaseItem[] { season }.Concat(season.Children.OfType<Episode>())),
            Season season => season.Children.OfType<Episode>(),
            MusicAlbum album => album.Children,
            _ => []
        };

        foreach (var child in children)
        {
            if (officialRatingChanged && !child.LockedFields.Contains(MetadataField.OfficialRating))
            {
                child.OfficialRating = officialRating;
            }

            if (customRatingChanged)
            {
                child.CustomRating = customRating;
            }

            if ((addedTags.Length > 0 || removedTags.Length > 0) && !child.LockedFields.Contains(MetadataField.Tags))
            {
                child.Tags = child.Tags
                    .Concat(addedTags)
                    .Except(removedTags, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            child.OnMetadataChanged();
            await child.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }
}
