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
/// Los cobros (spec cobros-recurrentes §3.2 y §4): la bandeja «Por cobrar», el detalle, marcar pagado y corregir el pago. De punta
/// a punta por HTTP: política, tenant de la ruta, <c>Overdue</c> calculado con la «hoy» del tenant, un total por moneda y auditoría
/// atómica. Los cobros nacen creando servicios por la API, como los crea la pantalla.
/// </summary>
public sealed class BillingChargeApiTests
{
    private const decimal Price = 100000m;

    private static readonly Guid Tenant = Guid.Parse(BillingApiHarness.TenantId);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Los tenants de las pruebas viven en America/Bogota (UTC-5, sin horario de verano): la «hoy» del tenant.
    private static DateOnly TenantToday => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

    /// <summary>
    /// <c>monthsBack</c> meses antes de hoy con el día recortado a 28: <c>AddMonths</c> sobre el 29 al 31 recorta al último día del
    /// mes más corto y la prueba dependería de la fecha en que corre. Genera <c>monthsBack + 1</c> períodos.
    /// </summary>
    private static DateOnly PastAnchor(int monthsBack) =>
        new DateOnly(TenantToday.Year, TenantToday.Month, Math.Min(TenantToday.Day, 28)).AddMonths(-monthsBack);

    /// <summary>Cuántos de los <c>monthsBack + 1</c> períodos de <see cref="PastAnchor"/> están vencidos: el último vence hoy salvo del 29 al 31.</summary>
    private static int OverdueOf(int monthsBack) => TenantToday.Day <= 28 ? monthsBack : monthsBack + 1;

    private static string ChargesUrl(string tenantId = BillingApiHarness.TenantId) => $"{BillingUrl(tenantId)}/charges";

    private static string ChargeUrl(Guid chargeId, string tenantId = BillingApiHarness.TenantId) => $"{ChargesUrl(tenantId)}/{chargeId}";

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool Has(JsonElement errors, string field) => errors.TryGetProperty(field, out _);

    private static object ServiceBody(
        Guid customerId, object? profile, string name = "Hosting", decimal price = Price, string? currency = null, DateOnly? firstDueDate = null) => new
        {
            customerId,
            name,
            price,
            currency,
            cycle = "Monthly",
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

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await BodyAsync(response));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private static async Task<ServiceDetailDto> CreateAsync(HttpClient client, object body) =>
        await ReadAsync<ServiceDetailDto>(await client.PostAsJsonAsync($"{BillingUrl()}/services", body, Ct));

    private static Task<HttpResponseMessage> PayAsync(HttpClient client, Guid chargeId, DateOnly paidOn, string? note = null) =>
        client.PostAsJsonAsync($"{ChargeUrl(chargeId)}/pay", new { paidOn = Iso(paidOn), note }, Ct);

    private static Task<HttpResponseMessage> CorrectAsync(HttpClient client, Guid chargeId, DateOnly paidOn, string? note = null) =>
        client.PutAsJsonAsync($"{ChargeUrl(chargeId)}/payment", new { paidOn = Iso(paidOn), note }, Ct);

    private static Task<ChargePageDto> ListAsync(HttpClient client, string query = "") =>
        client.GetFromJsonAsync<ChargePageDto>($"{ChargesUrl()}{query}", Ct)!;

    private static async Task<(Guid CustomerId, Guid Admin)> SeedCustomerAndAdminAsync(
        QepApiFactory factory, string customerName = "Panadería La Esquina")
    {
        var customerId = await SeedCustomerAsync(factory, Tenant, customerName, "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", "Ana Admin", "admin");
        return (customerId, admin);
    }

