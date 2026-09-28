using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Exceptions;
using Core.Service.Handlers;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using Wallet.Api.Models;

namespace Wallet.Api.Controllers;

[ApiController]
[Route("api/wallets")] // Hardcoded lowercase route ensures stable mapping inside Linux containers
[ProducesResponseType(StatusCodes.Status429TooManyRequests)]
public class WalletController : ControllerBase
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string IdempotentReplayedHeader = "Idempotent-Replayed";

    private readonly IWalletHandler _handler;

    public WalletController(IWalletHandler handler)
    {
        _handler = handler;
    }

    // 📥 1. CREATE WALLET ENDPOINT
    // POST /api/wallets
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.WalletCreate)]
    [ProducesResponseType(StatusCodes.Status201Created, Type = typeof(WalletResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateWallet([FromBody] CreateWalletPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            var command = new CreateWalletCommand(payload.Currency, payload.InitialBalance);
            var wallet = await _handler.HandleCreateAsync(command, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, WalletResponse.From(wallet));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // 📤 2. RETRIEVE WALLET BALANCE (WITH CURRENCY CONVERSION HINT)
    // GET /api/wallets/{walletId}?currency=USD
    [HttpGet("{walletId:long}")]
    [EnableRateLimiting(RateLimitPolicies.WalletRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(BalanceDisplayResult))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBalance([FromRoute] long walletId, [FromQuery] string? currency, CancellationToken cancellationToken)
    {
        try
        {
            var query = new GetBalanceQuery(walletId, currency);
            var result = await _handler.HandleQueryAsync(query, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // 🛠️ 3. ADJUST WALLET BALANCE ENDPOINT
    // POST /api/wallets/{walletId}/adjustbalance?amount=50&currency=EUR&strategy=SubtractFundsStrategy
    // Header: Idempotency-Key: <client-generated unique value, e.g. a UUID>
    [HttpPost("{walletId:long}/adjustbalance")]
    [EnableRateLimiting(RateLimitPolicies.WalletAdjust)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(WalletResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AdjustBalance(
        [FromRoute] long walletId,
        [FromQuery][Required] decimal amount,
        [FromQuery][Required] string currency,
        [FromQuery][Required] string strategy,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var command = new AdjustBalanceCommand(walletId, amount, currency, strategy, idempotencyKey ?? string.Empty);
            var result = await _handler.HandleAdjustmentAsync(command, cancellationToken);

            if (result.IsReplay)
                Response.Headers[IdempotentReplayedHeader] = "true";

            return Ok(new WalletResponse(result.WalletId, result.Currency, result.Balance));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ConcurrencyConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (IdempotencyKeyReuseException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public record CreateWalletPayload([Required] string Currency, decimal InitialBalance = 0);
