using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
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
    private readonly FileNameSearch _fileNameSearch;

    /// <summary>
    /// Initializes a new instance of the <see cref="BulkMetadataController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{BulkMetadataController}"/> interface.</param>
    public BulkMetadataController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<BulkMetadataController> logger)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Bulk Metadata Editor plugin is not initialized.");
        _updater = new BulkMetadataUpdater(libraryManager, providerManager, fileSystem, logger);
        _auditLog = new BulkMetadataAuditLog(plugin.AuditLogPath, logger);
        _fileNameSearch = new FileNameSearch(libraryManager, userManager);
    }

    /// <summary>
    /// Finds every item whose file or folder name contains a term, applying the same filters as the item list.
    /// </summary>
    /// <param name="query">The filters and the file name term.</param>
    /// <response code="200">Matching item IDs and types, in list order.</response>
    /// <response code="400">No file name term was given.</response>
    /// <response code="404">The parent folder was not found.</response>
    /// <returns>The matching items.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<FileNameSearchResult> Search([FromQuery] FileNameSearchQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.FileName))
        {
            return BadRequest("A file name term is required.");
        }

        var result = _fileNameSearch.Find(query);
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
