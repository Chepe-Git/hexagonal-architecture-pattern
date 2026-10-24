using Microsoft.AspNetCore.Mvc;
using MakeTransfer.Core.Application.Ports.Incoming;
using MakeTransfer.Core.Application.Ports.Outgoing;
using MakeTransfer.Core.Application.DataSets;
using MakeTransfer.Core.Domain.Transfers;
using MakeTransfer.Api.Models.Requests;
using MakeTransfer.Api.Models.Responses;

namespace MakeTransfer.Api.Controllers;

/// <summary>
/// Handles money transfer requests from clients.
/// This is where HTTP requests get converted to domain operations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class TransfersController : ControllerBase
{
    private readonly IBankingOperationsInputPort _bankingOperations;
    private readonly IDatabaseOutputPort _databasePort;
    private readonly ILogger<TransfersController> _logger;

    public TransfersController(
        IBankingOperationsInputPort bankingOperations,
        IDatabaseOutputPort databasePort,
        ILogger<TransfersController> logger)
    {
        _bankingOperations = bankingOperations ?? throw new ArgumentNullException(nameof(bankingOperations));
        _databasePort = databasePort ?? throw new ArgumentNullException(nameof(databasePort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Retrieves a transfer by its identifier.
    /// </summary>
    /// <param name="transferId">The unique transfer identifier</param>
    /// <returns>HTTP response with transfer details</returns>
    [HttpGet("{transferId}")]
    [ProducesResponseType(typeof(ApiResponse<TransferResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public IActionResult GetTransfer(string transferId)
    {
        _logger.LogInformation("Retrieving transfer {TransferId}", transferId);

        var transfer = _databasePort.GetTransferById(transferId);
        if (transfer is null)
        {
            // US-0004 AC-5: failureReason is not included in error responses.
            return NotFound(ApiResponse<object>.ErrorResponse(
                404,
                $"Transfer {transferId} was not found"));
        }

        var failureReason = transfer.Status == TransferStatus.Failed
            ? transfer.FailureReason // US-0004 AC-1/AC-4: return the registered failure reason exactly as stored.
            : string.Empty; // US-0004 AC-2: non-failed transfers return an empty failure reason.

        var transferResponse = new TransferResponse
        {
            TransferId = transfer.TransferId,
            Status = transfer.Status.ToString(),
            Message = "Transfer retrieved successfully.",
            ProcessedAt = transfer.CreatedAtUtc,
            FailureReason = failureReason
        };

        // US-0004 AC-3: successful transfer lookup returns ApiResponse with failureReason inside data.
        return Ok(ApiResponse<TransferResponse>.SuccessResponse(
            transferResponse,
            "Transfer retrieved successfully."));
    }

    /// <summary>
    /// Execute a money transfer between two accounts
    /// </summary>
    /// <param name="request">Transfer details from the client</param>
    /// <returns>HTTP response with transfer status</returns>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<TransferResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    [ProducesResponseType(typeof(ApiResponse<object>), 500)]
    [ProducesResponseType(typeof(ApiResponse<object>), 502)]
    public IActionResult ExecuteTransfer([FromBody] MakeTransferRequest request)
    {
        _logger.LogInformation(
            "Processing transfer request from {FromAccount} to {ToAccount} for {Amount} {Currency}",
            request.FromAccountId,
            request.ToAccountId,
            request.Amount,
            request.Currency);

        if (!ModelState.IsValid)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse(
                400,
                "Invalid request data",
                ModelState));
        }

        try
        {
            // Convert HTTP request to domain data
            var transferData = new TransferData(
                request.FromAccountId,
                request.ToAccountId,
                request.Amount,
                request.Currency,
                request.Reference,
                "WEB_API",     // Who initiated this
                "WEB_API"      // Which channel
            );

            // Let the domain handle the business logic
            var result = _bankingOperations.ExecuteTransfer(transferData);

            // Convert domain result back to HTTP response
            var transferResponse = new TransferResponse
            {
                TransferId = result.Data?.TransferId ?? "N/A",
                Status = result.Data?.Status.ToString() ?? "Unknown",
                Message = result.Message,
                ProcessedAt = result.Data?.ProcessedAt ?? DateTime.UtcNow,
                ExternalReference = result.Data?.ExternalReference
            };

            var apiResponse = result.Success
                ? ApiResponse<TransferResponse>.SuccessResponse(
                    transferResponse,
                    result.Message,
                    result.CorrelationId)
                : ApiResponse<TransferResponse>.ErrorResponse(
                    result.StatusCode,
                    result.Message,
                    transferResponse,
                    result.CorrelationId);

            _logger.LogInformation(
                "Transfer request completed with status {StatusCode}: {Message} [CorrelationId: {CorrelationId}]",
                result.StatusCode,
                result.Message,
                result.CorrelationId);

            return StatusCode(result.StatusCode, apiResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error processing transfer request for {FromAccount} to {ToAccount}",
                request.FromAccountId,
                request.ToAccountId);

            return StatusCode(500, ApiResponse<object>.ErrorResponse(
                500,
                "An unexpected error occurred while processing the transfer"));
        }
    }
}