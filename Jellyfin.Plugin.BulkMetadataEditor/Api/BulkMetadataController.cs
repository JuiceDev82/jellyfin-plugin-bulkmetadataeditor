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

    /// <summary>
    /// Initializes a new instance of the <see cref="BulkMetadataController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{BulkMetadataController}"/> interface.</param>
    public BulkMetadataController(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<BulkMetadataController> logger)
    {
        _updater = new BulkMetadataUpdater(libraryManager, providerManager, fileSystem, logger);
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

        return await _updater.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
