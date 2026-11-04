using Modules.Store.Domain;
using static Modules.Store.UnitTests.StoreTestData;

namespace Modules.Store.UnitTests;

public sealed class OnlineStoreTests
{
    private static string CodeOf(Action act) => Assert.Throws<StoreDomainException>(act).Code;

    private static OnlineStore Empty() => OnlineStore.CreateEmpty(TenantId, Now);

    [Fact]
    public void AnEmptyStoreReportsEveryBlockerInOrder()
    {
        var store = Empty();

        Assert.Equal(0, store.Version);
        Assert.Equal(
            [
                StorePublishBlocker.SlugRequired,
                StorePublishBlocker.DataPolicyRequired,
                StorePublishBlocker.CurrencyRequired,
                StorePublishBlocker.SalesCountryRequired,
            ],
            store.PublishBlockers());
    }

    [Fact]
    public void AConfiguredStoreIsPublishableAndPublishing()
    {
        var store = ConfiguredStore();

        Assert.Empty(store.PublishBlockers());
        store.Publish(Member, Now);

        Assert.True(store.IsPublished);
        Assert.Equal(Now, store.PublishedAt);
    }

    [Fact]
    public void PublishingWithBlockersThrowsTheFirstBlockersCode()
    {
        var store = Empty();
        Assert.Equal(StoreErrorCodes.SlugRequired, CodeOf(() => store.Publish(Member, Now)));
    }

    [Fact]
    public void UnpublishingTakesTheStoreDown()
    {
        var store = ConfiguredStore();
        store.Publish(Member, Now);

        store.Unpublish(Member, Now.AddMinutes(1));

        Assert.False(store.IsPublished);
        Assert.Null(store.PublishedAt);
    }

    [Fact]
    public void ConfiguringBumpsTheVersion()
    {
        var store = ConfiguredStore();
        Assert.Equal(1, store.Version);

        store.Configure(Input(), KnowsCoAndEs, Member, Now);
        Assert.Equal(2, store.Version);
    }

    [Fact]
    public void ASlugIsNormalizedOnConfigure()
    {
        Assert.Equal("acme", ConfiguredStore(Input(slug: " ACME ")).Slug);
        Assert.Equal(StoreErrorCodes.SlugReserved, CodeOf(() => ConfiguredStore(Input(slug: "Tienda"))));
    }

    [Fact]
    public void AMalformedColorIsRejectedButLowContrastNever()
    {
        Assert.Equal(StoreErrorCodes.ColorInvalid, CodeOf(() => ConfiguredStore(Input() with { PrimaryColor = null })));
        Assert.Equal(StoreErrorCodes.ColorInvalid, CodeOf(() => ConfiguredStore(Input() with { PrimaryColor = "blue" })));
        Assert.Equal(StoreErrorCodes.ColorInvalid, CodeOf(() => ConfiguredStore(Input() with { OnPrimaryColor = "#12" })));
        Assert.Equal(StoreErrorCodes.ColorInvalid, CodeOf(() => ConfiguredStore(Input() with { AccentTextColor = "x" })));

        var store = ConfiguredStore(Input() with { PrimaryColor = "#ffd400", OnPrimaryColor = "#ffffff" });

        Assert.Equal("#FFD400", store.PrimaryColor);
        Assert.Equal("#FFFFFF", store.OnPrimaryColor);
        Assert.Null(store.AccentTextColor);
        Assert.True(store.ResolveColors()!.Checks.OnPrimaryLowContrast);
    }

    [Fact]
    public void ANeverSavedStoreHasNoResolvedColors() => Assert.Null(Empty().ResolveColors());

    [Fact]
    public void ChangingThePolicyUrlMovesItsDateAndKeepingItDoesNot()
    {
        var store = ConfiguredStore(Input(policy: "https://acme.test/a"));
        var firstSetAt = store.DataPolicySetAt;
        Assert.Equal(Now, firstSetAt);

        store.Configure(Input(policy: "https://acme.test/a"), KnowsCoAndEs, Member, Now.AddDays(1));
        Assert.Equal(firstSetAt, store.DataPolicySetAt);

        store.Configure(Input(policy: "https://acme.test/b"), KnowsCoAndEs, Member, Now.AddDays(2));
        Assert.Equal(Now.AddDays(2), store.DataPolicySetAt);
    }

