using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkMetadataEditor.Api;

/// <summary>
/// Bulk metadata editing endpoints. Administrator only.
/// </summary>
[ApiController]
[Route("BulkMetadataEditor")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class BulkMetadataController : ControllerBase
{
    private readonly BulkMetadataUpdater _updater;
    private readonly BulkMetadataAuditLog _auditLog;
    private readonly ItemSearch _itemSearch;
    private readonly CollectionIndex _collectionIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="BulkMetadataController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="linkedChildrenService">Instance of the <see cref="ILinkedChildrenService"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{BulkMetadataController}"/> interface.</param>
    public BulkMetadataController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IProviderManager providerManager,
        ILinkedChildrenService linkedChildrenService,
        IFileSystem fileSystem,
        ILogger<BulkMetadataController> logger)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Bulk Metadata Editor plugin is not initialized.");
        _updater = new BulkMetadataUpdater(libraryManager, providerManager, fileSystem, logger);
        _auditLog = new BulkMetadataAuditLog(plugin.AuditLogPath, logger);
        _collectionIndex = new CollectionIndex(libraryManager, linkedChildrenService);
        _itemSearch = new ItemSearch(libraryManager, userManager, _collectionIndex);
    }

    /// <summary>
    /// Gets the names of the collections each of the given items belongs to.
    /// </summary>
    /// <param name="request">The items to look up.</param>
    /// <response code="200">Collection names keyed by item ID; items in no collection are omitted.</response>
    /// <returns>The collection names.</returns>
    [HttpPost("Collections")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CollectionsResponse> GetCollections([FromBody] CollectionsRequest request)
    {
        // POST rather than GET: a page can hold 500 item IDs, too many for a URL.
        return new CollectionsResponse { Collections = _collectionIndex.GetCollectionNames(request.ItemIds) };
    }

    /// <summary>
    /// Finds every item matching the item list's filters, for the cases <c>/Items</c> cannot serve:
    /// a file name or description term, excluded tags, a collection name, or a title search that must
    /// keep the requested sort.
    /// </summary>
    /// <param name="query">The filters.</param>
    /// <response code="200">Matching item IDs and types, in list order.</response>
    /// <response code="400">None of a title, file name, description or collection term, or excluded tags, were given.</response>
    /// <response code="404">The parent folder was not found.</response>
    /// <returns>The matching items.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ItemSearchResult> Search([FromQuery] ItemSearchQuery query)
    {
        // Without these, /Items answers the same query paged in the database, which is cheaper.
        if (string.IsNullOrWhiteSpace(query.SearchTerm)
            && string.IsNullOrWhiteSpace(query.FileName)
            && string.IsNullOrWhiteSpace(query.OverviewContains)
            && string.IsNullOrWhiteSpace(query.InCollection)
            && string.IsNullOrWhiteSpace(query.ExcludeTags))
        {
            return BadRequest("A title, file name, description or collection term, or excluded tags, are required.");
        }

        var result = _itemSearch.Find(query);
        return result is null ? NotFound() : result;
    }

    /// <summary>
    /// Applies metadata changes to a set of items.
    /// </summary>
    /// <param name="request">The items and the changes to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Changes applied; see the per-item results.</response>
    /// <returns>The per-item results.</returns>
    [HttpPost("Update")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BulkUpdateResponse>> Update([FromBody] BulkUpdateRequest request, CancellationToken cancellationToken)
    {
        if (request.ItemIds.Length == 0)
        {
            return BadRequest("No items were specified.");
        }

        var response = await _updater.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
        _auditLog.Record(request, response, User.Identity?.Name);
        return response;
    }

    /// <summary>
    /// Gets the audit log of bulk edits, newest first.
    /// </summary>
    /// <response code="200">Audit log returned.</response>
    /// <returns>The audit log.</returns>
    [HttpGet("AuditLog")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AuditLogResponse> GetAuditLog()
    {
        return new AuditLogResponse { Entries = _auditLog.ReadEntries() };
    }

    /// <summary>
    /// Clears the audit log.
    /// </summary>
    /// <response code="204">Audit log cleared.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("AuditLog/Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearAuditLog()
    {
        _auditLog.Clear();
        return NoContent();
    }
}
