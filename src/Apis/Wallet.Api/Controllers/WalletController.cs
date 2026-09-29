using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Handlers;
using Core.Service.Strategies;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;

using Wallet.Api.Filters;
using Wallet.Api.Models;

namespace Wallet.Api.Controllers;

[ApiController]
[Route("api/wallets")] // Hardcoded lowercase route ensures stable mapping inside Linux containers
[TypeFilter<ApiExceptionFilter>] // Maps domain exceptions to status codes, so actions only handle the success path
[ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ErrorResponse))] // Written by the rate limiter, same body shape
[ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ErrorResponse))]
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
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ErrorResponse))]
    public async Task<IActionResult> CreateWallet([FromBody] CreateWalletPayload payload, CancellationToken cancellationToken)
    {
        var command = new CreateWalletCommand(payload.Currency, payload.InitialBalance);
        var wallet = await _handler.HandleCreateAsync(command, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, WalletResponse.From(wallet));
    }

    // 📤 2. RETRIEVE WALLET BALANCE (WITH CURRENCY CONVERSION HINT)
    // GET /api/wallets/{walletId}?currency=USD
    [HttpGet("{walletId:long}")]
    [EnableRateLimiting(RateLimitPolicies.WalletRead)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(BalanceDisplayResult))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ErrorResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ErrorResponse))]
    public async Task<IActionResult> GetBalance([FromRoute] long walletId, [FromQuery] string? currency, CancellationToken cancellationToken)
    {
        var query = new GetBalanceQuery(walletId, currency);
        var result = await _handler.HandleQueryAsync(query, cancellationToken);
        return Ok(result);
    }

    // 🛠️ 3. ADJUST WALLET BALANCE ENDPOINT
    // POST /api/wallets/{walletId}/adjustbalance?amount=50&currency=EUR&strategy=SubtractFundsStrategy (a BalanceStrategyType name)
    // Header: Idempotency-Key: <client-generated unique value, e.g. a UUID>
    [HttpPost("{walletId:long}/adjustbalance")]
    [EnableRateLimiting(RateLimitPolicies.WalletAdjust)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(WalletResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ErrorResponse))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ErrorResponse))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ErrorResponse))]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity, Type = typeof(ErrorResponse))]
    public async Task<IActionResult> AdjustBalance(
        [FromRoute] long walletId,
        [FromQuery][Required] decimal amount,
        [FromQuery][Required] string currency,
        [FromQuery][BindRequired] BalanceStrategyType strategy, // BindRequired rejects a missing value, which [Required] can't detect on a value type
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var command = new AdjustBalanceCommand(walletId, amount, currency, strategy, idempotencyKey ?? string.Empty);
        var result = await _handler.HandleAdjustmentAsync(command, cancellationToken);

        if (result.IsReplay)
            Response.Headers[IdempotentReplayedHeader] = "true";

        return Ok(new WalletResponse(result.WalletId, result.Currency, result.Balance));
    }
}

public record CreateWalletPayload([Required] string Currency, decimal InitialBalance = 0);
