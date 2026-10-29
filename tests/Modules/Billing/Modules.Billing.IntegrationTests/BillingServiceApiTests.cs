using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Billing.Application;
using Modules.Billing.Domain;
using Modules.Billing.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using static Modules.Billing.IntegrationTests.BillingApiHarness;

namespace Modules.Billing.IntegrationTests;

/// <summary>
/// Los servicios recurrentes (spec cobros-recurrentes §3.2 y §4): crear, leer, listar, editar, pausar, reanudar y cancelar,
/// más el conteo en seco del formulario. De punta a punta por HTTP: política, tenant de la ruta, <c>If-Match</c>, generación de
/// cobros en la misma transacción y auditoría atómica.
/// </summary>
public sealed class BillingServiceApiTests
{
    private const decimal Price = 150000m;

    private static readonly Guid Tenant = Guid.Parse(BillingApiHarness.TenantId);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Los tenants de las pruebas viven en America/Bogota (UTC-5, sin horario de verano): la «hoy» del tenant.
    private static DateOnly TenantToday => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

    private static string ServicesUrl(string tenantId = BillingApiHarness.TenantId) => $"{BillingUrl(tenantId)}/services";

    private static string ServiceUrl(Guid serviceId, string tenantId = BillingApiHarness.TenantId) =>
        $"{ServicesUrl(tenantId)}/{serviceId}";

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Sin las listas (comparan por referencia) ni los instantes: la base los guarda al microsegundo y el alta responde con el tick.
    private static ServiceDto Normalized(ServiceDto service) =>
        service with { OpenAmountByCurrency = [], NextCharge = null, CreatedAt = default, UpdatedAt = default };

    private static bool Has(JsonElement errors, string field) => errors.TryGetProperty(field, out _);

    private static object ServiceBody(
        Guid customerId,
        object? profile,
        string name = "Hosting",
        decimal price = Price,
        string? currency = null,
        string cycle = "Monthly",
        DateOnly? firstDueDate = null) => new
        {
            customerId,
            name,
            price,
            currency,
            cycle,
            firstDueDate = Iso(firstDueDate ?? TenantToday.AddMonths(2)),
            profile,
        };

