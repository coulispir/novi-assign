using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Handlers;
using Core.Service.Repositories;

using FluentAssertions;

using NSubstitute;

namespace Unit.Tests.Application;

public sealed class CreateWalletHandlerTests
{
    private readonly IWalletRepository _walletRepository = Substitute.For<IWalletRepository>();
    private readonly CreateWalletHandler _handler;

    public CreateWalletHandlerTests()
    {
        _handler = new CreateWalletHandler(_walletRepository);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_SavesTheWalletAndReturnsIt()
    {
        var result = await _handler.HandleAsync(new CreateWalletCommand("usd", 25m), CancellationToken.None);

        result.Currency.Should().Be("USD");
        result.Balance.Should().Be(25m);
        _walletRepository.Received(1).Add(Arg.Is<AccountWallet>(wallet => wallet.Currency == "USD" && wallet.Balance == 25m));
        await _walletRepository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithInvalidWallet_ThrowsValidationErrorWithoutSaving()
    {
        var handle = () => _handler.HandleAsync(new CreateWalletCommand("EUR", -1m), CancellationToken.None).AsTask();

        await handle.Should().ThrowAsync<DomainValidationException>();
        await _walletRepository.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }
}
