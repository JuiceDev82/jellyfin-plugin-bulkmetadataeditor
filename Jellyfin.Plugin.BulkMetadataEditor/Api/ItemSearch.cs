using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.BulkMetadataEditor.Api;

/// <summary>
/// The filters of the item list, as sent to the <c>/Items</c> API, plus the filters <c>/Items</c> lacks.
/// </summary>
public class ItemSearchQuery
{
    /// <summary>Gets or sets text the item's file or folder name must contain.</summary>
    public string? FileName { get; set; }

    /// <summary>Gets or sets pipe-delimited tags the item must not have.</summary>
    public string? ExcludeTags { get; set; }

    public Guid? UserId { get; set; }

    public string? IncludeItemTypes { get; set; }

    public Guid? ParentId { get; set; }

    /// <summary>Gets or sets text the title (or original title) must contain.</summary>
    public string? SearchTerm { get; set; }

    /// <summary>Gets or sets pipe-delimited genre names.</summary>
    public string? Genres { get; set; }

    /// <summary>Gets or sets pipe-delimited tags.</summary>
    public string? Tags { get; set; }

    /// <summary>Gets or sets comma-delimited years.</summary>
    public string? Years { get; set; }

    public bool? IsLocked { get; set; }

    public bool? HasOverview { get; set; }

    public bool? HasOfficialRating { get; set; }

    public bool? HasImdbId { get; set; }

    public bool? HasTmdbId { get; set; }

    public bool? HasTvdbId { get; set; }

    /// <summary>Gets or sets comma-delimited <see cref="ItemSortBy"/> names.</summary>
    public string? SortBy { get; set; }

    public string? SortOrder { get; set; }
}

/// <summary>
/// An item matched by a search.
/// </summary>
public class ItemSearchItem
{
    public Guid Id { get; set; }

    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Every item matched by a search, in list order.
/// </summary>
public class ItemSearchResult
{
    public int TotalRecordCount { get; set; }

    public List<ItemSearchItem> Items { get; set; } = [];
}

/// <summary>
/// Runs the item list's filters together with what <c>/Items</c> cannot do: match a file name, exclude
/// tags, and search titles without losing the requested sort.
/// <list type="bullet">
/// <item>Excluded tags run in the database through <see cref="InternalItemsQuery.ExcludeTags"/>, which matches the item's own tags.</item>
/// <item>The title term runs as <see cref="InternalItemsQuery.NameContains"/>. A <see cref="InternalItemsQuery.SearchTerm"/>
/// would make Jellyfin order by search relevance and ignore the sort.</item>
/// <item>Nothing can filter on part of a path, so the file name is matched in memory over what the database returns.</item>
/// </list>
/// </summary>
public class ItemSearch
{
    private static readonly char[] PipeSeparator = ['|'];
    private static readonly char[] CommaSeparator = [','];

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemSearch"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public ItemSearch(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Runs the search.
    /// </summary>
    /// <param name="request">The filters.</param>
    /// <returns>All matching items, or null if the parent folder does not exist.</returns>
    public ItemSearchResult? Find(ItemSearchQuery request)
    {
        var user = request.UserId is { } userId && userId != Guid.Empty ? _userManager.GetUserById(userId) : null;

        var folder = request.ParentId is { } parentId && parentId != Guid.Empty
            ? _libraryManager.GetItemById(parentId) as Folder
            : _libraryManager.GetUserRootFolder();
        if (folder is null)
        {
            return null;
        }

        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = ParseEnums<BaseItemKind>(request.IncludeItemTypes),
            // NameContains is compared against CleanName, so clean the term the same way.
            NameContains = string.IsNullOrWhiteSpace(request.SearchTerm) ? null : request.SearchTerm.Trim().GetCleanValue(),
            Genres = SplitList(request.Genres, PipeSeparator),
            Tags = SplitList(request.Tags, PipeSeparator),
            ExcludeTags = SplitList(request.ExcludeTags, PipeSeparator),
            Years = SplitList(request.Years, CommaSeparator)
                .Select(y => int.TryParse(y, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : (int?)null)
                .OfType<int>()
                .ToArray(),
            IsLocked = request.IsLocked,
            HasOverview = request.HasOverview,
            HasOfficialRating = request.HasOfficialRating,
            HasImdbId = request.HasImdbId,
            HasTmdbId = request.HasTmdbId,
            HasTvdbId = request.HasTvdbId
        };

        var order = Enum.TryParse<SortOrder>(request.SortOrder, true, out var parsedOrder) ? parsedOrder : SortOrder.Ascending;
        query.OrderBy = ParseEnums<ItemSortBy>(request.SortBy).Select(s => (s, order)).ToArray();

        IEnumerable<BaseItem> matches = folder.GetItems(query).Items;
        var term = request.FileName?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            matches = matches.Where(item => FileNameOf(item)?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
        }

        var items = matches
            .Select(item => new ItemSearchItem { Id = item.Id, Type = item.GetBaseItemKind().ToString() })
            .ToList();

        return new ItemSearchResult { TotalRecordCount = items.Count, Items = items };
    }

    private static string? FileNameOf(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        // Folder-based items (shows, seasons, albums) have a directory path; use its last segment.
        return Path.GetFileName(item.Path.TrimEnd('/', '\\'));
    }

    private static string[] SplitList(string? value, char[] separators)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static T[] ParseEnums<T>(string? value)
        where T : struct, Enum
        => SplitList(value, CommaSeparator)
            .Select(v => Enum.TryParse<T>(v, true, out var parsed) ? parsed : (T?)null)
            .OfType<T>()
            .ToArray();
}