    private static object ProfileBody(string channel, Guid assigneeMemberId) => new { channel, assigneeMemberId };

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<JsonElement> ErrorsOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("errors").Clone();
    }

    private static HttpRequestMessage WithVersion(HttpMethod method, string url, long? version, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static async Task<ServiceDetailDto> ReadDetailAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await BodyAsync(response));
        return (await response.Content.ReadFromJsonAsync<ServiceDetailDto>(Ct))!;
    }

    private static async Task<ServiceDetailDto> CreateAsync(HttpClient client, object body) =>
        await ReadDetailAsync(await client.PostAsJsonAsync(ServicesUrl(), body, Ct));

    private static Task<HttpResponseMessage> TransitionAsync(HttpClient client, Guid serviceId, string verb, long? version) =>
        client.SendAsync(WithVersion(HttpMethod.Post, $"{ServiceUrl(serviceId)}/{verb}", version), Ct);

    /// <summary>Un cliente con teléfono y correo y un encargado admin: lo mínimo para crear un servicio con perfil.</summary>
    private static async Task<(Guid CustomerId, Guid Admin)> SeedCustomerAndAdminAsync(
        QepApiFactory factory, string customerName = "Panadería La Esquina")
    {
        var customerId = await SeedCustomerAsync(factory, Tenant, customerName, "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", "Ana Admin", "admin");
        return (customerId, admin);
    }

    [Fact]
    public async Task CreateWithProfileCreatesServiceProfileAndFirstCharge()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var first = TenantToday.AddMonths(2);

        var response = await manager.PostAsJsonAsync(
            ServicesUrl(), ServiceBody(customerId, ProfileBody("WhatsApp", admin), firstDueDate: first), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await ReadDetailAsync(response);
        Assert.EndsWith($"/services/{detail.Service.Id}", response.Headers.Location!.ToString());
        Assert.Equal("Active", detail.Service.Status);
        Assert.Equal(1, detail.Service.Version);
        Assert.Equal("Panadería La Esquina", detail.Service.Customer.Name);
        Assert.Equal("COP", detail.Service.Currency);
        Assert.Equal("WhatsApp", detail.Profile.Channel);
        Assert.Equal(admin, detail.Profile.Assignee.MemberId);
        var charge = Assert.Single(detail.Charges);
        Assert.Equal(first, charge.DueDate);
        Assert.Equal("Pending", charge.Status);
        Assert.Equal(0, charge.DaysOverdue);
        Assert.Equal(Price, charge.Amount);
        Assert.Equal(admin, charge.Assignee!.MemberId);
        Assert.Equal(first, detail.Service.NextCharge!.DueDate);
        Assert.Equal(1, detail.Service.OpenCount);
        Assert.Equal([new CurrencyAmountDto("COP", Price)], detail.Service.OpenAmountByCurrency);

        // El perfil quedó guardado y la auditoría escribió el alta del servicio y la del perfil en la misma transacción.
        var profile = await manager.GetAsync($"{BillingUrl()}/customers/{customerId}/profile", Ct);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, BillingAuditActions.ServiceCreated, detail.Service.Id.ToString()));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, BillingAuditActions.ProfileUpdated, customerId.ToString()));
    }

    // Review Focus 4: la migración desde el Excel trae vencimientos pasados; cada período nace con su propio cobro.
    [Fact]
    public async Task CreateWithAPastFirstDueDateGeneratesEveryPeriodUpToTheWindow()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);

        var detail = await CreateAsync(
            manager, ServiceBody(customerId, ProfileBody("Email", admin), firstDueDate: TenantToday.AddMonths(-3)));

        Assert.Equal(4, detail.Charges.Count);
        var overdue = detail.Charges.Where(charge => charge.Status == "Overdue").ToList();
        Assert.Equal(3, overdue.Count);
        Assert.All(overdue, charge => Assert.True(charge.DaysOverdue > 0));
        var dueToday = Assert.Single(detail.Charges, charge => charge.Status == "Pending");
        Assert.Equal(TenantToday, dueToday.DueDate);
        // Por vencimiento descendente, como el detalle.
        Assert.Equal(detail.Charges.OrderByDescending(charge => charge.DueDate).Select(charge => charge.Id), detail.Charges.Select(charge => charge.Id));
        Assert.Equal(4, detail.Service.OpenCount);
        Assert.Equal([new CurrencyAmountDto("COP", 4 * Price)], detail.Service.OpenAmountByCurrency);
        Assert.Equal(TenantToday.AddMonths(-3), detail.Service.NextCharge!.DueDate);
    }

    // Task 2 diferido (a): NextDueDateToGenerate ignora la reactivación cuando nunca generó; es inalcanzable porque crear siempre
    // genera el primer cobro. Si alguien deja de generar al crear, esta prueba lo atrapa.
    [Fact]
    public async Task CreateAlwaysGeneratesTheFirstChargeSoAPauseAndResumeNeverSeesAnUngeneratedService()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        // Muy por delante de la ventana: sin la regla «al menos el primero» no nacería nada.
        var created = await CreateAsync(
            manager, ServiceBody(customerId, ProfileBody("Email", admin), firstDueDate: TenantToday.AddMonths(8)));
        Assert.Single(created.Charges);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var stored = await new BillingServiceRepository(db)
            .FindAsync(Tenant, new BillingServiceId(created.Service.Id), Ct);
        Assert.NotNull(stored!.LastGeneratedDueDate);
        Assert.Equal(created.Service.FirstDueDate, stored.LastGeneratedDueDate);
    }

    [Fact]
    public async Task CreateWithoutProfileForACustomerWithoutOneAnswers422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, _admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);

        var response = await manager.PostAsJsonAsync(ServicesUrl(), ServiceBody(customerId, profile: null), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.profile.missing", await CodeOfAsync(response));
    }

    [Fact]
    public async Task CreateWithAnInvalidProfileForACustomerWithoutOneAnswersTheProfileCode()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Sin teléfono", phoneE164: null, email: "sin@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        var advisor = await SeedMemberAsync(factory, "caro@example.com", "Caro Asesora", "advisor");
        using var manager = CreateManager(factory);

        var noPhone = await manager.PostAsJsonAsync(ServicesUrl(), ServiceBody(customerId, ProfileBody("WhatsApp", admin)), Ct);
        var badAssignee = await manager.PostAsJsonAsync(ServicesUrl(), ServiceBody(customerId, ProfileBody("Email", advisor)), Ct);
        var badChannel = await manager.PostAsJsonAsync(ServicesUrl(), ServiceBody(customerId, ProfileBody("Sms", admin)), Ct);

        Assert.Equal("billing.profile.whatsapp_requires_phone", await CodeOfAsync(noPhone));
        Assert.Equal("billing.profile.assignee_invalid", await CodeOfAsync(badAssignee));
        Assert.Equal("validation.failed", await CodeOfAsync(badChannel));
        // Nada de lo anterior dejó un servicio a medias.
        var list = await manager.GetFromJsonAsync<ServicePageDto>($"{ServicesUrl()}?status=all", Ct);
        Assert.Equal(0, list!.Total);
    }

    [Fact]
    public async Task CreateIgnoresTheProfileWhenTheCustomerAlreadyHasOne()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        var biller = await SeedMemberAsync(factory, "bea@example.com", "Bea Facturación", "billing");
        using var manager = CreateManager(factory);
        var put = await manager.PutAsJsonAsync(
            $"{BillingUrl()}/customers/{customerId}/profile", ProfileBody("WhatsApp", admin), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var detail = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", biller)));

        Assert.Equal("WhatsApp", detail.Profile.Channel);
        Assert.Equal(admin, detail.Profile.Assignee.MemberId);
        Assert.Equal(1, detail.Profile.Version);
    }

    [Fact]
    public async Task CreateWithNullCurrencyUsesTheTenantDefault()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);

        var detail = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin), currency: null));
        var lowercase = await CreateAsync(manager, ServiceBody(customerId, null, name: "Dominio", currency: "usd"));

        Assert.Equal("COP", detail.Service.Currency);
        Assert.Equal("COP", Assert.Single(detail.Charges).Currency);
        Assert.Equal("USD", lowercase.Service.Currency);
    }

    [Fact]
    public async Task CreateWithAnUnknownCurrencyAnswers422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);

        var response = await manager.PostAsJsonAsync(
            ServicesUrl(), ServiceBody(customerId, ProfileBody("Email", admin), currency: "XYZ"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.currency.unknown", await CodeOfAsync(response));
    }

    [Fact]
    public async Task CreateForAnInactiveCustomerAnswers422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        await DeactivateCustomerAsync(factory, customerId);
        using var manager = CreateManager(factory);

        var response = await manager.PostAsJsonAsync(ServicesUrl(), ServiceBody(customerId, ProfileBody("Email", admin)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.customer.inactive", await CodeOfAsync(response));
    }

    [Fact]
    public async Task CreateForAnUnknownOrForeignCustomerAnswers422CustomerNotFound()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, BillingApiHarness.OtherTenantId);
        var foreign = await SeedCustomerAsync(
            factory, Guid.Parse(BillingApiHarness.OtherTenantId), "Ajeno", "+573009998877", "ajeno@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        using var manager = CreateManager(factory);

        var unknown = await manager.PostAsJsonAsync(
            ServicesUrl(), ServiceBody(Guid.CreateVersion7(), ProfileBody("Email", admin)), Ct);
        var other = await manager.PostAsJsonAsync(ServicesUrl(), ServiceBody(foreign, ProfileBody("Email", admin)), Ct);

        foreach (var response in new[] { unknown, other })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("billing.customer.not_found", await CodeOfAsync(response));
        }
    }

    [Fact]
    public async Task CreateValidatesNamePriceCycleAndDate()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);

        var response = await manager.PostAsJsonAsync(
            ServicesUrl(),
            ServiceBody(
                customerId,
                ProfileBody("Email", admin),
                name: "  ",
                price: 0m,
                cycle: "Weekly",
                firstDueDate: new DateOnly(1990, 1, 1)),
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        var errors = await ErrorsOfAsync(response);
        Assert.True(Has(errors, "Name"));
        Assert.True(Has(errors, "Price"));
        Assert.True(Has(errors, "Cycle"));
        Assert.True(Has(errors, "FirstDueDate"));
    }

    [Fact]
    public async Task CreateWithAnAbsurdlyDistantFirstDueDateAnswers422BeforeLoopingOverTheCalendar()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);

        var response = await manager.PostAsJsonAsync(
            ServicesUrl(), ServiceBody(customerId, ProfileBody("Email", admin), firstDueDate: new DateOnly(2999, 1, 1)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(Has((await ErrorsOfAsync(response)), "FirstDueDate"));
    }

    [Fact]
    public async Task UpdateChangesNamePriceCurrencyAndKeepsTheGeneratedCharge()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));

        var response = await manager.SendAsync(
            WithVersion(HttpMethod.Put, ServiceUrl(created.Service.Id), created.Service.Version, new { name = "Hosting Pro", price = 20m, currency = "USD" }),
            Ct);

        var updated = await ReadDetailAsync(response);
        Assert.Equal("Hosting Pro", updated.Service.Name);
        Assert.Equal(20m, updated.Service.Price);
        Assert.Equal("USD", updated.Service.Currency);
        Assert.Equal(2, updated.Service.Version);
        // El cobro ya generado conserva el monto y la moneda de cuando nació; la ficha agrega el nombre nuevo.
        var charge = Assert.Single(updated.Charges);
        Assert.Equal(Price, charge.Amount);
        Assert.Equal("COP", charge.Currency);
        Assert.Equal("Hosting Pro", charge.ServiceName);
        Assert.Equal([new CurrencyAmountDto("COP", Price)], updated.Service.OpenAmountByCurrency);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, BillingAuditActions.ServiceUpdated, created.Service.Id.ToString()));
    }

    [Fact]
    public async Task UpdateWithAnUnknownCurrencyOrInvalidDataAnswers422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));

        var currency = await manager.SendAsync(
            WithVersion(HttpMethod.Put, ServiceUrl(created.Service.Id), 1, new { name = "Hosting", price = 10m, currency = "XYZ" }), Ct);
        var invalid = await manager.SendAsync(
            WithVersion(HttpMethod.Put, ServiceUrl(created.Service.Id), 1, new { name = "", price = -1m, currency = "COP" }), Ct);

        Assert.Equal("billing.currency.unknown", await CodeOfAsync(currency));
        Assert.Equal("validation.failed", await CodeOfAsync(invalid));
        var errors = await ErrorsOfAsync(invalid);
        Assert.True(Has(errors, "Name"));
        Assert.True(Has(errors, "Price"));
    }

    [Fact]
    public async Task UpdateWithoutIfMatchAnswers428AndWithAnOldVersion412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));
        var body = new { name = "Hosting 2", price = 10m, currency = "COP" };

        var missing = await manager.SendAsync(WithVersion(HttpMethod.Put, ServiceUrl(created.Service.Id), null, body), Ct);
        var first = await manager.SendAsync(WithVersion(HttpMethod.Put, ServiceUrl(created.Service.Id), 1, body), Ct);
        var stale = await manager.SendAsync(WithVersion(HttpMethod.Put, ServiceUrl(created.Service.Id), 1, body), Ct);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(missing));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(stale));
    }

    [Fact]
    public async Task EveryTransitionNeedsIfMatch()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));

        foreach (var verb in new[] { "pause", "resume", "cancel" })
        {
            var missing = await TransitionAsync(manager, created.Service.Id, verb, version: null);
            var stale = await TransitionAsync(manager, created.Service.Id, verb, version: 99);

            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        }
    }

    [Fact]
    public async Task PauseResumeCancelFollowTheTransitionTable()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));
        var id = created.Service.Id;

        var resumeActive = await TransitionAsync(manager, id, "resume", 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resumeActive.StatusCode);
        Assert.Equal("billing.service.invalid_transition", await CodeOfAsync(resumeActive));

        var paused = await ReadDetailAsync(await TransitionAsync(manager, id, "pause", 1));
        Assert.Equal("Paused", paused.Service.Status);
        Assert.Equal(2, paused.Service.Version);
        var pauseAgain = await TransitionAsync(manager, id, "pause", 2);
        Assert.Equal("billing.service.invalid_transition", await CodeOfAsync(pauseAgain));

        var resumed = await ReadDetailAsync(await TransitionAsync(manager, id, "resume", 2));
        Assert.Equal("Active", resumed.Service.Status);
        Assert.Equal(3, resumed.Service.Version);

        var cancelled = await ReadDetailAsync(await TransitionAsync(manager, id, "cancel", 3));
        Assert.Equal("Cancelled", cancelled.Service.Status);
        foreach (var verb in new[] { "pause", "resume", "cancel" })
        {
            var response = await TransitionAsync(manager, id, verb, 4);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("billing.service.invalid_transition", await CodeOfAsync(response));
        }

        var edit = await manager.SendAsync(
            WithVersion(HttpMethod.Put, ServiceUrl(id), 4, new { name = "x", price = 1m, currency = "COP" }), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Equal("billing.service.cancelled", await CodeOfAsync(edit));

        foreach (var action in new[] { BillingAuditActions.ServicePaused, BillingAuditActions.ServiceResumed, BillingAuditActions.ServiceCancelled })
        {
            Assert.Equal(1, await CountAuditEntriesAsync(factory, action, id.ToString()));
        }
    }

    [Fact]
    public async Task CancelCancelsEveryPendingCharge()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var created = await CreateAsync(
            manager, ServiceBody(customerId, ProfileBody("Email", admin), firstDueDate: TenantToday.AddMonths(-2)));
        Assert.Equal(3, created.Service.OpenCount);

        var cancelled = await ReadDetailAsync(await TransitionAsync(manager, created.Service.Id, "cancel", 1));

        Assert.Equal(3, cancelled.Charges.Count);
        Assert.All(cancelled.Charges, charge => Assert.Equal("Cancelled", charge.Status));
        Assert.Null(cancelled.Service.NextCharge);
        Assert.Equal(0, cancelled.Service.OpenCount);
        Assert.Empty(cancelled.Service.OpenAmountByCurrency);
    }

    [Fact]
    public async Task PauseKeepsThePendingChargesPending()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var created = await CreateAsync(
            manager, ServiceBody(customerId, ProfileBody("Email", admin), firstDueDate: TenantToday.AddMonths(-2)));

        var paused = await ReadDetailAsync(await TransitionAsync(manager, created.Service.Id, "pause", 1));

        Assert.Equal(created.Charges.Select(charge => (charge.Id, charge.Status, charge.Version)), paused.Charges.Select(charge => (charge.Id, charge.Status, charge.Version)));
        Assert.Equal(3, paused.Service.OpenCount);
        Assert.Equal(created.Service.NextCharge!.Id, paused.Service.NextCharge!.Id);
    }

    [Fact]
    public async Task GetReadsTheDetailBackAndAnUnknownIdAnswers404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        using var reader = CreateReader(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));

        var get = await reader.GetAsync(ServiceUrl(created.Service.Id), Ct);
        var missing = await reader.GetAsync(ServiceUrl(Guid.CreateVersion7()), Ct);

        // Los DTO llevan listas (comparación por referencia en un record) y el alta responde con el decimal del request mientras
        // la lectura lo trae de numeric(18,2): se comparan los campos, no el texto del JSON.
        var read = await ReadDetailAsync(get);
        Assert.Equal(Normalized(created.Service), Normalized(read.Service));
        Assert.Equal(created.Service.OpenAmountByCurrency, read.Service.OpenAmountByCurrency);
        Assert.Equal(created.Service.NextCharge!.Id, read.Service.NextCharge!.Id);
        Assert.Equal(created.Profile, read.Profile);
        Assert.Equal(created.Charges.Select(charge => (charge.Id, charge.Status, charge.Amount)), read.Charges.Select(charge => (charge.Id, charge.Status, charge.Amount)));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("billing.service.not_found", await CodeOfAsync(missing));
    }

    [Fact]
    public async Task AReaderCanReadButNotWrite()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        using var reader = CreateReader(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));

        var list = await reader.GetAsync(ServicesUrl(), Ct);
        var preview = await reader.GetAsync($"{ServicesUrl()}/preview-charges?cycle=Monthly&firstDueDate={Iso(TenantToday)}", Ct);
        var create = await reader.PostAsJsonAsync(ServicesUrl(), ServiceBody(customerId, null), Ct);
        var pause = await TransitionAsync(reader, created.Service.Id, "pause", 1);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, pause.StatusCode);
    }

    [Fact]
    public async Task ListFiltersByCustomerAndStatusAndDefaultsToActive()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (alfa, admin) = await SeedCustomerAndAdminAsync(factory, "Alfa SAS");
        var beta = await SeedCustomerAsync(factory, Tenant, "Beta Ltda", "+573001112233", "beta@example.com");
        using var manager = CreateManager(factory);
        var profile = ProfileBody("Email", admin);
        var hosting = await CreateAsync(manager, ServiceBody(alfa, profile, name: "Hosting"));
        var dominio = await CreateAsync(manager, ServiceBody(alfa, null, name: "Dominio"));
        var soporte = await CreateAsync(manager, ServiceBody(beta, profile, name: "Soporte"));
        await TransitionAsync(manager, dominio.Service.Id, "pause", 1);
        var cancelled = await CreateAsync(manager, ServiceBody(beta, null, name: "Antiguo"));
        await TransitionAsync(manager, cancelled.Service.Id, "cancel", 1);

        async Task<ServicePageDto> ListAsync(string query) =>
            (await manager.GetFromJsonAsync<ServicePageDto>($"{ServicesUrl()}{query}", Ct))!;

        var byDefault = await ListAsync(string.Empty);
        var paused = await ListAsync("?status=paused");
        var cancelledOnly = await ListAsync("?status=Cancelled");
        var all = await ListAsync("?status=all");
        var ofAlfa = await ListAsync($"?status=all&customerId={alfa}");
        var bySearchName = await ListAsync("?status=all&search=sopor");
        var bySearchCustomer = await ListAsync("?status=all&search=alfa");

        Assert.Equal([hosting.Service.Id, soporte.Service.Id], byDefault.Items.Select(item => item.Id));
        Assert.Equal(2, byDefault.Total);
        Assert.Equal(dominio.Service.Id, Assert.Single(paused.Items).Id);
        Assert.Equal(cancelled.Service.Id, Assert.Single(cancelledOnly.Items).Id);
        Assert.Equal(4, all.Total);
        Assert.Equal([dominio.Service.Id, hosting.Service.Id], ofAlfa.Items.Select(item => item.Id));
        Assert.Equal(soporte.Service.Id, Assert.Single(bySearchName.Items).Id);
        Assert.Equal([dominio.Service.Id, hosting.Service.Id], bySearchCustomer.Items.Select(item => item.Id));
        // La lista trae el cliente y el próximo cobro de cada fila sin pedirlos aparte.
        var row = byDefault.Items.Single(item => item.Id == hosting.Service.Id);
        Assert.Equal("Alfa SAS", row.Customer.Name);
        Assert.Equal(1, row.OpenCount);
        Assert.NotNull(row.NextCharge);
        Assert.Equal(admin, row.NextCharge!.Assignee!.MemberId);
    }

    // Spec §4: por customer.name y luego name. El nombre del cliente vive en otro esquema: el orden se hace en memoria y la
    // página tiene que cortar después de ordenar, no antes.
    [Fact]
    public async Task ListIsOrderedByCustomerNameThenServiceNameAcrossPages()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (zeta, admin) = await SeedCustomerAndAdminAsync(factory, "zeta SAS");
        var alfa = await SeedCustomerAsync(factory, Tenant, "alfa SAS", "+573001112233", "alfa@example.com");
        var beta = await SeedCustomerAsync(factory, Tenant, "Beta Ltda", "+573004445566", "beta@example.com");
        using var manager = CreateManager(factory);
        var profile = ProfileBody("Email", admin);
        // Se crean en un orden que no coincide con el esperado ni por nombre de servicio ni por cliente.
        var zetaA = await CreateAsync(manager, ServiceBody(zeta, profile, name: "A zeta"));
        var betaB = await CreateAsync(manager, ServiceBody(beta, profile, name: "B beta"));
        var alfaZ = await CreateAsync(manager, ServiceBody(alfa, profile, name: "Z alfa"));
        var alfaC = await CreateAsync(manager, ServiceBody(alfa, null, name: "C alfa"));
        var betaA = await CreateAsync(manager, ServiceBody(beta, null, name: "A beta"));

        var first = await manager.GetFromJsonAsync<ServicePageDto>($"{ServicesUrl()}?pageSize=2&page=1", Ct);
        var second = await manager.GetFromJsonAsync<ServicePageDto>($"{ServicesUrl()}?pageSize=2&page=2", Ct);
        var third = await manager.GetFromJsonAsync<ServicePageDto>($"{ServicesUrl()}?pageSize=2&page=3", Ct);
        var beyond = await manager.GetFromJsonAsync<ServicePageDto>($"{ServicesUrl()}?pageSize=2&page=4", Ct);

        Assert.Equal([alfaC.Service.Id, alfaZ.Service.Id], first!.Items.Select(item => item.Id));
        Assert.Equal([betaA.Service.Id, betaB.Service.Id], second!.Items.Select(item => item.Id));
        Assert.Equal([zetaA.Service.Id], third!.Items.Select(item => item.Id));
        Assert.Empty(beyond!.Items);
        Assert.All(new[] { first, second, third, beyond }, page => Assert.Equal(5, page.Total));
        Assert.Equal((1, 2), (first.Page, first.PageSize));
    }

    [Theory]
    [InlineData("status=weekly")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("page=0")]
    public async Task ListWithABadFilterAnswers422(string query)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync($"{ServicesUrl()}?{query}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task GetOfAnotherTenantsServiceAnswers403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, BillingApiHarness.OtherTenantId);
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));
        // Quien opera en el otro tenant y apunta a la ruta de éste: 403, nunca 404.
        using var outsider = CreateClient(
            factory, BillingApiHarness.OtherSubjectId, BillingApiHarness.OtherTenantId, BillingPermissions.Read, BillingPermissions.Manage);

        var get = await outsider.GetAsync(ServiceUrl(created.Service.Id), Ct);
        var list = await outsider.GetAsync(ServicesUrl(), Ct);
        var preview = await outsider.GetAsync($"{ServicesUrl()}/preview-charges?cycle=Monthly&firstDueDate={Iso(TenantToday)}", Ct);
        var create = await outsider.PostAsJsonAsync(ServicesUrl(), ServiceBody(customerId, null), Ct);
        var pause = await TransitionAsync(outsider, created.Service.Id, "pause", 1);

        foreach (var response in new[] { get, list, preview, create, pause })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("authorization.denied", await CodeOfAsync(response));
        }
    }

    // Los métodos de los repositorios de cobros no llevan tenant: cada handler carga el servicio acotado al tenant primero.
    [Fact]
    public async Task AServiceOfAnotherTenantIsNotFoundThroughTheOwnRoute()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var foreignTenant = Guid.Parse(BillingApiHarness.OtherTenantId);
        var foreign = BillingService.Create(
            BillingServiceId.New(), foreignTenant, Guid.CreateVersion7(), "Ajeno", 10m, "COP", BillingCycle.Monthly, TenantToday.AddMonths(-1), DateTimeOffset.UtcNow);
        var foreignCharge = Charge.Generate(foreign, TenantToday.AddMonths(-1), DateTimeOffset.UtcNow);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            db.Services.Add(foreign);
            db.Charges.Add(foreignCharge);
            await db.SaveChangesAsync(Ct);
        }

        using var manager = CreateManager(factory);
        var id = foreign.Id.Value;
        var responses = new[]
        {
            await manager.GetAsync(ServiceUrl(id), Ct),
            await manager.SendAsync(WithVersion(HttpMethod.Put, ServiceUrl(id), 1, new { name = "x", price = 1m, currency = "COP" }), Ct),
            await TransitionAsync(manager, id, "pause", 1),
            await TransitionAsync(manager, id, "cancel", 1),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("billing.service.not_found", await CodeOfAsync(response));
        }

        // La cancelación que no pasó no tocó el cobro ajeno.
        await using var read = factory.Services.CreateAsyncScope();
        var untouched = await new ChargeRepository(read.ServiceProvider.GetRequiredService<BillingDbContext>())
            .FindAsync(foreignTenant, foreignCharge.Id, Ct);
        Assert.Equal(ChargeStatus.Pending, untouched!.Status);
    }

    [Fact]
    public async Task WithTheModuleOffAnswers403ModuleNotEnabled()
    {
        var (database, factory) = await OpenAsync(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Billing));
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);

        var list = await manager.GetAsync(ServicesUrl(), Ct);
        var preview = await manager.GetAsync($"{ServicesUrl()}/preview-charges?cycle=Monthly&firstDueDate={Iso(TenantToday)}", Ct);

        foreach (var response in new[] { list, preview })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("tenancy.module_not_enabled", await CodeOfAsync(response));
        }
    }

    [Fact]
    public async Task PreviewChargesCountsWhatCreateWouldGenerateWithoutWriting()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateReader(factory);

        var past = await reader.GetFromJsonAsync<ChargesPreviewDto>(
            $"{ServicesUrl()}/preview-charges?cycle=Monthly&firstDueDate={Iso(TenantToday.AddMonths(-3))}", Ct);
        var future = await reader.GetFromJsonAsync<ChargesPreviewDto>(
            $"{ServicesUrl()}/preview-charges?cycle=Annual&firstDueDate={Iso(TenantToday.AddMonths(2))}", Ct);

        Assert.Equal(new ChargesPreviewDto(4, 3), past);
        Assert.Equal(new ChargesPreviewDto(1, 0), future);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        Assert.Empty(db.Services);
        Assert.Empty(db.Charges);
    }

    [Theory]
    [InlineData("cycle=Weekly&firstDueDate=2026-01-10", "Cycle")]
    [InlineData("firstDueDate=2026-01-10", "Cycle")]
    [InlineData("cycle=Monthly&firstDueDate=banana", "FirstDueDate")]
    [InlineData("cycle=Monthly", "FirstDueDate")]
    [InlineData("cycle=Monthly&firstDueDate=1990-01-01", "FirstDueDate")]
    public async Task PreviewChargesWithABadCycleOrDateAnswers422(string query, string field)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync($"{ServicesUrl()}/preview-charges?{query}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        Assert.True(Has((await ErrorsOfAsync(response)), field));
    }
}
