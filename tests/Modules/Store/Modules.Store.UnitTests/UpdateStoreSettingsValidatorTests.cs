using FluentValidation;
using Modules.Store.Application;
using static Modules.Store.UnitTests.SettingsRig;
using static Modules.Store.UnitTests.StoreTestData;

namespace Modules.Store.UnitTests;

/// <summary>
/// R15: una prueba por familia de límites de HTTP contract A (largos de texto, tamaños de lista, forma de las cuentas). El slug y los
/// colores no son de este validador: los decide el dominio con su propio código <c>store.settings.*</c> y los cubre el manejador.
/// </summary>
public sealed class UpdateStoreSettingsValidatorTests
{
    private static readonly UpdateStoreSettingsValidator Validator = new();

    private static List<string> FailedProperties(StoreSettingsRequest body, long expectedVersion = 1) =>
        Validator.Validate(new UpdateStoreSettingsCommand(TenantId, expectedVersion, body))
            .Errors.Select(error => error.PropertyName).ToList();

    [Fact]
    public void AValidBodyPasses()
    {
        Assert.Empty(FailedProperties(Body()));
    }

    // Familia: largos de texto. El tope se mide recortado, y justo en el tope pasa.
    [Theory]
    [InlineData("name", 80)]
    [InlineData("bannerText", 200)]
    [InlineData("whatsApp", 32)]
    [InlineData("address", 200)]
    [InlineData("schedule", 200)]
    public void ATextOverItsLimitIsRejectedAndAtTheLimitPasses(string field, int limit)
    {
        StoreSettingsRequest With(string value) => field switch
        {
            "name" => Body(name: value),
            "bannerText" => Body() with { BannerText = value },
            "whatsApp" => Body() with { WhatsApp = value },
            "address" => Body() with { Address = value },
            _ => Body() with { Schedule = value },
        };

        Assert.Empty(FailedProperties(With(new string('a', limit))));
        Assert.Empty(FailedProperties(With(" " + new string('a', limit) + " ")));
        Assert.Equal([field], FailedProperties(With(new string('a', limit + 1))));
    }

    // Familia: tamaños de lista.
    [Fact]
    public void AListOverItsLimitIsRejectedAndAtTheLimitPasses()
    {
        var eleven = Enumerable.Range(0, 11).Select(index => (StoreCurrencyRequest?)Entry($"C{index}X", $"N{index}")).ToList();
        var ten = eleven.Take(10).ToList();
        var fiftyOne = Enumerable.Range(0, 51).Select(index => (string?)$"{(char)('A' + index / 26)}{(char)('A' + index % 26)}").ToList();
        var thirteen = Enumerable.Range(0, 13).Select(_ => Guid.NewGuid()).ToList();

        Assert.Empty(FailedProperties(Body(currencies: ten)));
        Assert.Equal(["currencies"], FailedProperties(Body(currencies: eleven)));

        Assert.Empty(FailedProperties(Body(salesCountries: fiftyOne.Take(50).ToList())));
        Assert.Equal(["salesCountries"], FailedProperties(Body(salesCountries: fiftyOne)));

        Assert.Empty(FailedProperties(Body(featured: thirteen.Take(12).ToList())));
        Assert.Equal(["featuredCategoryIds"], FailedProperties(Body(featured: thirteen)));

        var repeated = Guid.NewGuid();
        Assert.Equal(["featuredCategoryIds"], FailedProperties(Body(featured: [repeated, repeated])));
    }

    // Una lista ausente es un 422 con su campo, no un NullReferenceException ni un 400 del binding.
    [Fact]
    public void AMissingListOrBodyIsRejected()
    {
        var empty = Body() with { Currencies = null, SalesCountries = null, FeaturedCategoryIds = null };
        Assert.Equal(["currencies", "salesCountries", "featuredCategoryIds"], FailedProperties(empty));

        Assert.Equal(["body"], Validator.Validate(new UpdateStoreSettingsCommand(TenantId, 0, null!)).Errors.Select(e => e.PropertyName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Wholesale")]
    [InlineData("1")]
    public void APricingModeThatIsNotANamedModeIsRejected(string? mode)
    {
        Assert.Equal(["pricingMode"], FailedProperties(Body(pricingMode: mode)));
    }

    [Fact]
    public void AnAccountWithoutItsCompanyOrNumberIsRejectedAndANegativeVersionToo()
    {
        var noCompany = new StoreCurrencyRequest("COP", new StoreBillingAccountRequest(Guid.Empty, "Banco", "A-1", "COP"));
        var noNumber = new StoreCurrencyRequest("COP", new StoreBillingAccountRequest(CompanyId, "Banco", " ", "COP"));
        var noAccount = new StoreCurrencyRequest("COP", null);

        Assert.Equal(["currencies[0]"], FailedProperties(Body(currencies: [noCompany])));
        Assert.Equal(["currencies[0]"], FailedProperties(Body(currencies: [noNumber])));
        Assert.Equal(["currencies[0]"], FailedProperties(Body(currencies: [noAccount])));
        Assert.Contains("ExpectedVersion", FailedProperties(Body(), expectedVersion: -1));
        Assert.Empty(FailedProperties(Body(), expectedVersion: 0));
    }
}
