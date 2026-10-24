using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Collections.Api.Dto;
using Jellyfin.Plugin.Collections.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Collections.Api;

/// <summary>
/// Paste Collections API. Administrators only.
/// </summary>
[ApiController]
[Route("PasteCollections")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class BatchCollectionsController : ControllerBase
{
    private const long MaxRequestBytes = 2 * 1024 * 1024;

    private readonly BatchCollectionsService _service;
    private readonly SeerrService _seerr;
    private readonly ILogger<BatchCollectionsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchCollectionsController"/> class.
    /// </summary>
    /// <param name="service">The batch collections service.</param>
    /// <param name="seerr">The Seerr service.</param>
    /// <param name="logger">The logger.</param>
    public BatchCollectionsController(BatchCollectionsService service, SeerrService seerr, ILogger<BatchCollectionsController> logger)
    {
        _service = service;
        _seerr = seerr;
        _logger = logger;
    }

    /// <summary>
    /// Lists existing collections (for name suggestions).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The collections.</returns>
    [HttpGet("Collections")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CollectionSummaryDto>> GetCollections(CancellationToken cancellationToken)
    {
        return Run(() => Ok(_service.GetCollections(cancellationToken)), "list collections");
    }

    /// <summary>
    /// Matches a pasted list against the library. Changes nothing.
    /// </summary>
    /// <param name="request">The preview request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-line results.</returns>
    [HttpPost("Preview")]
    [Consumes(MediaTypeNames.Application.Json)]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PreviewResponse> Preview([FromBody] PreviewRequest request, CancellationToken cancellationToken)
    {
        return Run(() => Ok(_service.Preview(request, cancellationToken)), "preview");
    }

    /// <summary>
    /// Adds the confirmed items to the collection, creating it if needed.
    /// </summary>
    /// <param name="request">The apply request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was added.</returns>
    [HttpPost("Apply")]
    [Consumes(MediaTypeNames.Application.Json)]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApplyResponse>> Apply([FromBody] ApplyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.ApplyAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (BatchRequestException ex)
        {
            return BadRequest(new ErrorDto { Message = ex.Message });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Paste Collections: apply failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorDto { Message = "Jellyfin could not update the collection. Check the server log for details." });
        }
    }

    /// <summary>
    /// Lists existing collections that have no poster.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The collections.</returns>
    [HttpGet("Posters/Missing")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CollectionSummaryDto>> GetCollectionsWithoutPoster(CancellationToken cancellationToken)
    {
        return Run(() => Ok(_service.GetCollectionsWithoutPoster(cancellationToken)), "list collections without a poster");
    }

    /// <summary>
    /// Gives one existing collection a poster, if it has none.
    /// </summary>
    /// <param name="request">The collection and poster mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    [HttpPost("Posters/Add")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PosterResponse>> AddPoster([FromBody] PosterRequest request, CancellationToken cancellationToken)
    {
        return await RunAsync(async () => Ok(await _service.AddPosterToExistingAsync(request, cancellationToken).ConfigureAwait(false)), "add poster").ConfigureAwait(false);
    }

    /// <summary>
    /// Checks that Seerr answers and accepts the API key.
    /// </summary>
    /// <param name="request">The address and key to test; blank values use the saved settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The test result.</returns>
    [HttpPost("Seerr/Test")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SeerrTestResponse>> SeerrTest([FromBody] SeerrTestRequest request, CancellationToken cancellationToken)
    {
        return await RunAsync(async () => Ok(await _seerr.TestAsync(request, cancellationToken).ConfigureAwait(false)), "Seerr test").ConfigureAwait(false);
    }

    /// <summary>
    /// Requests titles that aren't in the library in Seerr.
    /// </summary>
    /// <param name="request">The titles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per title.</returns>
    [HttpPost("Seerr/Request")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<SeerrResultDto>>> SeerrRequest([FromBody] SeerrRequestBody request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await RunAsync(async () => Ok(await _seerr.RequestAsync(request.Items, cancellationToken).ConfigureAwait(false)), "Seerr request").ConfigureAwait(false);
    }

    private async Task<ActionResult> RunAsync(Func<Task<ActionResult>> action, string operation)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (BatchRequestException ex)
        {
            return BadRequest(new ErrorDto { Message = ex.Message });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Paste Collections: {Operation} failed", operation);
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorDto { Message = "Something went wrong on the server. Check the server log for details." });
        }
    }

    private ActionResult Run(Func<ActionResult> action, string operation)
    {
        try
        {
            return action();
        }
        catch (BatchRequestException ex)
        {
            return BadRequest(new ErrorDto { Message = ex.Message });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Paste Collections: {Operation} failed", operation);
            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorDto { Message = "Something went wrong on the server. Check the server log for details." });
        }
    }
}
