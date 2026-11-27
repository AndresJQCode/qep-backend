using Modules.Customers.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-18 §5.5 y ruling R2: Siigo declara sus propios enums de documento y de persona y nunca referencia tipos de Quotations;
/// el adaptador de Bootstrapper los traduce por nombre. Este proyecto ve los dos módulos y exige que los juegos de nombres coincidan:
/// un valor nuevo de QEP sin su par en Siigo se perdería en silencio (la traducción lo dejaría en <c>null</c> y el armador lo reportaría
/// como un tipo sin código).
/// </summary>
public sealed class SiigoOrderSourceTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly Guid CityId = Guid.CreateVersion7();

    [Fact]
    public void ThePersonKindsMatchByName() =>
        Assert.Equal(Enum.GetNames<PartyPersonType>().Order(), Enum.GetNames<SiigoPersonKind>().Order());

    [Fact]
    public void TheIdentificationKindsMatchByName() =>
        Assert.Equal(Enum.GetNames<PartyIdentificationType>().Order(), Enum.GetNames<SiigoIdentificationKind>().Order());

    [Fact]
    public void EveryQepIdentificationTypeHasASiigoCode()
    {
        var supported = Enum.GetValues<SiigoIdentificationKind>().Where(kind => SiigoIdentificationTypes.CodeFor(kind) is not null);

        string[] expected =
        [
            nameof(PartyIdentificationType.Nit),
            nameof(PartyIdentificationType.Cc),
            nameof(PartyIdentificationType.Ce),
            nameof(PartyIdentificationType.Pasaporte),
            nameof(PartyIdentificationType.Nif),
            nameof(PartyIdentificationType.Nie),
            nameof(PartyIdentificationType.TaxId),
        ];
        Assert.Equal(expected.Order(), supported.Select(kind => kind.ToString()).Order());
    }

    [Fact]
    public async Task TheAdapterTranslatesTheOrderTheEnumsTheCityAndTheBankKey()
    {
        var source = new SiigoOrderSource(new FakeInvoicing(Order()), new FakeGeography(), new FakeSplitter(null));

        var input = await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken);

        Assert.NotNull(input);
        Assert.Equal("BANCOLOMBIA|000123456", input.BillingAccountKey);
        Assert.Equal(SiigoIdentificationKind.Nit, input.BilledParty.IdentificationType);
        Assert.Equal(SiigoPersonKind.Juridica, input.BilledParty.PersonType);
        Assert.Equal(("11001", "11"), (input.BilledParty.CityCode, input.BilledParty.StateCode));
        Assert.Single(input.Lines);
        Assert.Equal(7, input.OrderVersion);
        Assert.Equal(input.CompanyId, Order().CompanyId);
    }

    [Fact]
    public async Task AGiftLineTravelsWithItsFlagAndListPrice()
    {
        var gift = new ExternalInvoicingLine(Guid.CreateVersion7(), "Obsequio", 1m, 0m, 100m, 19, 0m, IsGift: true, ListUnitPrice: 1_000m);
        var order = Order() with { Lines = [.. Order().Lines, gift] };
        var source = new SiigoOrderSource(new FakeInvoicing(order), new FakeGeography(), new FakeSplitter(null));

        var input = await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken);

        var line = Assert.Single(input!.Lines, candidate => candidate.IsGift);
        Assert.Equal(1_000m, line.ListUnitPrice);
        Assert.False(input.Lines[0].IsGift);
    }

    [Fact]
    public async Task TheAdvisorTravels()
    {
        var source = new SiigoOrderSource(new FakeInvoicing(Order()), new FakeGeography(), new FakeSplitter(null));

        var input = await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken);

        Assert.Equal((AdvisorId, "Andrea Ritual"), (input!.AdvisorMembershipId, input.AdvisorLabel));
    }

    [Fact]
    public async Task ACityOutsideColombiaGetsNoDaneCode()
    {
        var order = Order() with { BilledParty = Order().BilledParty with { Country = "ES", CityName = null } };
        var source = new SiigoOrderSource(new FakeInvoicing(order), new FakeGeography(), new FakeSplitter(null));

        var input = await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken);

        Assert.Null(input!.BilledParty.CityCode);
        Assert.Null(input.BilledParty.StateCode);
    }

    private static ExternalInvoicingParty Foreign(string? country, string? cityName) =>
        Order().BilledParty with { Country = country, CityName = cityName, CityId = null, DepartmentId = null };

    [Fact]
    public async Task AForeignCityInTheListTravelsWithTheSiigoCodes()
    {
        var geography = new FakeGeography();
        geography.Foreign[("US", "Miami")] = new SiigoForeignCityLookup(true, new SiigoForeignCityCodes("Us", "09", "1"));
        var source = new SiigoOrderSource(
            new FakeInvoicing(Order() with { BilledParty = Foreign("US", " Miami ") }), geography, new FakeSplitter(null));

        var input = await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken);

        var party = input!.BilledParty;
        Assert.Equal(("1", "09", "Us"), (party.CityCode, party.StateCode, party.SiigoCountryCode));
        Assert.Null(party.UnlinkedCity);
    }

    [Fact]
    public async Task ALowercaseCountryIsAskedInUppercase()
    {
        var geography = new FakeGeography();
        var source = new SiigoOrderSource(
            new FakeInvoicing(Order() with { BilledParty = Foreign(" us ", " Miami ") }), geography, new FakeSplitter(null));

        await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken);

        Assert.Equal(("US", "Miami"), Assert.Single(geography.Asked));
    }

    [Fact]
    public async Task AForeignCityOutOfTheListIsUnlinkedAndReportable()
    {
        var geography = new FakeGeography();
        geography.Foreign[("US", "Miamy")] = new SiigoForeignCityLookup(true, null);
        var source = new SiigoOrderSource(
            new FakeInvoicing(Order() with { BilledParty = Foreign("US", " Miamy ") }), geography, new FakeSplitter(null));

        var input = await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken);

        var party = input!.BilledParty;
        Assert.Equal(new SiigoUnlinkedCity("US", "Miamy", Reportable: true), party.UnlinkedCity);
        Assert.Equal((null, null, null), (party.CityCode, party.StateCode, party.SiigoCountryCode));
    }

    [Fact]
    public async Task ACountryOutOfTheCatalogIsUnlinkedButNotReportable()
    {
        var geography = new FakeGeography();
        geography.Foreign[("GD", "St. George's")] = new SiigoForeignCityLookup(false, null);
        var source = new SiigoOrderSource(
            new FakeInvoicing(Order() with { BilledParty = Foreign("GD", "St. George's") }), geography, new FakeSplitter(null));

        var input = await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken);

        Assert.Equal(new SiigoUnlinkedCity("GD", "St. George's", Reportable: false), input!.BilledParty.UnlinkedCity);
    }

    [Fact]
    public async Task AForeignPartyWithoutCityAsksNothing()
    {
        var geography = new FakeGeography();
        var source = new SiigoOrderSource(
            new FakeInvoicing(Order() with { BilledParty = Foreign("US", null) }), geography, new FakeSplitter(null));

        var input = await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken);

        var party = input!.BilledParty;
        Assert.Empty(geography.Asked);
        Assert.Null(party.UnlinkedCity);
        Assert.Equal((null, null, null), (party.CityCode, party.StateCode, party.SiigoCountryCode));
    }

    [Fact]
    public async Task AColombianPartyNeverAsksForForeignCodesNorCarriesASiigoCountry()
    {
        var geography = new FakeGeography();
        var source = new SiigoOrderSource(new FakeInvoicing(Order()), geography, new FakeSplitter(null));

        var input = await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken);

        Assert.Empty(geography.Asked);
        Assert.Null(input!.BilledParty.SiigoCountryCode);
        Assert.Null(input.BilledParty.UnlinkedCity);
    }

    [Fact]
    public async Task ASplitPhoneTravelsAsIndicativeAndNumber()
    {
        var splitter = new FakeSplitter(new PhoneNumberParts("CO", "3001234567", "57"));

        var input = await GetWithPartyAsync(Party("CO", "+573001234567"), splitter);

        Assert.Equal(new SiigoPhone("57", "3001234567"), input.BilledParty.Phone);
    }

    [Theory]
    [InlineData(null, "CO")]
    [InlineData("", "CO")]
    [InlineData("co", "CO")]
    [InlineData("CO", "CO")]
    [InlineData("VE", "VE")]
    [InlineData(" ve ", "VE")]
    public async Task TheRegionIsColombiaForAnEmptyCountryAndOtherwiseThePartyCountry(string? country, string expectedRegion)
    {
        var splitter = new FakeSplitter(null);

        await GetWithPartyAsync(Party(country, "4121234567"), splitter);

        Assert.Equal(("4121234567", expectedRegion), Assert.Single(splitter.Calls));
    }

    [Fact]
    public async Task AnUnsplitPhoneTravelsRawForTheMapperToDecide()
    {
        var input = await GetWithPartyAsync(Party("CO", "12345"), new FakeSplitter(null));

        Assert.Equal(new SiigoPhone(null, "12345"), input.BilledParty.Phone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task NoPhoneIsNullAndTheSplitterIsNotCalled(string? phone)
    {
        var splitter = new FakeSplitter(new PhoneNumberParts("CO", "3001234567", "57"));

        var input = await GetWithPartyAsync(Party("CO", phone), splitter);

        Assert.Null(input.BilledParty.Phone);
        Assert.Empty(splitter.Calls);
    }

    private static ExternalInvoicingParty Party(string? country, string? phone) =>
        Order().BilledParty with { Country = country, Phone = phone };

    private static async Task<SiigoInvoiceInput> GetWithPartyAsync(ExternalInvoicingParty party, FakeSplitter splitter)
    {
        var order = Order() with { BilledParty = party };
        var source = new SiigoOrderSource(new FakeInvoicing(order), new FakeGeography(), splitter);
        return (await source.GetAsync(TenantId, OrderId, TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task TheMarkTravelsWithTheSiigoProvider()
    {
        var invoicing = new FakeInvoicing(Order());
        var source = new SiigoOrderSource(invoicing, new FakeGeography(), new FakeSplitter(null));
        var memberId = Guid.CreateVersion7();

        await source.MarkInvoicedAsync(TenantId, OrderId, "FV-1-123", "a1b2c3", memberId, TestContext.Current.CancellationToken);

        Assert.Equal(("siigo", "FV-1-123", "a1b2c3", memberId), invoicing.Marked);
    }

    [Fact]
    public async Task TheMarkIsAskedWithTheSiigoProvider()
    {
        var invoicing = new FakeInvoicing(Order()) { Marks = true };
        var source = new SiigoOrderSource(invoicing, new FakeGeography(), new FakeSplitter(null));

        var marked = await source.IsMarkedInvoicedAsync(TenantId, OrderId, "FV-1-123", TestContext.Current.CancellationToken);

        Assert.True(marked);
        Assert.Equal(("siigo", "FV-1-123"), invoicing.MarkAsked);
    }

    [Fact]
    public async Task UnmarkPassesTheSiigoProvider()
    {
        var invoicing = new FakeInvoicing(Order()) { Releases = true };
        var source = new SiigoOrderSource(invoicing, new FakeGeography(), new FakeSplitter(null));

        var released = await source.UnmarkInvoicedAsync(
            TenantId, OrderId, "FV-1-1", Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        Assert.True(released);
        Assert.Equal(("siigo", "FV-1-1"), invoicing.RevertAsked);
    }

    [Fact]
    public async Task TheLabelsTravelByOrderId()
    {
        var other = Guid.CreateVersion7();
        var invoicing = new FakeInvoicing(Order());
        invoicing.Labels[OrderId] = new ExternalInvoicingOrderLabel("PED-1", "Verde SAS");
        invoicing.Labels[other] = new ExternalInvoicingOrderLabel("PED-2", null);
        var source = new SiigoOrderSource(invoicing, new FakeGeography(), new FakeSplitter(null));

        var labels = await source.ListLabelsAsync(TenantId, [OrderId, other], TestContext.Current.CancellationToken);

        Assert.Equal(2, labels.Count);
        Assert.Equal(new SiigoOrderLabel("PED-1", "Verde SAS"), labels[OrderId]);
        Assert.Equal(new SiigoOrderLabel("PED-2", null), labels[other]);
        Assert.Equal((TenantId, 2), invoicing.LabelsAsked);
    }

    private static ExternalInvoicingOrder Order() =>
        new(
            OrderId,
            "PED-1",
            "COP",
            new DateOnly(2026, 11, 18),
            false,
            [new ExternalInvoicingLine(Guid.CreateVersion7(), "Aceite", 2m, 23_800m, 0m, 19, 4_522m)],
            0m,
            0m,
            0,
            28_322m,
            28_322m,
            0m,
            28_322m,
            [],
            [],
            Guid.Parse("0193a1b2-000b-7000-8000-00000000000b"),
            " Bancolombia ",
            "000-123 456",
            null,
            new ExternalInvoicingParty(
                false, PartyPersonType.Juridica, PartyIdentificationType.Nit, "900123456-1", "Verde SAS", "Calle 1", CityId, Guid.CreateVersion7(), "CO", "a@b.co", "300"),
            7,
            AdvisorId,
            "Andrea Ritual");

    private static readonly Guid AdvisorId = Guid.Parse("0193a1b2-000c-7000-8000-00000000000c");

    private sealed class FakeInvoicing(ExternalInvoicingOrder order) : IExternalOrderInvoicing
    {
        public (string Provider, string Number, string ExternalId, Guid MemberId)? Marked { get; private set; }

        public Task<ExternalInvoicingOrder?> GetForInvoicingAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<ExternalInvoicingOrder?>(order);

        public Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(order.Version);

        public bool Marks { get; init; }

        public (string Provider, string Number)? MarkAsked { get; private set; }

        public Task<bool> IsMarkedAsync(Guid tenantId, Guid orderId, string provider, string number, CancellationToken cancellationToken)
        {
            MarkAsked = (provider, number);
            return Task.FromResult(Marks);
        }

        public bool Releases { get; init; }

        public (string Provider, string Number)? RevertAsked { get; private set; }

        public Task<bool> RevertExternalInvoicingAsync(
            Guid tenantId, Guid orderId, string provider, string number, Guid memberId, CancellationToken cancellationToken)
        {
            RevertAsked = (provider, number);
            return Task.FromResult(Releases);
        }

        public Dictionary<Guid, ExternalInvoicingOrderLabel> Labels { get; } = [];

        public (Guid TenantId, int Orders)? LabelsAsked { get; private set; }

        public Task<IReadOnlyDictionary<Guid, ExternalInvoicingOrderLabel>> ListLabelsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
        {
            LabelsAsked = (tenantId, orderIds.Count);
            return Task.FromResult<IReadOnlyDictionary<Guid, ExternalInvoicingOrderLabel>>(Labels);
        }

        public Task MarkInvoicedAsync(
            Guid tenantId, Guid orderId, string provider, string number, string externalId, Guid memberId, CancellationToken cancellationToken)
        {
            Marked = (provider, number, externalId, memberId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSplitter(PhoneNumberParts? parts) : IPhoneNumberSplitter
    {
        public List<(string? Phone, string? Region)> Calls { get; } = [];

        public PhoneNumberParts? Split(string? phone, string? defaultRegion = null)
        {
            Calls.Add((phone, defaultRegion));
            return parts;
        }
    }

    private sealed class FakeGeography : ISiigoGeographyLookup
    {
        public Task<SiigoCityCodes?> FindCodesAsync(Guid cityId, CancellationToken cancellationToken) =>
            Task.FromResult<SiigoCityCodes?>(new SiigoCityCodes("11001", "11"));

        public Dictionary<(string Country, string City), SiigoForeignCityLookup> Foreign { get; } = [];

        public List<(string Country, string City)> Asked { get; } = [];

        // Lo que no se configuró es un país del catálogo con una ciudad que no coincide.
        public Task<SiigoForeignCityLookup> FindForeignCodesAsync(string countryCode, string cityText, CancellationToken cancellationToken)
        {
            Asked.Add((countryCode, cityText));
            return Task.FromResult(Foreign.TryGetValue((countryCode, cityText), out var lookup) ? lookup : new SiigoForeignCityLookup(true, null));
        }
    }
}