    [Fact]
    public void APublishedStoreCannotBeSavedWithoutItsPolicy()
    {
        var store = ConfiguredStore();
        store.Publish(Member, Now);

        Assert.Equal(
            StoreErrorCodes.DataPolicyRequired,
            CodeOf(() => store.Configure(Input(policy: null), KnowsCoAndEs, Member, Now)));
        Assert.Equal("https://acme.test/politica", store.DataPolicyUrl);
    }

    [Fact]
    public void APublishedStoreCannotDropItsLastCurrency()
    {
        var store = ConfiguredStore();
        store.Publish(Member, Now);

        Assert.Equal(
            StoreErrorCodes.CurrencyRequired,
            CodeOf(() => store.Configure(Input(currencies: [], primaryCurrency: null), KnowsCoAndEs, Member, Now)));
        Assert.Single(store.Currencies);
    }

    [Fact]
    public void APublishedStoreCannotDropItsLastSalesCountry()
    {
        var store = ConfiguredStore();
        store.Publish(Member, Now);

        Assert.Equal(
            StoreErrorCodes.SalesCountryRequired,
            CodeOf(() => store.Configure(Input(countries: []), KnowsCoAndEs, Member, Now)));
        Assert.Equal(["CO"], store.SalesCountries);
    }

    [Fact]
    public void AnUnknownSalesCountryIsRejected() =>
        Assert.Equal(StoreErrorCodes.SalesCountryInvalid, CodeOf(() => ConfiguredStore(Input(countries: ["XX"]))));

    [Fact]
    public void AMalformedSalesCountryIsRejected() =>
        Assert.Equal(StoreErrorCodes.SalesCountryInvalid, CodeOf(() => ConfiguredStore(Input(countries: ["COL"]))));

    [Fact]
    public void AForeignSalesCountryIsAccepted()
    {
        var store = ConfiguredStore(Input(countries: ["CO", "ES"]));

        Assert.True(store.SellsTo("es"));
        Assert.False(store.SellsTo("US"));
    }

    [Fact]
    public void ARepeatedSalesCountryIsRejected() =>
        Assert.Equal(StoreErrorCodes.SalesCountryDuplicate, CodeOf(() => ConfiguredStore(Input(countries: ["CO", "co"]))));

    [Fact]
    public void SalesCountriesAreStoredUppercaseAndMatchedIgnoringCase()
    {
        var store = ConfiguredStore(Input(countries: ["co"]));

        Assert.Equal(["CO"], store.SalesCountries);
        Assert.True(store.SellsTo("Co"));
    }

    [Fact]
    public void SalesCountriesKeepTheSavedOrder() =>
        Assert.Equal(["ES", "CO"], ConfiguredStore(Input(countries: ["es", "CO"])).SalesCountries);

    [Fact]
    public void AnEntryWhoseAccountIsInAnotherCurrencyIsRejected() =>
        Assert.Equal(
            StoreErrorCodes.CurrencyMismatch,
            CodeOf(() => ConfiguredStore(Input(currencies: [Currency("EUR", accountCurrency: "COP")], primaryCurrency: "EUR"))));

    [Fact]
    public void ARepeatedCurrencyIsRejected() =>
        Assert.Equal(
            StoreErrorCodes.CurrencyDuplicate,
            CodeOf(() => ConfiguredStore(Input(currencies: [Cop, Currency("cop")]))));

    [Theory]
    [InlineData("USD")]
    [InlineData(null)]
    public void APrimaryOutsideTheListIsRejected(string? primary) =>
        Assert.Equal(
            StoreErrorCodes.PrimaryCurrencyInvalid,
            CodeOf(() => OnlineStore.ValidateCurrencies([Cop, Eur], primary)));

    [Fact]
    public void APrimaryWithoutEntriesIsRejected() =>
        Assert.Equal(StoreErrorCodes.PrimaryCurrencyInvalid, CodeOf(() => OnlineStore.ValidateCurrencies([], "COP")));

    [Fact]
    public void AnEmptyListWithoutPrimaryIsValid() => OnlineStore.ValidateCurrencies([], null);

