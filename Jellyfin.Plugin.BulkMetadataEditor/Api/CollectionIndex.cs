using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

namespace Jellyfin.Plugin.BulkMetadataEditor.Api;

/// <summary>
/// Which collections (box sets) each item belongs to. Membership is read from the database's link
/// table rather than from loaded items, whose linked children are not always populated. Neither
/// <c>/Items</c> nor its DTOs expose membership, and finding an item's collections means walking the
/// collections either way, so the whole map is built in one pass and cached briefly.
/// </summary>
public sealed class CollectionIndex
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private static readonly object CacheLock = new();
    private static Snapshot? _cached;

    private readonly ILibraryManager _libraryManager;
    private readonly ILinkedChildrenService _linkedChildrenService;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionIndex"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="linkedChildrenService">Instance of the <see cref="ILinkedChildrenService"/> interface.</param>
    public CollectionIndex(ILibraryManager libraryManager, ILinkedChildrenService linkedChildrenService)
    {
        _libraryManager = libraryManager;
        _linkedChildrenService = linkedChildrenService;
    }

    /// <summary>
    /// Gets the names of the collections each of the given items belongs to.
    /// </summary>
    /// <param name="itemIds">The items.</param>
    /// <returns>Collection names keyed by item ID (32 hex digits); items in no collection are omitted.</returns>
    public Dictionary<string, string[]> GetCollectionNames(IEnumerable<Guid> itemIds)
    {
        var snapshot = GetSnapshot();
        var result = new Dictionary<string, string[]>();
        foreach (var id in itemIds.Distinct())
        {
            if (snapshot.CollectionsByItem.TryGetValue(id, out var names))
            {
                result[id.ToString("N")] = names;
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the items that belong to any collection whose name contains the term.
    /// </summary>
    /// <param name="nameTerm">Text the collection name must contain, ignoring case.</param>
    /// <returns>The member item IDs.</returns>
    public Guid[] GetItemIdsInCollections(string nameTerm)
    {
        var term = nameTerm.Trim();
        return GetSnapshot().Collections
            .Where(c => c.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.ItemIds)
            .Distinct()
            .ToArray();
    }

    private Snapshot GetSnapshot()
    {
        lock (CacheLock)
        {
            if (_cached is null || DateTime.UtcNow - _cached.BuiltUtc > Lifetime)
            {
                _cached = Build();
            }

            return _cached;
        }
    }

    private Snapshot Build()
    {
        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true
        });

        var collections = new List<(string Name, IReadOnlyList<Guid> ItemIds)>(boxSets.Count);
        var namesByItem = new Dictionary<Guid, List<string>>();
        foreach (var boxSet in boxSets.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            var itemIds = _linkedChildrenService.GetLinkedChildrenIds(boxSet.Id);
            collections.Add((boxSet.Name, itemIds));
            foreach (var itemId in itemIds)
            {
                if (!namesByItem.TryGetValue(itemId, out var names))
                {
                    names = [];
                    namesByItem[itemId] = names;
                }

                names.Add(boxSet.Name);
            }
        }

        return new Snapshot(
            DateTime.UtcNow,
            collections,
            namesByItem.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    private sealed record Snapshot(
        DateTime BuiltUtc,
        IReadOnlyList<(string Name, IReadOnlyList<Guid> ItemIds)> Collections,
        Dictionary<Guid, string[]> CollectionsByItem);
}

/// <summary>
/// Items whose collections to look up.
/// </summary>
public class CollectionsRequest
{
    public Guid[] ItemIds { get; set; } = [];
}

/// <summary>
/// Collection names keyed by item ID (32 hex digits); items in no collection are omitted.
/// </summary>
public class CollectionsResponse
{
    public Dictionary<string, string[]> Collections { get; set; } = [];
}
