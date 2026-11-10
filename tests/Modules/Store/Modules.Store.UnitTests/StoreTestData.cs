using Modules.Store.Domain;

namespace Modules.Store.UnitTests;

internal static class StoreTestData
{
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static readonly Guid OtherMember = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly DateTimeOffset Now = new(2026, 11, 3, 15, 0, 0, TimeSpan.Zero);

    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public static readonly StoreCurrency Cop = Currency("COP");

    public static readonly StoreCurrency Eur = Currency("EUR");

    public static bool KnowsCoAndEs(string code) => code is "CO" or "ES";

    public static StoreCurrency Currency(string code, string? accountCurrency = null) =>
        new(code, new StoreBillingAccount(Guid.NewGuid(), "Bancolombia", "123-456", accountCurrency ?? code));

    public static StoreSettingsInput Input(
        string? slug = "acme",
        string? policy = "https://acme.test/politica",
        IReadOnlyList<StoreCurrency>? currencies = null,
        string? primaryCurrency = "COP",
        IReadOnlyList<string>? countries = null,
        Guid? advisorMembershipId = null) =>
        new(
            slug,
            Name: null,
            PrimaryColor: "#1F4FD8",
            OnPrimaryColor: null,
            AccentTextColor: null,
            BannerFileId: null,
            BannerText: null,
            Contact: new StoreContact(null, null, null, null, null, null),
            DataPolicyUrl: policy,
            PricingMode: StorePricingMode.RetailOnly,
            Currencies: currencies ?? [Cop],
            PrimaryCurrency: primaryCurrency,
            SalesCountries: countries ?? ["CO"],
            FeaturedCategoryIds: [],
            AdvisorMembershipId: advisorMembershipId);

    public static OnlineStore ConfiguredStore(StoreSettingsInput? input = null)
    {
        var store = OnlineStore.CreateEmpty(TenantId, Now);
        store.Configure(input ?? Input(), KnowsCoAndEs, Member, Now);
        return store;
    }

    public static StorePlace Colombia() =>
        new("CO", Guid.NewGuid(), "Antioquia", Guid.NewGuid(), "Medellín");

    public static StoreRequestBilling Billing() =>
        new("Ana Pérez", "CC", "123456", "300 123 4567", "+573001234567", "ana@test.co", "Calle 1 # 2-3", Colombia());

    public static StoreRequestShipping Shipping(string? recipient = null) =>
        new(recipient, "300 765 4321", "+573007654321", "Carrera 9 # 8-7", Colombia());

    public static IReadOnlyList<StoreOrderRequestLineInput> Lines(decimal discountPercentage = 0m) =>
    [
        new(Guid.NewGuid(), Guid.NewGuid(), "Camiseta - M", 2m, 10000m, discountPercentage, "Own", 10000m, 20000m),
    ];

    public static StoreConsent Consent() => new(Now, "203.0.113.9", "https://acme.test/politica", Now.AddDays(-5));

    public static StoreOrderRequest Request(
        long number = 7,
        StoreRequestShipping? shipping = null,
        decimal total = 20000m,
        decimal discountPercentage = 0m) =>
        StoreOrderRequest.Create(
            Guid.NewGuid(),
            TenantId,
            number,
            Guid.NewGuid(),
            Billing(),
            shipping,
            note: null,
            currency: "COP",
            mode: StorePricingMode.RetailOnly,
            lines: Lines(discountPercentage),
            subtotal: total,
            discountAmount: 0m,
            taxAmount: 0m,
            total: total,
            consent: Consent(),
            receivedAt: Now);
}