    [Fact]
    public void MoreThanTenCurrenciesAreRejected()
    {
        var eleven = Enumerable.Range(0, OnlineStore.MaxCurrencies + 1)
            .Select(i => Currency($"A{(char)('A' + i)}{(char)('A' + i)}"))
            .ToList();
        var ten = eleven.Take(OnlineStore.MaxCurrencies).ToList();

        Assert.Equal(StoreErrorCodes.ValidationFailed, CodeOf(() => OnlineStore.ValidateCurrencies(eleven, eleven[0].Currency)));
        OnlineStore.ValidateCurrencies(ten, ten[0].Currency);
    }

    [Fact]
    public void EntryCurrenciesAreTrimmedLikeThePrimary()
    {
        var store = ConfiguredStore(Input(currencies: [Currency(" cop ", accountCurrency: "COP")], primaryCurrency: " cop "));

        Assert.Equal("COP", Assert.Single(store.Currencies).Currency);
        Assert.Equal("COP", store.PrimaryCurrency);
        Assert.True(store.Sells("COP"));
        Assert.Equal(StoreErrorCodes.CurrencyDuplicate, CodeOf(() => OnlineStore.ValidateCurrencies([Cop, Currency(" cop ", "COP")], "COP")));
    }

    [Fact]
    public void AMalformedCurrencyIsRejected() =>
        Assert.Equal(
            StoreErrorCodes.CurrencyInvalid,
            CodeOf(() => ConfiguredStore(Input(currencies: [Currency("CO")], primaryCurrency: "CO"))));

    [Fact]
    public void CurrencyChecksAreOrderedFormatThenDuplicateThenMismatchThenPrimary()
    {
        // Un formato malo gana sobre un duplicado, y un duplicado sobre un descuadre de cuenta.
        Assert.Equal(
            StoreErrorCodes.CurrencyInvalid,
            CodeOf(() => OnlineStore.ValidateCurrencies([Cop, Cop, Currency("E1R")], "COP")));
        Assert.Equal(
            StoreErrorCodes.CurrencyDuplicate,
            CodeOf(() => OnlineStore.ValidateCurrencies([Cop, Cop, Currency("EUR", "COP")], "COP")));
        Assert.Equal(
            StoreErrorCodes.CurrencyMismatch,
            CodeOf(() => OnlineStore.ValidateCurrencies([Currency("EUR", "COP")], "USD")));
    }

    [Fact]
    public void EachCurrencyResolvesItsOwnAccount()
    {
        var store = ConfiguredStore(Input(currencies: [Cop, Eur], primaryCurrency: "COP"));

        Assert.Equal(Eur.BillingAccount, store.BillingAccountFor("eur"));
        Assert.Equal(Cop.BillingAccount, store.BillingAccountFor("COP"));
        Assert.Null(store.BillingAccountFor("USD"));
    }

    [Fact]
    public void ASingleCurrencyStoreIsAListOfOne()
    {
        var store = ConfiguredStore();

        Assert.DoesNotContain(StorePublishBlocker.CurrencyRequired, store.PublishBlockers());
        Assert.True(store.Sells("COP"));
        Assert.True(store.Sells("cop"));
        Assert.False(store.Sells("EUR"));
        Assert.Equal("COP", store.PrimaryCurrency);
    }

    [Fact]
    public void CurrenciesForVisitorsKeepTheSavedOrderWithoutMovingThePrimary()
    {
        var store = ConfiguredStore(Input(currencies: [Eur, Cop], primaryCurrency: "COP"));

        Assert.Equal(["EUR", "COP"], store.CurrenciesForVisitors());
        Assert.Equal(["EUR", "COP"], store.Currencies.Select(c => c.Currency));
    }

    [Fact]
    public void CurrenciesAreStoredUppercase()
    {
        var store = ConfiguredStore(Input(currencies: [Currency("cop")], primaryCurrency: "cop"));

        Assert.Equal("COP", store.Currencies[0].Currency);
        Assert.Equal("COP", store.Currencies[0].BillingAccount.Currency);
        Assert.Equal("COP", store.PrimaryCurrency);
    }

    [Fact]
    public void ARejectedConfigureLeavesTheStoreUntouched()
    {
        var store = ConfiguredStore();

        Assert.Throws<StoreDomainException>(() =>
            store.Configure(Input(slug: "nuevo", countries: ["XX"]), KnowsCoAndEs, Member, Now.AddDays(1)));

        Assert.Equal("acme", store.Slug);
        Assert.Equal(1, store.Version);
    }
}
