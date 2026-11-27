using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Cotizaciones de proyecto por HTTP (spec 2026-11-26 §6 y §7), contra Postgres real. Cada prueba prende
/// <c>projects</c> en su propio tenant, así que el host compartido sigue sirviendo.
/// </summary>
public sealed class ProjectQuotationsApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private static readonly Guid FirstPhase = Guid.CreateVersion7();
    private static readonly Guid SecondPhase = Guid.CreateVersion7();
    private static readonly Guid PhaseItem = Guid.CreateVersion7();
    private static readonly Guid LooseItem = Guid.CreateVersion7();
    private static readonly string?[] PriceThenScopeHistory =
        ["Precio del proyecto actualizado.", "Alcance actualizado."];
    private static readonly string[] RoundingCurrencies = ["COP", "USD", "EUR"];

    [Fact]
    public async Task CreatingAProjectBuildsOneServiceLineAtTheTotal()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;

        var response = await PostAsync(setup, Create(setup, PerPhase(setup.ServiceId, 600_000m, 400_000m)));

        var created = await ReadAsync(response, HttpStatusCode.Created);
        var fetched = await GetAsync(setup, created.Id);
        foreach (var document in new[] { created, fetched })
        {
            Assert.Equal("Project", document.Kind);
            Assert.NotNull(document.Project);
            Assert.Equal("PerPhase", document.Project.PricingMode);
            Assert.Equal(setup.ServiceId, document.Project.ServiceVariantId);
            Assert.Equal(1_000_000m, document.Project.Total);
            Assert.Equal("COP", document.Project.PricesCurrency);
            Assert.Equal([FirstPhase, SecondPhase], document.Project.Phases.Select(phase => phase.Id));
            Assert.Equal([600_000m, 400_000m], document.Project.Phases.Select(phase => phase.Price!.Value));
            Assert.Equal(PhaseItem, Assert.Single(document.Project.Phases[0].Items).Id);
            var line = Assert.Single(document.Items);
            Assert.Equal((setup.ServiceId, 1m, 1_000_000m, 0m), (line.VariantId, line.Quantity, line.UnitPrice, line.DiscountPercentage));
            Assert.Equal(1_000_000m, document.Total);
            Assert.False(document.RequiresShipping);
        }
    }

    // §12-G: sin bloque, el proyecto nace con un alcance vacío.
    [Fact]
    public async Task AProjectWithoutAScopeIsBornEmpty()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;

        var created = await ReadAsync(
            await PostAsync(setup, new CreateQuotationRequest(setup.ClientId, null, null, null, null, null, Kind: "Project")),
            HttpStatusCode.Created);

        Assert.Equal("Project", created.Kind);
        Assert.NotNull(created.Project);
        Assert.Equal(("Global", null, null), (created.Project.PricingMode, created.Project.ServiceVariantId, created.Project.Total));
        Assert.Empty(created.Project.Phases);
        Assert.Empty(created.Project.Items);
        Assert.Empty(created.Items);
        Assert.False(created.RequiresShipping);
    }

    [Fact]
    public async Task APreviewAcceptsDashTextsAndSavesNothing()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var project = new ProjectScopeRequest(
            setup.ServiceId, "Global", null,
            [new ProjectPhaseRequest(FirstPhase, "-", null, [new ProjectItemRequest(PhaseItem, "-")])],
            [new ProjectItemRequest(LooseItem, "-")],
            null);
        var before = await ReadAsync(await PostAsync(setup, Create(setup, project)), HttpStatusCode.Created);

        var preview = await ReadAsync(
            await setup.Client.PostAsJsonAsync(
                $"{QuotationsUrl(setup.TenantId)}/preview", Create(setup, project), TestContext.Current.CancellationToken),
            HttpStatusCode.OK);

        Assert.Equal(Guid.Empty, preview.Id);
        Assert.Equal("-", Assert.Single(preview.Project!.Items).Text);
        Assert.Null(preview.Project.Total);
        Assert.Equal(0m, Assert.Single(preview.Items).UnitPrice);

        var after = await ReadAsync(await PostAsync(setup, Create(setup, project)), HttpStatusCode.Created);
        Assert.Equal(Sequence(before) + 1, Sequence(after));
    }

    [Fact]
    public async Task ManualLinesOnAProjectAreRejected()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var request = Create(setup, Global(setup.ServiceId, 100_000m)) with { Items = [new(setup.ServiceId, 1m)] };

        await AssertProblemAsync(
            await PostAsync(setup, request), HttpStatusCode.UnprocessableEntity, "quotation.project.items_not_allowed");
        await AssertProblemAsync(
            await setup.Client.PostAsJsonAsync(
                $"{QuotationsUrl(setup.TenantId)}/preview", request, TestContext.Current.CancellationToken),
            HttpStatusCode.UnprocessableEntity,
            "quotation.project.items_not_allowed");

        var created = await CreateProjectAsync(setup, Global(setup.ServiceId, 100_000m));
        var edit = Save(Global(setup.ServiceId, 200_000m)) with { Items = [new(setup.ServiceId, 1m)] };

        await AssertProblemAsync(
            await PutAsync(setup, created, edit),
            HttpStatusCode.UnprocessableEntity,
            "quotation.project.items_not_allowed");
        Assert.Equal(created.Version, (await GetAsync(setup, created.Id)).Version);
    }

    [Fact]
    public async Task AnInactiveOrPhysicalServiceIsInvalidOnlyWhenItChanges()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var physical = await CreateProductWithScalesAsync(setup.Client, setup.TenantId);
        foreach (var invalid in new[] { physical, Guid.CreateVersion7() })
        {
            await AssertProblemAsync(
                await PostAsync(setup, Create(setup, Global(invalid, 100_000m))),
                HttpStatusCode.UnprocessableEntity,
                "quotation.project.service_invalid");
        }

        var created = await CreateProjectAsync(setup, Global(setup.ServiceId, 100_000m));
        var deactivated = await setup.Client.PostAsync(
            $"/api/v1/tenants/{setup.TenantId}/catalog/products/{setup.ServiceId}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.True(deactivated.IsSuccessStatusCode, $"{deactivated.StatusCode}");

        // El servicio guardado no cambia: la línea se queda como está y sólo cambia de precio.
        var saved = await ReadAsync(
            await PutAsync(setup, created, Save(Global(setup.ServiceId, 250_000m, "Otro texto"))), HttpStatusCode.OK);

        Assert.Equal("Otro texto", Assert.Single(saved.Project!.Items).Text);
        var line = Assert.Single(saved.Items);
        Assert.Equal((Assert.Single(created.Items).Id, 250_000m), (line.Id, line.UnitPrice));
        await AssertProblemAsync(
            await PostAsync(setup, Create(setup, Global(setup.ServiceId, 100_000m))),
            HttpStatusCode.UnprocessableEntity,
            "quotation.project.service_invalid");
        await AssertProblemAsync(
            await PutAsync(setup, saved, Save(Global(physical, 250_000m))),
            HttpStatusCode.UnprocessableEntity,
            "quotation.project.service_invalid");
    }

    // Cambiar de servicio quita la línea del anterior y agrega la del nuevo; quitarlo deja el proyecto sin línea. Las dos
    // cosas quedan guardadas.
    [Fact]
    public async Task ChangingAndRemovingTheServiceIsPersisted()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var other = await CreateServiceAsync(setup.Client, setup.TenantId);
        var created = await CreateProjectAsync(setup, Global(setup.ServiceId, 100_000m));
        var first = Assert.Single(created.Items);

        var changed = await ReadAsync(
            await PutAsync(setup, created, Save(Global(other, 150_000m))), HttpStatusCode.OK);
        var reloaded = await GetAsync(setup, created.Id);

        foreach (var document in new[] { changed, reloaded })
        {
            Assert.Equal(other, document.Project!.ServiceVariantId);
            var line = Assert.Single(document.Items);
            Assert.NotEqual(first.Id, line.Id);
            Assert.Equal((other, 1m, 150_000m), (line.VariantId, line.Quantity, line.UnitPrice));
            Assert.Equal(150_000m, document.Total);
        }

        var withoutService = new ProjectScopeRequest(
            null, "Global", 150_000m, null, [new ProjectItemRequest(LooseItem, "Soporte")], "COP");
        var removed = await ReadAsync(await PutAsync(setup, reloaded, Save(withoutService)), HttpStatusCode.OK);
        var after = await GetAsync(setup, created.Id);

        foreach (var document in new[] { removed, after })
        {
            Assert.Null(document.Project!.ServiceVariantId);
            Assert.Empty(document.Items);
            Assert.Equal((150_000m, 0m), (document.Project.Total!.Value, document.Total));
            Assert.False(document.CanBeConvertedToOrder);
        }
    }

    // Spec §9: un servicio que se desactivó después no bloquea enviar ni convertir.
    [Fact]
    public async Task ADeactivatedServiceDoesNotBlockSendingOrConverting()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var account = await CreateAccountAsync(setup, "COP");
        var created = await ReadAsync(
            await PostAsync(setup, Create(setup, Global(setup.ServiceId, 100_000m)) with { BillingAccount = account }),
            HttpStatusCode.Created);
        var deactivated = await setup.Client.PostAsync(
            $"/api/v1/tenants/{setup.TenantId}/catalog/products/{setup.ServiceId}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.True(deactivated.IsSuccessStatusCode, $"{deactivated.StatusCode}");
        var pdfFileId = await CreateAvailablePdfFileAsync(setup.Client, setup.Factory, setup.TenantId);

        var sent = await ReadAsync(
            await setup.Client.PostAsJsonAsync(
                $"{QuotationUrl(setup, created.Id)}/send",
                new SendQuotationRequest(pdfFileId),
                TestContext.Current.CancellationToken),
            HttpStatusCode.OK);
        var converted = await CallAsync(setup, sent, "convert");

        Assert.Equal("Sent", sent.Status);
        Assert.True(converted.IsSuccessStatusCode, await converted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var order = await converted.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        Assert.Equal(("Project", 100_000m), (order.Kind, order.Total));
        Assert.Equal(setup.ServiceId, order.Project!.ServiceVariantId);
    }

    // §12-A: una empresa sin cuenta no devuelve el documento a la moneda del tenant.
    [Fact]
    public async Task ChoosingACompanyWithoutBankAccountKeepsTheDocumentCurrency()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var usd = await CreateAccountAsync(setup, "USD");
        var created = await ReadAsync(
            await PostAsync(setup, Create(setup, Global(setup.ServiceId, 100m, currency: "USD")) with { BillingAccount = usd }),
            HttpStatusCode.Created);
        var company = await CreateAccountAsync(setup, "COP");
        var withoutAccount = new QuotationBillingAccountRequest(company.CompanyId, null, null, null);
        var request = Save(Global(setup.ServiceId, 120.25m, currency: "USD"), withoutAccount);

        var preview = await ReadAsync(
            await setup.Client.PostAsJsonAsync(
                $"{QuotationUrl(setup, created.Id)}/preview", request, TestContext.Current.CancellationToken),
            HttpStatusCode.OK);
        var saved = await ReadAsync(await PutAsync(setup, created, request), HttpStatusCode.OK);

        foreach (var document in new[] { preview, saved, await GetAsync(setup, created.Id) })
        {
            Assert.Equal(company.CompanyId, document.BillingAccount!.CompanyId);
            Assert.Null(document.BillingAccount.BankName);
            Assert.Equal(
                ("USD", "USD", 120.25m, 120.25m),
                (document.Currency, document.Project!.PricesCurrency, document.Project.Total!.Value, document.Total));
        }
    }

    [Fact]
    public async Task SavingWithAnAccountInAnotherCurrencyAppliesPricesTypedInIt()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var usd = await CreateAccountAsync(setup, "USD");
        var typedInUsd = await CreateProjectAsync(setup, PerPhase(setup.ServiceId, 600_000m, 400_000m));
        var typedInCop = await CreateProjectAsync(setup, PerPhase(setup.ServiceId, 600_000m, 400_000m));

        var applied = await ReadAsync(
            await PutAsync(setup, typedInUsd, Save(PerPhase(setup.ServiceId, 150.5m, 100m, "USD"), usd)),
            HttpStatusCode.OK);
        var preview = await ReadAsync(
            await setup.Client.PostAsJsonAsync(
                $"{QuotationUrl(setup, typedInCop.Id)}/preview",
                Save(PerPhase(setup.ServiceId, 600_000m, 400_000m, "COP"), usd),
                TestContext.Current.CancellationToken),
            HttpStatusCode.OK);
        var discarded = await ReadAsync(
            await PutAsync(setup, typedInCop, Save(PerPhase(setup.ServiceId, 600_000m, 400_000m, "COP"), usd)),
            HttpStatusCode.OK);

        Assert.Equal(("USD", "USD", 250.5m), (applied.Currency, applied.Project!.PricesCurrency, applied.Project.Total!.Value));
        Assert.Equal((250.5m, 250.5m), (Assert.Single(applied.Items).UnitPrice, applied.Total));
        foreach (var document in new[] { preview, discarded })
        {
            Assert.Equal(("USD", "USD"), (document.Currency, document.Project!.PricesCurrency));
            Assert.Null(document.Project.Total);
            Assert.All(document.Project.Phases, phase => Assert.Null(phase.Price));
            Assert.Equal((0m, 0m), (Assert.Single(document.Items).UnitPrice, document.Total));
        }
    }

    // En un alta no hay precios guardados: los de otra moneda se descartan.
    [Fact]
    public async Task CreatingWithPricesInAnotherCurrencyLeavesThemEmpty()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;

        var created = await CreateProjectAsync(setup, Global(setup.ServiceId, 100m, currency: "USD"));

        Assert.Equal(("COP", "COP"), (created.Currency, created.Project!.PricesCurrency));
        Assert.Null(created.Project.GlobalPrice);
        Assert.Null(created.Project.Total);
        Assert.Equal(0m, Assert.Single(created.Items).UnitPrice);
    }

    // §12-H: sin cambio de moneda, los precios de otra moneda dejan los guardados, por id de fase.
    [Fact]
    public async Task PricesInAnotherCurrencyKeepTheSavedOnes()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var created = await CreateProjectAsync(setup, PerPhase(setup.ServiceId, 600_000m, 400_000m));
        var newPhase = Guid.CreateVersion7();
        var stale = new ProjectScopeRequest(
            setup.ServiceId, "PerPhase", null,
            [
                new ProjectPhaseRequest(SecondPhase, "Entrega", 1m, null),
                new ProjectPhaseRequest(FirstPhase, "Descubrimiento", 2m, null),
            ],
            null, "USD");

        var saved = await ReadAsync(await PutAsync(setup, created, Save(stale)), HttpStatusCode.OK);

        Assert.Equal([400_000m, 600_000m], saved.Project!.Phases.Select(phase => phase.Price!.Value));
        Assert.Equal((1_000_000m, "COP"), (saved.Project.Total!.Value, saved.Project.PricesCurrency));

        var withNewPhase = stale with
        {
            Phases = [.. stale.Phases!, new ProjectPhaseRequest(newPhase, "Soporte", 3m, null)]
        };
        var incomplete = await ReadAsync(await PutAsync(setup, saved, Save(withNewPhase)), HttpStatusCode.OK);

        Assert.Equal([400_000m, 600_000m, null], incomplete.Project!.Phases.Select(phase => phase.Price));
        Assert.Null(incomplete.Project.Total);
        Assert.Equal(0m, Assert.Single(incomplete.Items).UnitPrice);
    }

    // §12-A: sin cuenta, la moneda al editar es la del documento y no la del tenant.
    [Fact]
    public async Task RemovingTheAccountKeepsTheDocumentCurrency()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var usd = await CreateAccountAsync(setup, "USD");
        var created = await ReadAsync(
            await PostAsync(setup, Create(setup, Global(setup.ServiceId, 100m, currency: "USD")) with { BillingAccount = usd }),
            HttpStatusCode.Created);
        Assert.Equal(("USD", 100m), (created.Currency, created.Total));

        var saved = await ReadAsync(
            await PutAsync(setup, created, Save(Global(setup.ServiceId, 120.25m, currency: "USD"))), HttpStatusCode.OK);

        Assert.Null(saved.BillingAccount);
        Assert.Equal(("USD", "USD", 120.25m, 120.25m), (saved.Currency, saved.Project!.PricesCurrency, saved.Project.Total!.Value, saved.Total));
    }

    // Ausente conserva el alcance guardado, y un cambio de moneda sin bloque borra los precios.
    [Fact]
    public async Task SavingWithoutAProjectKeepsTheScope()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var usd = await CreateAccountAsync(setup, "USD");
        var created = await CreateProjectAsync(setup, Global(setup.ServiceId, 100_000m));

        var kept = await ReadAsync(
            await PutAsync(setup, created, Save(null) with { Notes = "Sólo la nota" }), HttpStatusCode.OK);
        var repriced = await ReadAsync(await PutAsync(setup, kept, Save(null, usd)), HttpStatusCode.OK);

        Assert.Equal((100_000m, "Soporte"), (kept.Project!.Total!.Value, Assert.Single(kept.Project.Items).Text));
        Assert.Equal(100_000m, Assert.Single(kept.Items).UnitPrice);
        Assert.Equal("USD", repriced.Currency);
        Assert.Null(repriced.Project!.Total);
        Assert.Equal("Soporte", Assert.Single(repriced.Project.Items).Text);
        Assert.Equal(0m, Assert.Single(repriced.Items).UnitPrice);
    }

    [Fact]
    public async Task AStaleTabGets412BeforePrices()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var usd = await CreateAccountAsync(setup, "USD");
        var created = await CreateProjectAsync(setup, Global(setup.ServiceId, 100_000m));
        var moved = await ReadAsync(
            await PutAsync(setup, created, Save(Global(setup.ServiceId, 50m, currency: "USD"), usd)), HttpStatusCode.OK);

        var stale = await PutAsync(setup, created, Save(Global(setup.ServiceId, 900_000m)));

        await AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency.conflict");
        var current = await GetAsync(setup, created.Id);
        Assert.Equal((moved.Version, "USD", 50m), (current.Version, current.Currency, current.Total));
    }

    [Fact]
    public async Task AProjectBlockOnAStandardQuotationIsAFieldError()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var standard = await CreateQuotationAsync(setup.Client, setup.TenantId, setup.ClientId);
        var body = Save(Global(setup.ServiceId, 100_000m));

        var saved = await PutAsync(setup, standard, body);
        var preview = await setup.Client.PostAsJsonAsync(
            $"{QuotationUrl(setup, standard.Id)}/preview", body, TestContext.Current.CancellationToken);

        foreach (var response in new[] { saved, preview })
        {
            await AssertFieldErrorAsync(response, "Project");
        }

        Assert.Equal("Standard", standard.Kind);
        Assert.Null(standard.Project);
        Assert.Equal(standard.Version, (await GetAsync(setup, standard.Id)).Version);
    }

    [Fact]
    public async Task RetailFloorAndShippingAreIgnoredOnAProject()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var shipping = new ShippingRequest(null, "Charged", 15_000m, true);

        var created = await ReadAsync(
            await PostAsync(
                setup,
                Create(setup, Global(setup.ServiceId, 100_000m)) with
                {
                    IsRetail = true,
                    GlobalScaleFloor = 6,
                    Shipping = shipping,
                }),
            HttpStatusCode.Created);
        var saved = await ReadAsync(
            await PutAsync(
                setup,
                created,
                Save(Global(setup.ServiceId, 100_000m)) with { IsRetail = true, GlobalScaleFloor = 6, Shipping = shipping }),
            HttpStatusCode.OK);

        foreach (var document in new[] { created, saved })
        {
            Assert.False(document.IsRetail);
            Assert.Null(document.GlobalScaleFloor);
            Assert.Null(document.Shipping.ChargeMode);
            Assert.Equal((0m, false), (document.Shipping.FreightAmount, document.Shipping.CashOnDelivery));
            Assert.Equal(100_000m, document.Total);
        }
    }

    // §6.5: «se ignoran» incluye no validarlo. Un envío mal escrito es 422 sólo en una cotización estándar.
    [Fact]
    public async Task AnInvalidShippingBlockIsIgnoredOnAProject()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var invalid = new ShippingRequest(null, "Teleport", 15_000.123m, false);
        var create = Create(setup, Global(setup.ServiceId, 100_000m)) with { Shipping = invalid };

        var created = await ReadAsync(await PostAsync(setup, create), HttpStatusCode.Created);
        var newPreview = await ReadAsync(
            await setup.Client.PostAsJsonAsync(
                $"{QuotationsUrl(setup.TenantId)}/preview", create, TestContext.Current.CancellationToken),
            HttpStatusCode.OK);
        var edit = Save(Global(setup.ServiceId, 200_000m)) with { Shipping = invalid };
        var preview = await ReadAsync(
            await setup.Client.PostAsJsonAsync(
                $"{QuotationUrl(setup, created.Id)}/preview", edit, TestContext.Current.CancellationToken),
            HttpStatusCode.OK);
        var saved = await ReadAsync(await PutAsync(setup, created, edit), HttpStatusCode.OK);

        foreach (var document in new[] { created, newPreview, preview, saved })
        {
            Assert.Null(document.Shipping.ChargeMode);
            Assert.Equal(0m, document.Shipping.FreightAmount);
        }

        Assert.Equal(200_000m, saved.Total);

        var standard = await CreateQuotationAsync(setup.Client, setup.TenantId, setup.ClientId);
        await AssertFieldErrorAsync(
            await PutAsync(setup, standard, Save(null) with { Shipping = invalid }), "Shipping.FreightChargeMode");
        await AssertFieldErrorAsync(
            await setup.Client.PostAsJsonAsync(
                $"{QuotationUrl(setup, standard.Id)}/preview",
                Save(null) with { Shipping = invalid },
                TestContext.Current.CancellationToken),
            "Shipping.FreightChargeMode");
        await AssertFieldErrorAsync(
            await PostAsync(setup, new CreateQuotationRequest(setup.ClientId, null, null, null, null, null, Shipping: invalid)),
            "Shipping.FreightChargeMode");
    }

    // El precio cabe en la columna tal como se escribió, pero redondeado a pesos ya no: 422 en el campo y no un 500.
    [Fact]
    public async Task APriceThatRoundsPastTheColumnIsAFieldError()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;

        await AssertFieldErrorAsync(
            await PostAsync(setup, Create(setup, Global(setup.ServiceId, 999_999_999_999.99m))), "Project.GlobalPrice");
        await AssertFieldErrorAsync(
            await PostAsync(setup, Create(setup, PerPhase(setup.ServiceId, 999_999_999_999m, 0.5m))), "Project.Phases");
    }

    // El precio cabía con centavos; con la regla nueva del tenant sube a la columna siguiente. Guardar sin `project` aplica
    // la regla, así que es el mismo 422 en el campo, y la cotización queda como estaba.
    [Fact]
    public async Task ACoarserRuleOnASaveWithoutAProjectIsAFieldError()
    {
        var setup = await SetUpAsync(
            await sharedHost.StartTestAsync(), [.. ManagerPermissions, TenancyPermissions.SettingsUpdate]);
        using var _ = setup.Client;
        // En dólares: el peso no admite centavos.
        var usd = await CreateAccountAsync(setup, "USD");
        var created = await ReadAsync(
            await PostAsync(
                setup,
                Create(setup, Global(setup.ServiceId, 999_999_999_999.40m, currency: "USD")) with { BillingAccount = usd }),
            HttpStatusCode.Created);
        Assert.Equal(999_999_999_999.40m, created.Total);
        await PutRoundingAsync(setup, "USD", decimals: 0, mode: "Up");

        var saved = await PutAsync(setup, created, Save(null) with { Notes = "Sólo la nota" });
        var preview = await setup.Client.PostAsJsonAsync(
            $"{QuotationUrl(setup, created.Id)}/preview", Save(null), TestContext.Current.CancellationToken);

        foreach (var response in new[] { saved, preview })
        {
            await AssertFieldErrorAsync(response, "Project.GlobalPrice");
        }

        var current = await GetAsync(setup, created.Id);
        Assert.Equal((created.Version, 999_999_999_999.40m), (current.Version, current.Total));
    }

    private static async Task PutRoundingAsync(Setup setup, string currency, int decimals, string mode)
    {
        var response = await setup.Client.PutAsJsonAsync(
            $"/api/v1/tenants/{setup.TenantId}/settings/price-rounding",
            new
            {
                currencies = RoundingCurrencies.Select(code => new
                {
                    currency = code,
                    // El peso no admite decimales; las otras dos, dos.
                    decimals = code == currency ? decimals : code == "COP" ? 0 : 2,
                    mode = code == currency ? mode : "Nearest",
                }),
            },
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SavingTheScopeOrThePriceLeavesHistory()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var created = await CreateProjectAsync(setup, Global(setup.ServiceId, 100_000m));

        // La vigencia que ya tiene, para que la fila del historial sea sólo la del proyecto.
        SaveQuotationRequest Same(ProjectScopeRequest project) => Save(project) with { ValidUntil = created.ValidUntil };

        var repriced = await ReadAsync(
            await PutAsync(setup, created, Same(Global(setup.ServiceId, 200_000m))), HttpStatusCode.OK);
        await ReadAsync(
            await PutAsync(setup, repriced, Same(Global(setup.ServiceId, 200_000m, "Otro texto"))), HttpStatusCode.OK);

        var history = await setup.Client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationUrl(setup, created.Id)}/history", TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        var edits = history.Items
            .Where(entry => entry.EventType == "Edited")
            .OrderBy(entry => entry.EventAt)
            .Select(entry => entry.Details)
            .ToArray();
        Assert.Equal(PriceThenScopeHistory, edits);
    }

    // Spec §7 (P9): con el módulo apagado, toda operación sobre una cotización de proyecto es 403; las demás siguen.
    [Theory]
    [InlineData("get")]
    [InlineData("history")]
    [InlineData("save")]
    [InlineData("preview")]
    [InlineData("change-client")]
    [InlineData("add-item")]
    [InlineData("batch-items")]
    [InlineData("update-item")]
    [InlineData("remove-item")]
    [InlineData("send")]
    [InlineData("mark-sent")]
    [InlineData("pdf")]
    [InlineData("void")]
    [InlineData("convert")]
    [InlineData("convert-preview")]
    [InlineData("create")]
    [InlineData("create-preview")]
    [InlineData("list")]
    [InlineData("export")]
    public async Task WithTheModuleOffEveryProjectQuotationRouteIs403(string route)
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var project = await CreateProjectAsync(setup, Global(setup.ServiceId, 100_000m));
        var standard = await CreateQuotationAsync(setup.Client, setup.TenantId, setup.ClientId);
        await DisableModuleAsync(setup.Factory, setup.TenantId, TenantModuleKeys.Projects);

        var response = await CallAsync(setup, project, route);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "tenancy.module_not_enabled");
        Assert.Equal("Standard", (await GetAsync(setup, standard.Id)).Kind);
    }

    [Fact]
    public async Task ListsDefaultToStandardAndRowsCarryKindAndMode()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var project = await CreateProjectAsync(setup, PerPhase(setup.ServiceId, 600_000m, 400_000m));
        var standard = await CreateQuotationAsync(setup.Client, setup.TenantId, setup.ClientId);

        var byDefault = await ListAsync(setup, string.Empty);
        var projects = await ListAsync(setup, "?kind=Project");
        var all = await ListAsync(setup, "?kind=all");

        var standardRow = Assert.Single(byDefault.Items);
        Assert.Equal((standard.Id, "Standard", null), (standardRow.Id, standardRow.Kind, standardRow.PricingMode));
        Assert.Equal(1, byDefault.Total);
        var projectRow = Assert.Single(projects.Items);
        Assert.Equal(
            (project.Id, "Project", "PerPhase", false),
            (projectRow.Id, projectRow.Kind, projectRow.PricingMode, projectRow.RequiresShipping));
        Assert.Equal(2, all.Total);
        Assert.Equal(
            new HashSet<(Guid, string?)> { (project.Id, "PerPhase"), (standard.Id, null) },
            all.Items.Select(item => (item.Id, item.PricingMode)).ToHashSet());
        await AssertFieldErrorAsync(
            await setup.Client.GetAsync(
                $"{QuotationsUrl(setup.TenantId)}?kind=Other", TestContext.Current.CancellationToken),
            "kind");
        await AssertFieldErrorAsync(
            await setup.Client.PostAsync(
                $"{QuotationsUrl(setup.TenantId)}/export?{ExportRange()}&kind=Other",
                content: null,
                TestContext.Current.CancellationToken),
            "kind");

        await DisableModuleAsync(setup.Factory, setup.TenantId, TenantModuleKeys.Projects);

        Assert.Equal(standard.Id, Assert.Single((await ListAsync(setup, "?kind=All")).Items).Id);
        Assert.Equal(standard.Id, Assert.Single((await ListAsync(setup, string.Empty)).Items).Id);
    }

    // El worker reclama jobs de toda la base: esta prueba levanta su propio host.
    [Fact]
    public async Task TheExportFollowsTheKindFilter()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await SetUpAsync(factory);
        using var _ = setup.Client;
        var project = await CreateProjectAsync(setup, Global(setup.ServiceId, 100_000m));
        var standard = await CreateQuotationAsync(setup.Client, setup.TenantId, setup.ClientId);

        Assert.Equal([project.QuotationNumber], await ExportedNumbersAsync(setup, "&kind=Project"));
        Assert.Equal([standard.QuotationNumber], await ExportedNumbersAsync(setup, string.Empty));
        // El proyecto se creó primero: ordenados por número, va antes.
        Assert.Equal(
            [project.QuotationNumber, standard.QuotationNumber], await ExportedNumbersAsync(setup, "&kind=All"));

        await DisableModuleAsync(factory, setup.TenantId, TenantModuleKeys.Projects);

        Assert.Equal([standard.QuotationNumber], await ExportedNumbersAsync(setup, "&kind=All"));
    }

    [Fact]
    public async Task ItemRoutesOnAProjectAreRejected()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var project = await CreateProjectAsync(setup, Global(setup.ServiceId, 100_000m));

        foreach (var route in new[] { "add-item", "batch-items", "update-item", "remove-item" })
        {
            await AssertProblemAsync(
                await CallAsync(setup, project, route),
                HttpStatusCode.UnprocessableEntity,
                "quotation.project.items_not_allowed");
        }

        // Antes de leer el catálogo: una variante que no existe responde lo mismo.
        await AssertProblemAsync(
            await setup.Client.PostAsJsonAsync(
                $"{QuotationUrl(setup, project.Id)}/items",
                new AddQuotationItemRequest(Guid.CreateVersion7(), 1m),
                TestContext.Current.CancellationToken),
            HttpStatusCode.UnprocessableEntity,
            "quotation.project.items_not_allowed");
        Assert.Equal(project.Version, (await GetAsync(setup, project.Id)).Version);
    }

    [Fact]
    public async Task SendingOrConvertingWithoutServiceOrTotalIsIncomplete()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var account = await CreateAccountAsync(setup, "COP");
        var withoutService = await ReadAsync(
            await PostAsync(
                setup,
                Create(setup, new ProjectScopeRequest(null, "Global", 100_000m, null, null, "COP")) with
                {
                    BillingAccount = account,
                }),
            HttpStatusCode.Created);
        var withoutPrice = await ReadAsync(
            await PostAsync(
                setup,
                Create(setup, new ProjectScopeRequest(setup.ServiceId, "Global", null, null, null, null)) with
                {
                    BillingAccount = account,
                }),
            HttpStatusCode.Created);

        foreach (var quotation in new[] { withoutService, withoutPrice })
        {
            Assert.False(quotation.CanBeConvertedToOrder);
            foreach (var route in new[] { "send", "mark-sent", "convert", "convert-preview" })
            {
                await AssertProblemAsync(
                    await CallAsync(setup, quotation, route),
                    HttpStatusCode.UnprocessableEntity,
                    "quotation.project.incomplete");
            }

            Assert.Equal("Draft", (await GetAsync(setup, quotation.Id)).Status);
        }
    }

    [Fact]
    public async Task ConvertingCopiesKindScopeAndLine()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var account = await CreateAccountAsync(setup, "COP");
        var quotation = await ReadAsync(
            await PostAsync(
                setup, Create(setup, PerPhase(setup.ServiceId, 600_000m, 400_000m)) with { BillingAccount = account }),
            HttpStatusCode.Created);
        var sent = await ReadAsync(await CallAsync(setup, quotation, "mark-sent"), HttpStatusCode.OK);
        Assert.Equal("Sent", sent.Status);

        var response = await CallAsync(setup, sent, "convert");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var converted = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(converted);
        var ordersUrl = $"/api/v1/tenants/{setup.TenantId}/orders/{converted.Id}";
        var detail = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            ordersUrl, TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        foreach (var order in new[] { converted, detail.Order })
        {
            Assert.Equal("Project", order.Kind);
            Assert.False(order.RequiresShipping);
            Assert.NotNull(order.Project);
            Assert.Equal(("PerPhase", 1_000_000m, "COP"), (order.Project.PricingMode, order.Project.Total!.Value, order.Project.PricesCurrency));
            Assert.Equal([FirstPhase, SecondPhase], order.Project.Phases.Select(phase => phase.Id));
            Assert.Equal(PhaseItem, Assert.Single(order.Project.Phases[0].Items).Id);
        }

        var line = Assert.Single(detail.Items);
        Assert.Equal((setup.ServiceId, 1m, 1_000_000m), (line.VariantId, line.Quantity, line.UnitPrice));
        var rows = await setup.Client.GetFromJsonAsync<OrdersPageResponse>(
            $"/api/v1/tenants/{setup.TenantId}/orders", TestContext.Current.CancellationToken);
        Assert.Equal("Project", Assert.Single(rows!.Items).Kind);

        // P9: el pedido de proyecto se sigue viendo con el módulo apagado.
        await DisableModuleAsync(setup.Factory, setup.TenantId, TenantModuleKeys.Projects);

        foreach (var url in new[] { ordersUrl, $"{QuotationUrl(setup, quotation.Id)}/order" })
        {
            var after = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
                url, TestContext.Current.CancellationToken);
            Assert.Equal("Project", after!.Order.Kind);
        }
    }

    // Spec §5: borrar el producto del servicio no le quita la línea a un borrador de proyecto ni lo reprecia; se congela su
    // foto, como la de una cotización enviada. Un borrador estándar pierde la línea del producto borrado, como siempre.
    [Fact]
    public async Task DeletingTheServiceProductKeepsTheProjectDraftLine()
    {
        var setup = await SetUpAsync();
        using var _ = setup.Client;
        var project = await CreateProjectAsync(setup, Global(setup.ServiceId, 100_000m));
        var otherProduct = await CreateProductWithScalesAsync(setup.Client, setup.TenantId);
        var standard = await CreateQuotationAsync(setup.Client, setup.TenantId, setup.ClientId);
        var added = await setup.Client.PostAsJsonAsync(
            $"{QuotationUrl(setup, standard.Id)}/items",
            new AddQuotationItemRequest(otherProduct, 1m),
            TestContext.Current.CancellationToken);
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var before = Assert.Single(project.Items);

        await DeleteProductAsync(setup, setup.ServiceId);
        await DeleteProductAsync(setup, otherProduct);

        var after = await GetAsync(setup, project.Id);
        var line = Assert.Single(after.Items);
        Assert.Equal(
            (before.Id, setup.ServiceId, "Desarrollo a la medida", before.ProductCode, 100_000m),
            (line.Id, line.VariantId, line.ProductName, line.ProductCode, line.UnitPrice));
        Assert.False(string.IsNullOrEmpty(line.ProductCode));
        Assert.Equal((project.Version, 100_000m, 100_000m), (after.Version, after.Total, after.Project!.Total!.Value));
        Assert.Empty((await GetAsync(setup, standard.Id)).Items);

        // El servicio guardado no cambia, así que el borrador se sigue guardando con su línea congelada.
        var saved = await ReadAsync(
            await PutAsync(setup, after, Save(Global(setup.ServiceId, 250_000m))), HttpStatusCode.OK);
        var repriced = Assert.Single(saved.Items);
        Assert.Equal((before.Id, "Desarrollo a la medida", 250_000m), (repriced.Id, repriced.ProductName, repriced.UnitPrice));
    }

    private static async Task DeleteProductAsync(Setup setup, Guid productId)
    {
        var url = $"/api/v1/tenants/{setup.TenantId}/catalog/products/{productId}";
        var product = await setup.Client.GetFromJsonAsync<JsonElement>(url, TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{product.GetProperty("version").GetInt64()}\""));
        var response = await setup.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static Task<HttpResponseMessage> CallAsync(Setup setup, QuotationResponse quotation, string route)
    {
        var token = TestContext.Current.CancellationToken;
        var url = QuotationUrl(setup, quotation.Id);
        var quotations = QuotationsUrl(setup.TenantId);
        var itemId = quotation.Items.FirstOrDefault()?.Id ?? Guid.CreateVersion7();
        var scope = Global(setup.ServiceId, 100_000m);

        return route switch
        {
            "get" => setup.Client.GetAsync(url, token),
            "history" => setup.Client.GetAsync($"{url}/history", token),
            "save" => PutAsync(setup, quotation, Save(scope)),
            "preview" => setup.Client.PostAsJsonAsync($"{url}/preview", Save(scope), token),
            "change-client" => setup.Client.PutAsJsonAsync(
                $"{url}/client", new ChangeQuotationClientRequest(setup.ClientId), token),
            "add-item" => setup.Client.PostAsJsonAsync(
                $"{url}/items", new AddQuotationItemRequest(setup.ServiceId, 1m), token),
            "batch-items" => setup.Client.PostAsJsonAsync(
                $"{url}/items/batch",
                new BatchUpdateQuotationItemsRequest([new(setup.ServiceId, 1m)], []),
                token),
            "update-item" => setup.Client.PutAsJsonAsync(
                $"{url}/items/{itemId}", new UpdateQuotationItemRequest(2m), token),
            "remove-item" => setup.Client.DeleteAsync($"{url}/items/{itemId}", token),
            "send" => setup.Client.PostAsync($"{url}/send", content: null, token),
            "mark-sent" => setup.Client.PostAsync($"{url}/mark-sent", content: null, token),
            "pdf" => setup.Client.PostAsync($"{url}/pdf", content: null, token),
            "void" => setup.Client.PostAsync($"{url}/void", content: null, token),
            "convert" => setup.Client.PostAsJsonAsync($"{url}/order", new ConvertQuotationToOrderRequest(null, []), token),
            "convert-preview" => setup.Client.GetAsync($"{url}/order/preview", token),
            "create" => PostAsync(setup, Create(setup, scope)),
            "create-preview" => setup.Client.PostAsJsonAsync($"{quotations}/preview", Create(setup, scope), token),
            "list" => setup.Client.GetAsync($"{quotations}?kind=Project", token),
            "export" => setup.Client.PostAsync($"{quotations}/export?{ExportRange()}&kind=Project", content: null, token),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown route."),
        };
    }

    private static string ExportRange() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"createdFrom={TodayInBogota().AddDays(-1):yyyy-MM-dd}&createdTo={TodayInBogota():yyyy-MM-dd}");

    private static async Task<QuotationsPageResponse> ListAsync(Setup setup, string query)
    {
        var page = await setup.Client.GetFromJsonAsync<QuotationsPageResponse>(
            $"{QuotationsUrl(setup.TenantId)}{query}", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }

    // Pide el Excel, corre el job y devuelve la columna «Numero» ordenada.
    private static async Task<string[]> ExportedNumbersAsync(Setup setup, string kind)
    {
        var response = await setup.Client.PostAsync(
            $"{QuotationsUrl(setup.TenantId)}/export?{ExportRange()}{kind}",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<ExportJobAcceptedResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(accepted);
        Assert.Equal(ExportJobRunOutcome.Completed, await RunExportJobAsync(setup.Factory));
        var sheet = ExportWorkbookReader.Read(await setup.Factory.ObjectStorage.DownloadAsync(
            $"exports/tenants/{setup.TenantId:N}/jobs/{accepted.JobId:N}.xlsx", TestContext.Current.CancellationToken));
        return [.. sheet.Rows.Skip(1).Select(row => row[0]).Order(StringComparer.Ordinal).Select(number => number!)];
    }

    private sealed record Setup(
        QepApiFactory Factory, HttpClient Client, Guid TenantId, Guid ClientId, Guid ServiceId);

    private async Task<Setup> SetUpAsync() => await SetUpAsync(await sharedHost.StartTestAsync());

    // Un tenant con el módulo prendido, un cliente activo y un servicio sin IVA. Sin cuenta de cobro, la cotización
    // nace en la moneda del tenant (COP).
    private static async Task<Setup> SetUpAsync(QepApiFactory factory, string[]? permissions = null)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, permissions ?? ManagerPermissions);
        await EnableModuleAsync(factory, tenantId, TenantModuleKeys.Projects);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var serviceId = await CreateServiceAsync(client, tenantId);
        return new Setup(factory, client, tenantId, clientId, serviceId);
    }

    internal static async Task<Guid> CreateServiceAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name = "Desarrollo a la medida",
                kind = "Service",
                salesUnit = "Hour",
                variants = new[]
                {
                    new { code = $"SRV-{Guid.NewGuid():N}"[..12], pricing = Pricing(100_000m, NoDiscountScales()) },
                },
            },
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await response.Content.ReadFromJsonAsync<ProductDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    private static async Task<QuotationBillingAccountRequest> CreateAccountAsync(Setup setup, string currency)
    {
        var company = await CreateCompanyWithBankAccountAsync(setup.Client, setup.TenantId, currency);
        return new QuotationBillingAccountRequest(
            company.CompanyId, company.BankName, company.AccountNumber, company.Currency);
    }

    private static ProjectScopeRequest Global(
        Guid serviceId, decimal price, string item = "Soporte", string currency = "COP") =>
        new(serviceId, "Global", price, null, [new ProjectItemRequest(LooseItem, item)], currency);

    private static ProjectScopeRequest PerPhase(Guid serviceId, decimal first, decimal second, string currency = "COP") =>
        new(
            serviceId,
            "PerPhase",
            null,
            [
                new ProjectPhaseRequest(
                    FirstPhase, "Descubrimiento", first, [new ProjectItemRequest(PhaseItem, "Talleres")]),
                new ProjectPhaseRequest(SecondPhase, "Entrega", second, null),
            ],
            null,
            currency);

    private static CreateQuotationRequest Create(Setup setup, ProjectScopeRequest project) =>
        new(setup.ClientId, null, null, null, null, null, Kind: "Project", Project: project);

    private static SaveQuotationRequest Save(
        ProjectScopeRequest? project, QuotationBillingAccountRequest? account = null) =>
        new(TodayInBogota().AddDays(30), null, null, null, account, null, Project: project);

    private static string QuotationUrl(Setup setup, Guid quotationId) =>
        $"{QuotationsUrl(setup.TenantId)}/{quotationId}";

    private static Task<HttpResponseMessage> PostAsync(Setup setup, CreateQuotationRequest request) =>
        setup.Client.PostAsJsonAsync(QuotationsUrl(setup.TenantId), request, TestContext.Current.CancellationToken);

    private static async Task<QuotationResponse> CreateProjectAsync(Setup setup, ProjectScopeRequest project) =>
        await ReadAsync(await PostAsync(setup, Create(setup, project)), HttpStatusCode.Created);

    private static async Task<HttpResponseMessage> PutAsync(
        Setup setup, QuotationResponse loaded, SaveQuotationRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, QuotationUrl(setup, loaded.Id))
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{loaded.Version}\""));
        return await setup.Client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private static async Task<QuotationResponse> GetAsync(Setup setup, Guid quotationId) =>
        await ReadAsync(
            await setup.Client.GetAsync(QuotationUrl(setup, quotationId), TestContext.Current.CancellationToken),
            HttpStatusCode.OK);

    private static async Task<QuotationResponse> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(
            response.StatusCode == expected,
            $"Expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        var body = await response.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation.failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    // El consecutivo es lo que va después del último guion del número.
    private static int Sequence(QuotationResponse quotation) => int.Parse(
        quotation.QuotationNumber[(quotation.QuotationNumber.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);
}