    [Fact]
    public async Task PayMarksPaidRecordsWhoAndGeneratesNothing()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        var caller = await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin), firstDueDate: PastAnchor(1)));
        var target = created.Charges.OrderBy(charge => charge.DueDate).First();
        var paidOn = TenantToday.AddDays(-1);

        var response = await PayAsync(manager, target.Id, paidOn, "  Transferencia Bancolombia  ");

        var paid = await ReadAsync<ChargeDto>(response);
        Assert.Equal("Paid", paid.Status);
        Assert.Equal(0, paid.DaysOverdue);
        Assert.Equal(2, paid.Version);
        Assert.Equal(paidOn, paid.Payment!.PaidOn);
        Assert.Equal("Transferencia Bancolombia", paid.Payment.Note);
        Assert.Equal(caller, paid.Payment.PaidBy!.MemberId);
        Assert.Equal("Caro Cajera", paid.Payment.PaidBy.DisplayName);
        Assert.Equal("Panadería La Esquina", paid.Customer.Name);
        Assert.Equal(admin, paid.Assignee!.MemberId);
        // Marcar pagado no genera nada (owner, 2026-10-29): el servicio sigue con los mismos cobros.
        var detail = await ReadAsync<ServiceDetailDto>(await manager.GetAsync($"{BillingUrl()}/services/{created.Service.Id}", Ct));
        Assert.Equal(created.Charges.Count, detail.Charges.Count);
        Assert.Equal(1, detail.Charges.Count(charge => charge.Status == "Paid"));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, BillingAuditActions.ChargePaid, target.Id.ToString()));
    }

    [Fact]
    public async Task PayWithAnEmptyNoteStoresNull()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));

        var paid = await ReadAsync<ChargeDto>(await PayAsync(manager, created.Charges[0].Id, TenantToday, "   "));

        Assert.Null(paid.Payment!.Note);
        Assert.Equal(TenantToday, paid.Payment.PaidOn);
    }

    [Fact]
    public async Task PayingAnAlreadyPaidChargeAnswers422NotPending()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));
        var id = created.Charges[0].Id;
        Assert.Equal(HttpStatusCode.OK, (await PayAsync(manager, id, TenantToday)).StatusCode);

        var again = await PayAsync(manager, id, TenantToday);

        // Sin If-Match (spec §4): pagar dos veces es 422, no 412.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("billing.charge.not_pending", await CodeOfAsync(again));
    }

    // F3: cancelar el servicio cancela todos sus Pending, así que «cobro Pending de un servicio Cancelled» no existe; lo alcanzable
    // es pagar un cobro ya Cancelled, que no es deuda.
    [Fact]
    public async Task PayingACancelledChargeAnswers422NotPending()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));
        var cancel = new HttpRequestMessage(HttpMethod.Post, $"{BillingUrl()}/services/{created.Service.Id}/cancel");
        cancel.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await manager.SendAsync(cancel, Ct)).StatusCode);

        var response = await PayAsync(manager, created.Charges[0].Id, TenantToday);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.charge.not_pending", await CodeOfAsync(response));
    }

    [Fact]
    public async Task PayingAChargeOfAPausedServiceIsAllowed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin), firstDueDate: PastAnchor(1)));
        var pause = new HttpRequestMessage(HttpMethod.Post, $"{BillingUrl()}/services/{created.Service.Id}/pause");
        pause.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await manager.SendAsync(pause, Ct)).StatusCode);

        // La deuda existe aunque el servicio esté en pausa.
        var paid = await ReadAsync<ChargeDto>(await PayAsync(manager, created.Charges[^1].Id, TenantToday));

        Assert.Equal("Paid", paid.Status);
    }

    [Fact]
    public async Task PayWithAFutureDateAnswers422DateInFuture()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));

        var response = await PayAsync(manager, created.Charges[0].Id, TenantToday.AddDays(1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("billing.payment.date_in_future", await CodeOfAsync(response));
        // Nada quedó a medias.
        var read = await ReadAsync<ChargeDto>(await manager.GetAsync(ChargeUrl(created.Charges[0].Id), Ct));
        Assert.Equal("Pending", read.Status);
    }

    [Fact]
    public async Task PayWithA1001CharNoteOrWithoutADateAnswers422ValidationFailed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));
        var id = created.Charges[0].Id;

        var tooLong = await PayAsync(manager, id, TenantToday, new string('x', 1001));
        var noDate = await manager.PostAsJsonAsync($"{ChargeUrl(id)}/pay", new { note = "sin fecha" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(tooLong));
        Assert.True(Has(await ErrorsOfAsync(tooLong), "Note"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noDate.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(noDate));
        Assert.True(Has(await ErrorsOfAsync(noDate), "PaidOn"));
        // 1000 exactos pasan.
        var limit = await PayAsync(manager, id, TenantToday, new string('x', 1000));
        Assert.Equal(HttpStatusCode.OK, limit.StatusCode);
    }

    [Fact]
    public async Task PayWithoutAnActiveMembershipAnswers403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));

        var response = await PayAsync(manager, created.Charges[0].Id, TenantToday);

        // El cobro guarda quién lo marcó: sin membresía no hay a quién atribuirlo.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
    }

    [Fact]
    public async Task UpdatePaymentChangesDateAndNoteOnlyOnAPaidCharge()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        var caller = await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin), firstDueDate: PastAnchor(1)));
        var target = created.Charges.OrderBy(charge => charge.DueDate).First();

        var pending = await CorrectAsync(manager, target.Id, TenantToday);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, pending.StatusCode);
        Assert.Equal("billing.charge.not_paid", await CodeOfAsync(pending));

        var paid = await ReadAsync<ChargeDto>(await PayAsync(manager, target.Id, TenantToday.AddDays(-2), "primera"));
        var corrected = await ReadAsync<ChargeDto>(await CorrectAsync(manager, target.Id, TenantToday.AddDays(-5), "corregida"));

        Assert.Equal("Paid", corrected.Status);
        Assert.Equal(TenantToday.AddDays(-5), corrected.Payment!.PaidOn);
        Assert.Equal("corregida", corrected.Payment.Note);
        Assert.Equal(paid.Version + 1, corrected.Version);
        // Quién y cuándo se registró no cambian; tampoco el monto ni el vencimiento.
        Assert.Equal(caller, corrected.Payment.PaidBy!.MemberId);
        Assert.Equal(paid.Payment!.PaidAt, corrected.Payment.PaidAt);
        Assert.Equal(target.Amount, corrected.Amount);
        Assert.Equal(target.DueDate, corrected.DueDate);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, BillingAuditActions.ChargePaymentUpdated, target.Id.ToString()));

        var future = await CorrectAsync(manager, target.Id, TenantToday.AddDays(1));
        var tooLong = await CorrectAsync(manager, target.Id, TenantToday, new string('x', 1001));
        Assert.Equal("billing.payment.date_in_future", await CodeOfAsync(future));
        Assert.Equal("validation.failed", await CodeOfAsync(tooLong));
        Assert.True(Has(await ErrorsOfAsync(tooLong), "Note"));
    }

    [Fact]
    public async Task OpenListIsSortedByDueDateAndMarksOverdueWithTodayOfTheTenant()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (alfa, admin) = await SeedCustomerAndAdminAsync(factory, "Alfa SAS");
        var beta = await SeedCustomerAsync(factory, Tenant, "Beta Ltda", "+573001112233", "beta@example.com");
        using var manager = CreateManager(factory);
        var profile = ProfileBody("Email", admin);
        await CreateAsync(manager, ServiceBody(alfa, profile, name: "Hosting", firstDueDate: PastAnchor(2)));
        await CreateAsync(manager, ServiceBody(beta, profile, name: "Dominio", firstDueDate: TenantToday.AddMonths(2)));
        await CreateAsync(manager, ServiceBody(alfa, null, name: "Soporte", firstDueDate: PastAnchor(1)));

        var page = await ListAsync(manager, "?status=open");

        // 3 + 1 + 2 cobros abiertos, por vencimiento ascendente (el Id desempata).
        Assert.Equal(6, page.Total);
        Assert.Equal((1, 25), (page.Page, page.PageSize));
        Assert.Equal(page.Items.OrderBy(item => item.DueDate).Select(item => item.DueDate), page.Items.Select(item => item.DueDate));
        foreach (var item in page.Items)
        {
            var overdue = item.DueDate < TenantToday;
            Assert.Equal(overdue ? "Overdue" : "Pending", item.Status);
            Assert.Equal(overdue ? TenantToday.DayNumber - item.DueDate.DayNumber : 0, item.DaysOverdue);
        }

        Assert.Equal(OverdueOf(2) + OverdueOf(1), page.Items.Count(item => item.Status == "Overdue"));
        var first = page.Items[0];
        Assert.Equal("Alfa SAS", first.Customer.Name);
        Assert.Equal("Hosting", first.ServiceName);
        Assert.Equal(admin, first.Assignee!.MemberId);
        // «open» es el predeterminado.
        Assert.Equal(6, (await ListAsync(manager)).Total);
    }

    [Fact]
    public async Task PaidListIsSortedByPaidOnDescendingAndCancelledListHoldsTheCancelled()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        var profile = ProfileBody("Email", admin);
        var hosting = await CreateAsync(manager, ServiceBody(customerId, profile, name: "Hosting", firstDueDate: PastAnchor(2)));
        var old = await CreateAsync(manager, ServiceBody(customerId, null, name: "Antiguo"));
        var byDue = hosting.Charges.OrderBy(charge => charge.DueDate).ToList();
        await PayAsync(manager, byDue[0].Id, TenantToday.AddDays(-9));
        await PayAsync(manager, byDue[1].Id, TenantToday.AddDays(-1));
        await PayAsync(manager, byDue[2].Id, TenantToday.AddDays(-4));
        var cancel = new HttpRequestMessage(HttpMethod.Post, $"{BillingUrl()}/services/{old.Service.Id}/cancel");
        cancel.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        await manager.SendAsync(cancel, Ct);

        var paid = await ListAsync(manager, "?status=paid");
        var cancelled = await ListAsync(manager, "?status=Cancelled");

        Assert.Equal([byDue[1].Id, byDue[2].Id, byDue[0].Id], paid.Items.Select(item => item.Id));
        Assert.All(paid.Items, item => Assert.Equal("Paid", item.Status));
        Assert.Equal(old.Charges[0].Id, Assert.Single(cancelled.Items).Id);
        Assert.Equal("Cancelled", cancelled.Items[0].Status);
        // La deuda abierta ya no incluye ninguno de los dos.
        Assert.Equal(0, (await ListAsync(manager, "?status=open")).Total);
    }

    [Fact]
    public async Task SummaryIsTheWholeTenantsDebtRegardlessOfFilters()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (alfa, admin) = await SeedCustomerAndAdminAsync(factory, "Alfa SAS");
        var beta = await SeedCustomerAsync(factory, Tenant, "Beta Ltda", "+573001112233", "beta@example.com");
        await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        var cop = await CreateAsync(manager, ServiceBody(alfa, ProfileBody("Email", admin), name: "Hosting", price: Price, firstDueDate: PastAnchor(1)));
        await CreateAsync(manager, ServiceBody(beta, ProfileBody("Email", admin), name: "Dominio", price: 20m, currency: "USD", firstDueDate: PastAnchor(1)));
        var overdue = OverdueOf(1);

        var unfiltered = (await ListAsync(manager, "?status=open")).Summary;
        // Con filtros que dejan la página vacía el resumen sigue siendo el del tenant entero.
        var filtered = await ListAsync(manager, $"?status=paid&customerId={alfa}&search=zzz");

        Assert.Empty(filtered.Items);
        // Los records con listas se comparan por referencia: se comparan serializados.
        Assert.Equal(JsonSerializer.Serialize(unfiltered), JsonSerializer.Serialize(filtered.Summary));
        Assert.Equal(4, unfiltered.OpenCount);
        Assert.Equal(2 * overdue, unfiltered.OverdueCount);
        Assert.Equal(0, unfiltered.FailedReminderCount);
        // Un total por moneda, sin convertir ni sumar entre monedas.
        Assert.Equal([new CurrencyAmountDto("COP", 2 * Price), new CurrencyAmountDto("USD", 40m)], unfiltered.OpenAmountByCurrency);
        Assert.Equal(
            [new CurrencyAmountDto("COP", overdue * Price), new CurrencyAmountDto("USD", overdue * 20m)],
            unfiltered.OverdueAmountByCurrency);

        // Pagar baja la deuda abierta.
        await PayAsync(manager, cop.Charges[0].Id, TenantToday);
        var afterPay = (await ListAsync(manager)).Summary;
        Assert.Equal(3, afterPay.OpenCount);
        Assert.Equal([new CurrencyAmountDto("COP", Price), new CurrencyAmountDto("USD", 40m)], afterPay.OpenAmountByCurrency);
    }

    [Fact]
    public async Task AssigneeMeReturnsOnlyTheCallersCustomers()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (alfa, other) = await SeedCustomerAndAdminAsync(factory, "Alfa SAS");
        var beta = await SeedCustomerAsync(factory, Tenant, "Beta Ltda", "+573001112233", "beta@example.com");
        var caller = await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        await CreateAsync(manager, ServiceBody(alfa, ProfileBody("Email", other), name: "Hosting"));
        var mine = await CreateAsync(manager, ServiceBody(beta, ProfileBody("Email", caller), name: "Dominio"));

        var page = await ListAsync(manager, "?assignee=me");
        var all = await ListAsync(manager);

        Assert.Equal(mine.Charges[0].Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.Total);
        Assert.Equal(2, all.Total);
        // El resumen no se filtra por encargado: es la deuda del tenant.
        Assert.Equal(2, page.Summary.OpenCount);
    }

    [Fact]
    public async Task AssigneeMeWithoutMembershipAnswersEmpty()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));

        var response = await manager.GetAsync($"{ChargesUrl()}?assignee=me", Ct);

        // Quien no tiene membresía activa no es encargado de nadie: lista vacía, no 403 ni todo el tenant.
        var page = await ReadAsync<ChargePageDto>(response);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.Equal(1, page.Summary.OpenCount);
    }

    [Fact]
    public async Task SearchMatchesCustomerNameAndServiceName()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (alfa, admin) = await SeedCustomerAndAdminAsync(factory, "Alfa SAS");
        var beta = await SeedCustomerAsync(factory, Tenant, "Beta Ltda", "+573001112233", "beta@example.com");
        using var manager = CreateManager(factory);
        var profile = ProfileBody("Email", admin);
        var hosting = await CreateAsync(manager, ServiceBody(alfa, profile, name: "Hosting"));
        var soporte = await CreateAsync(manager, ServiceBody(beta, profile, name: "Soporte técnico"));

        var byCustomer = await ListAsync(manager, "?search=alfa");
        var byService = await ListAsync(manager, "?search=soporte");
        var byCustomerId = await ListAsync(manager, $"?customerId={beta}");
        var none = await ListAsync(manager, "?search=inexistente");

        Assert.Equal(hosting.Charges[0].Id, Assert.Single(byCustomer.Items).Id);
        Assert.Equal(soporte.Charges[0].Id, Assert.Single(byService.Items).Id);
        Assert.Equal(soporte.Charges[0].Id, Assert.Single(byCustomerId.Items).Id);
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task PagingCutsTheOpenListAndReportsTheTotal()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin), firstDueDate: PastAnchor(3)));

        var first = await ListAsync(manager, "?pageSize=3&page=1");
        var second = await ListAsync(manager, "?pageSize=3&page=2");

        Assert.Equal(4, first.Total);
        Assert.Equal(3, first.Items.Count);
        Assert.Single(second.Items);
        Assert.Equal((2, 3), (second.Page, second.PageSize));
        Assert.DoesNotContain(second.Items[0].Id, first.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("page=0")]
    [InlineData("status=weekly")]
    public async Task ListWithABadFilterAnswers422(string query)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync($"{ChargesUrl()}?{query}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task PageSize100IsAccepted()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync($"{ChargesUrl()}?pageSize=100", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetReadsTheChargeBackAndAnUnknownIdAnswers404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        using var reader = CreateReader(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin), firstDueDate: PastAnchor(1)));
        var overdue = created.Charges.OrderBy(charge => charge.DueDate).First();

        var read = await ReadAsync<ChargeDto>(await reader.GetAsync(ChargeUrl(overdue.Id), Ct));
        var missing = await reader.GetAsync(ChargeUrl(Guid.CreateVersion7()), Ct);

        Assert.Equal(JsonSerializer.Serialize(overdue), JsonSerializer.Serialize(read));
        Assert.Equal("Overdue", read.Status);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("billing.charge.not_found", await CodeOfAsync(missing));
    }

    [Fact]
    public async Task AReaderCanReadButNotPayOrCorrect()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        await SeedCallerMembershipAsync(factory, "Caro Cajera");
        using var manager = CreateManager(factory);
        using var reader = CreateReader(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));
        var id = created.Charges[0].Id;

        var list = await reader.GetAsync(ChargesUrl(), Ct);
        var get = await reader.GetAsync(ChargeUrl(id), Ct);
        var pay = await PayAsync(reader, id, TenantToday);
        var correct = await CorrectAsync(reader, id, TenantToday);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, pay.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, correct.StatusCode);
    }

    [Fact]
    public async Task ChargesOfAnotherTenantAreForbiddenOnTheRouteOfThisOne()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, BillingApiHarness.OtherTenantId);
        var (customerId, admin) = await SeedCustomerAndAdminAsync(factory);
        using var manager = CreateManager(factory);
        var created = await CreateAsync(manager, ServiceBody(customerId, ProfileBody("Email", admin)));
        var id = created.Charges[0].Id;
        using var outsider = CreateClient(
            factory, BillingApiHarness.OtherSubjectId, BillingApiHarness.OtherTenantId, BillingPermissions.Read, BillingPermissions.Manage);

        var responses = new[]
        {
            await outsider.GetAsync(ChargesUrl(), Ct),
            await outsider.GetAsync(ChargeUrl(id), Ct),
            await PayAsync(outsider, id, TenantToday),
            await CorrectAsync(outsider, id, TenantToday),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("authorization.denied", await CodeOfAsync(response));
        }
    }

    // Los métodos de los repositorios de cobros no filtran por el servicio: cada handler lo carga acotado al tenant primero.
    [Fact]
    public async Task AChargeOfAnotherTenantIsNotFoundThroughTheOwnRoute()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedCallerMembershipAsync(factory, "Caro Cajera");
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
        var responses = new[]
        {
            await manager.GetAsync(ChargeUrl(foreignCharge.Id), Ct),
            await PayAsync(manager, foreignCharge.Id, TenantToday),
            await CorrectAsync(manager, foreignCharge.Id, TenantToday),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("billing.charge.not_found", await CodeOfAsync(response));
        }

        Assert.Empty((await ListAsync(manager)).Items);
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

        var list = await manager.GetAsync(ChargesUrl(), Ct);
        var pay = await PayAsync(manager, Guid.CreateVersion7(), TenantToday);

        foreach (var response in new[] { list, pay })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("tenancy.module_not_enabled", await CodeOfAsync(response));
        }
    }
}
