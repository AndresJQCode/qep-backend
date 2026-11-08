using FluentValidation;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

public sealed class RegisterBuyerHandlerTests
{
    private const string Host = "acme.lulocrm.com";

    private static RegisterBuyerBody Body(
        string email = "ana@correo.com",
        string password = "una-contrasena-larga",
        bool accepts = true,
        string turnstile = "token") =>
        new(email, password, accepts, turnstile, null);

    private static RegisterBuyerHandler Handler(BuyersWorld world, FakeCaptcha captcha) =>
        new(new FakeStorefronts(), captcha, world.Hasher, world.Signup, new RegisterBuyerBodyValidator());

    private static RegisterBuyerCommand Command(RegisterBuyerBody body, string host = Host) =>
        new(host, "203.0.113.10", body);

    [Fact]
    public async Task RegisterHashesThePasswordOnEveryPath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Nuevo.
        var fresh = new BuyersWorld();
        await Handler(fresh, new FakeCaptcha()).HandleAsync(Command(Body()), cancellationToken);
        Assert.Equal(1, fresh.Hasher.HashCalls);

        // Email ya verificado.
        var verified = new BuyersWorld();
        verified.AddVerifiedAccount("ana@correo.com", "hash-original");
        await Handler(verified, new FakeCaptcha()).HandleAsync(Command(Body()), cancellationToken);
        Assert.Equal(1, verified.Hasher.HashCalls);

        // Email sin verificar.
        var unverified = new BuyersWorld();
        unverified.AddUnverifiedAccount("ana@correo.com", "hash-original");
        await Handler(unverified, new FakeCaptcha()).HandleAsync(Command(Body()), cancellationToken);
        Assert.Equal(1, unverified.Hasher.HashCalls);
    }

    [Fact]
    public async Task RegisterNormalizesTheEmailOntoAnExistingAccount()
    {
        var world = new BuyersWorld();
        world.AddVerifiedAccount("ana@correo.com", "hash-original");

        await Handler(world, new FakeCaptcha()).HandleAsync(
            Command(Body(email: "  ANA@Correo.com  ")), TestContext.Current.CancellationToken);

        Assert.Single(world.SavedAccounts);
        Assert.Equal(BuyerIntegrationEvents.ExistingAccountNotice, Assert.Single(world.SavedEvents).EventName);
    }

    [Fact]
    public async Task RegisterWithoutConsentFailsBeforeTheCaptchaAndWritesNothing()
    {
        var world = new BuyersWorld();
        var captcha = new FakeCaptcha();

        var exception = await Assert.ThrowsAsync<BuyerDomainException>(
            () => Handler(world, captcha).HandleAsync(Command(Body(accepts: false)), TestContext.Current.CancellationToken));

        Assert.Equal(BuyerErrorCodes.ConsentRequired, exception.Code);
        Assert.Equal(0, captcha.Calls);
        Assert.Equal(0, world.Hasher.HashCalls);
        Assert.Empty(world.SavedAccounts);
    }

    [Fact]
    public async Task AFailedCaptchaIsRejectedAndWritesNothing()
    {
        var world = new BuyersWorld();
        var captcha = new FakeCaptcha { Passes = false };

        var exception = await Assert.ThrowsAsync<BuyerDomainException>(
            () => Handler(world, captcha).HandleAsync(Command(Body()), TestContext.Current.CancellationToken));

        Assert.Equal(BuyerErrorCodes.CaptchaInvalid, exception.Code);
        Assert.Empty(world.SavedAccounts);
        Assert.Empty(world.SavedEvents);
    }

    [Fact]
    public async Task AnInvalidBodyFailsValidationBeforeTheCaptcha()
    {
        var captcha = new FakeCaptcha();

        await Assert.ThrowsAsync<ValidationException>(
            () => Handler(new BuyersWorld(), captcha).HandleAsync(
                Command(Body(password: "corta")), TestContext.Current.CancellationToken));

        Assert.Equal(0, captcha.Calls);
    }

    [Fact]
    public async Task AnUnknownHostFailsBeforeAnythingElse()
    {
        var captcha = new FakeCaptcha();

        await Assert.ThrowsAsync<BuildingBlocks.Application.ResourceNotFoundException>(
            () => Handler(new BuyersWorld(), captcha).HandleAsync(
                Command(Body(password: "corta"), "no-existe.lulocrm.com"), TestContext.Current.CancellationToken));

        Assert.Equal(0, captcha.Calls);
    }
}
