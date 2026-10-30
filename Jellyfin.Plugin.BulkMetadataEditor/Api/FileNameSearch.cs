using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.BulkMetadataEditor.Api;

/// <summary>
/// The filters of the item list, as sent to the <c>/Items</c> API, plus a file name term.
/// </summary>
public class FileNameSearchQuery
{
    public string FileName { get; set; } = string.Empty;

    public Guid? UserId { get; set; }

    public string? IncludeItemTypes { get; set; }

    public Guid? ParentId { get; set; }

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
/// An item matched by a file name search.
/// </summary>
public class FileNameSearchItem
{
    public Guid Id { get; set; }

    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Every item matched by a file name search, in list order.
/// </summary>
public class FileNameSearchResult
{
    public int TotalRecordCount { get; set; }

    public List<FileNameSearchItem> Items { get; set; } = [];
}

/// <summary>
/// Finds items whose file (or folder) name contains a term. Neither <c>/Items</c> nor
/// <see cref="InternalItemsQuery"/> can filter on part of a path, so the other filters run in the
/// database and the file name is matched in memory over what they return.
/// </summary>
public class FileNameSearch
{
    private static readonly char[] PipeSeparator = ['|'];
    private static readonly char[] CommaSeparator = [','];

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileNameSearch"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public FileNameSearch(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Runs the search.
    /// </summary>
    /// <param name="request">The filters and file name term.</param>
    /// <returns>All matching items, or null if the parent folder does not exist.</returns>
    public FileNameSearchResult? Find(FileNameSearchQuery request)
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
            SearchTerm = string.IsNullOrWhiteSpace(request.SearchTerm) ? null : request.SearchTerm.Trim(),
            Genres = SplitList(request.Genres, PipeSeparator),
            Tags = SplitList(request.Tags, PipeSeparator),
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

        var term = request.FileName.Trim();
        var items = folder.GetItems(query).Items
            .Where(item => FileNameOf(item)?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => new FileNameSearchItem { Id = item.Id, Type = item.GetBaseItemKind().ToString() })
            .ToList();

        return new FileNameSearchResult { TotalRecordCount = items.Count, Items = items };
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
