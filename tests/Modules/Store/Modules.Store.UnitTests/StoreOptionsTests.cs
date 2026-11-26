using Modules.Store.Application;

namespace Modules.Store.UnitTests;

public sealed class StoreOptionsTests
{
    private static StoreOptions Valid() => new()
    {
        Storefront = new StorefrontOptions { SharedSecret = new string('s', 32) },
        Turnstile = new TurnstileOptions { Enabled = true, SecretKey = "turnstile-secret" },
    };

    [Fact]
    public void TheDefaultsMatchTheSpec()
    {
        var options = new StoreOptions();

        Assert.Equal("Store", StoreOptions.SectionName);
        Assert.Equal("lulocrm.com", options.RootDomain);
        Assert.Equal(500, options.DailyRequestCap);
        Assert.True(options.Turnstile.Enabled);
        Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/siteverify", options.Turnstile.VerifyUrl);
    }

    [Fact]
    public void AFullyConfiguredSectionIsValid() => Assert.Empty(Valid().Validate());

    [Fact]
    public void AShortStorefrontSecretFailsValidation()
    {
        var options = new StoreOptions
        {
            Storefront = new StorefrontOptions { SharedSecret = new string('s', 31) },
            Turnstile = new TurnstileOptions { Enabled = true, SecretKey = "turnstile-secret" },
        };

        var failure = Assert.Single(options.Validate());

        Assert.Contains("Store:Storefront:SharedSecret", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingStorefrontSecretFailsWhenSecretsAreRequired()
    {
        var options = Valid();
        options.Storefront = new StorefrontOptions();

        Assert.Contains("Store:Storefront:SharedSecret", Assert.Single(options.Validate()), StringComparison.Ordinal);
    }

    [Fact]
    public void TurnstileEnabledWithoutASecretFailsValidation()
    {
        var options = Valid();
        options.Turnstile = new TurnstileOptions { Enabled = true, SecretKey = null };

        var failure = Assert.Single(options.Validate());

        Assert.Contains("Store:Turnstile:SecretKey", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnstileDisabledDoesNotNeedASecretInDevelopmentAndTesting()
    {
        var options = Valid();
        options.Turnstile = new TurnstileOptions { Enabled = false };

        Assert.Empty(options.Validate(requireSecrets: false, turnstileMayBeDisabled: true));
    }

    [Fact]
    public void TurnstileDisabledFailsStartupOutsideDevelopmentAndTesting()
    {
        var options = Valid();
        options.Turnstile = new TurnstileOptions { Enabled = false };

        var failure = Assert.Single(options.Validate());

        Assert.Contains("Store:Turnstile:Enabled", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutRequiringSecretsOnlyAPresentButShortSecretFails()
    {
        var absent = new StoreOptions();
        Assert.Empty(absent.Validate(requireSecrets: false));

        // Sin requerir secretos tampoco se permite apagar el anti-bot: son dos condiciones distintas.
        var disabled = new StoreOptions { Turnstile = new TurnstileOptions { Enabled = false } };
        Assert.Contains(
            "Store:Turnstile:Enabled", Assert.Single(disabled.Validate(requireSecrets: false)), StringComparison.Ordinal);

        var short31 = new StoreOptions { Storefront = new StorefrontOptions { SharedSecret = new string('s', 31) } };
        Assert.Contains(
            "Store:Storefront:SharedSecret", Assert.Single(short31.Validate(requireSecrets: false)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void TheDailyCapMustBePositive(int cap)
    {
        var options = Valid();
        options.DailyRequestCap = cap;

        Assert.Contains("Store:DailyRequestCap", Assert.Single(options.Validate()), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRootDomainMustNotBeBlank()
    {
        var options = Valid();
        options.RootDomain = " ";

        Assert.Contains("Store:RootDomain", Assert.Single(options.Validate()), StringComparison.Ordinal);
    }
}
