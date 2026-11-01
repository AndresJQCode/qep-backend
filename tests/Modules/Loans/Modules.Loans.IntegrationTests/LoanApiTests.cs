using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Loans.Application;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;
using static Modules.Loans.IntegrationTests.LoansApiHarness;

namespace Modules.Loans.IntegrationTests;

/// <summary>
/// Alta, detalle, lista, edición y borrado de préstamos (spec 2026-10-31), de punta a punta por HTTP. El reloj del tenant queda
/// fijo en 2026-10-20 09:00 de Bogotá: «hoy» es el 20 de octubre y septiembre ya cerró.
/// </summary>
public sealed class LoanApiTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    // En UTC (14:00): Npgsql sólo escribe instantes con offset 0, y el reloj del tenant lo convierte a Bogotá.
    private static readonly DateTimeOffset NineLocal = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(-5)).ToUniversalTime();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAtNineAsync()
    {
        var database = await StartDatabaseAsync();
        var clock = new MutableTenantClock(NineLocal);
        var factory = new QepApiFactory(database.GetConnectionString(), services =>
        {
            services.RemoveAll<ITenantClock>();
            services.AddSingleton<ITenantClock>(clock);
        });
        await SeedTenantAsync(factory);
        return (database, factory);
    }

    private static Dictionary<string, object?> LoanBody(
        Guid customerId,
        string name = "Novasteel",
        decimal principal = 100_000_000m,
        string? currency = null,
        decimal rate = 1.5m,
        string disbursedOn = "2026-10-01",
        string cutoffRule = "EndOfMonth",
        Guid? assignee = null,
        string? note = null) => new()
    {
        ["customerId"] = customerId,
        ["name"] = name,
        ["principalAmount"] = principal,
        ["currency"] = currency,
        ["monthlyRatePercent"] = rate,
        ["disbursedOn"] = disbursedOn,
        ["cutoffRule"] = cutoffRule,
        ["assigneeMemberId"] = assignee,
        ["note"] = note,
    };

    private static async Task<JsonElement> JsonOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.Clone();
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        var root = await JsonOfAsync(response);
        return root.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, Dictionary<string, object?> body)
    {
        var response = await client.PostAsJsonAsync(LoansUrl(), body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonOfAsync(response);
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid loanId, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{LoansUrl()}/{loanId}")
        {
            Content = JsonContent.Create(body),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    private static string IfMatchOf(JsonElement detail) => $"\"{detail.GetProperty("loan").GetProperty("version").GetInt64()}\"";

    private static Guid LoanIdOf(JsonElement detail) => detail.GetProperty("loan").GetProperty("id").GetGuid();

    [Fact]
    public async Task CreateRespondsWithTheDetailAndTheOpenPeriod()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS", "+573001112233", "pagos@novasteel.test");

        var response = await manager.PostAsJsonAsync(LoansUrl(), LoanBody(customerId, note: "Capital de trabajo"), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await JsonOfAsync(response);
        var loan = detail.GetProperty("loan");
        Assert.Equal($"{LoansUrl()}/{LoanIdOf(detail)}", response.Headers.Location?.OriginalString);
        Assert.Equal(100_000_000m, loan.GetProperty("principalAmount").GetDecimal());
        Assert.Equal(100_000_000m, loan.GetProperty("outstandingPrincipal").GetDecimal());
        Assert.Equal("Active", loan.GetProperty("status").GetString());
        Assert.Equal("InterestOnly", loan.GetProperty("type").GetString());
        Assert.Equal("Novasteel SAS", loan.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal("Capital de trabajo", loan.GetProperty("note").GetString());
        Assert.Equal(0, loan.GetProperty("pendingCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, loan.GetProperty("lastGeneratedPeriodEnd").ValueKind);
        Assert.Equal("2026-10-31", loan.GetProperty("nextCutoffDate").GetString());
        Assert.Equal("2026-10-01", detail.GetProperty("currentPeriod").GetProperty("start").GetString());
        Assert.Equal("2026-10-31", detail.GetProperty("currentPeriod").GetProperty("end").GetString());
        Assert.Empty(detail.GetProperty("installments").EnumerateArray());
        Assert.Empty(detail.GetProperty("capitalPayments").EnumerateArray());
        Assert.True(detail.GetProperty("canEditTerms").GetBoolean());
        Assert.True(detail.GetProperty("canDelete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("capitalPaymentBlock").ValueKind);
        Assert.Equal("2026-10-01", detail.GetProperty("capitalPaymentWindow").GetProperty("from").GetString());
        Assert.Equal("2026-10-20", detail.GetProperty("capitalPaymentWindow").GetProperty("to").GetString());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, LoansAuditActions.LoanCreated, LoanIdOf(detail).ToString()));
    }

    [Fact]
    public async Task ALoanWithoutCurrencyUsesTheTenantCurrency()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");

        var defaulted = await CreateAsync(manager, LoanBody(customerId));
        var explicitUsd = await CreateAsync(manager, LoanBody(customerId, currency: " usd "));
        var unknown = await manager.PostAsJsonAsync(LoansUrl(), LoanBody(customerId, currency: "XXX"), Ct);

        Assert.Equal("COP", defaulted.GetProperty("loan").GetProperty("currency").GetString());
        Assert.Equal("USD", explicitUsd.GetProperty("loan").GetProperty("currency").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("loans.currency.unknown", await CodeOfAsync(unknown));
    }

    [Fact]
    public async Task CreatingWithAPastDisbursementGeneratesEveryClosedPeriod()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");

        var detail = await CreateAsync(manager, LoanBody(customerId, disbursedOn: "2026-07-15"));

        var installments = detail.GetProperty("installments").EnumerateArray().ToList();
        // Del período más reciente al más viejo: septiembre, agosto y el julio parcial.
        Assert.Equal(["2026-09-01", "2026-08-01", "2026-07-15"], installments.Select(item => item.GetProperty("periodStart").GetString()!).ToArray());
        Assert.Equal(1_500_000m, installments[1].GetProperty("amount").GetDecimal());
        Assert.All(installments, item => Assert.Equal("Pending", item.GetProperty("status").GetString()));
        Assert.Equal(3, detail.GetProperty("loan").GetProperty("pendingCount").GetInt32());
        Assert.Equal("2026-09-30", detail.GetProperty("loan").GetProperty("lastGeneratedPeriodEnd").GetString());
        Assert.Equal("2026-10-01", detail.GetProperty("currentPeriod").GetProperty("start").GetString());
        Assert.False(detail.GetProperty("canEditTerms").GetBoolean());
        Assert.True(detail.GetProperty("canDelete").GetBoolean());
        Assert.Equal("PendingInstallments", detail.GetProperty("capitalPaymentBlock").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("capitalPaymentWindow").ValueKind);
    }

    [Fact]
    public async Task AnInactiveCustomerIsRejected()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        await DeactivateCustomerAsync(factory, customerId);

        var response = await manager.PostAsJsonAsync(LoansUrl(), LoanBody(customerId), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.customer.inactive", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ACustomerOfAnotherTenantIsNotFound()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, OtherTenantId);
        using var manager = CreateManager(factory);
        var foreign = await SeedCustomerAsync(factory, Guid.Parse(OtherTenantId), "Ajeno SAS");

        var response = await manager.PostAsJsonAsync(LoansUrl(), LoanBody(foreign), Ct);

        // 422 y no 404: el cliente es un campo del cuerpo, no el recurso de la ruta.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.customer.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnAssigneeWhoCannotManageIsRejected()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var advisor = await SeedMemberAsync(factory, "caro@example.com", "Caro Asesora", "advisor");
        var removed = await SeedMemberAsync(factory, "rita@example.com", "Rita Quitada", "admin", removed: true);
        var admin = await SeedMemberAsync(factory, "zoe@example.com", "Zoe Admin", "admin");

        foreach (var invalid in new[] { advisor, removed, Guid.NewGuid() })
        {
            var rejected = await manager.PostAsJsonAsync(LoansUrl(), LoanBody(customerId, assignee: invalid), Ct);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Equal("loans.assignee.invalid", await CodeOfAsync(rejected));
        }

        var accepted = await CreateAsync(manager, LoanBody(customerId, assignee: admin));
        Assert.Equal(admin, accepted.GetProperty("loan").GetProperty("assignee").GetProperty("memberId").GetGuid());
        Assert.Equal("Zoe Admin", accepted.GetProperty("loan").GetProperty("assignee").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task AFutureDisbursementIsRejected()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");

        var response = await manager.PostAsJsonAsync(LoansUrl(), LoanBody(customerId, disbursedOn: "2026-10-21"), Ct);
        var missing = LoanBody(customerId);
        missing.Remove("name");
        var withoutName = await manager.PostAsJsonAsync(LoansUrl(), missing, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.loan.disbursed_in_future", await CodeOfAsync(response));
        // Un campo ausente es validation.failed con el campo, no un 400 del binding.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutName.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(withoutName));
        Assert.Contains("Name", await BodyAsync(withoutName));
    }

    [Fact]
    public async Task ListSeparatesActiveAndSettledAndSummarizesTheWholeTenantByCurrency()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var cop = await CreateAsync(manager, LoanBody(customerId, name: "COP activo"));
        // 100 USD al mes: agosto y septiembre quedan pendientes (200 USD).
        await CreateAsync(manager, LoanBody(customerId, name: "USD activo", principal: 5_000m, currency: "USD", rate: 2m, disbursedOn: "2026-08-01"));
        var settled = await CreateAsync(manager, LoanBody(customerId, name: "Saldado", principal: 1_000_000m, disbursedOn: "2026-10-05"));
        await SettleAsync(factory, LoanIdOf(settled));

        var active = await JsonOfAsync(await manager.GetAsync(LoansUrl(), Ct));
        var settledPage = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}?status=SETTLED", Ct));

        // Por fecha de desembolso, la más reciente primero.
        Assert.Equal(["COP activo", "USD activo"], active.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(2, active.GetProperty("total").GetInt32());
        Assert.Equal(1, active.GetProperty("page").GetInt32());
        Assert.Equal(25, active.GetProperty("pageSize").GetInt32());
        Assert.Equal(["Saldado"], settledPage.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(JsonValueKind.Null, settledPage.GetProperty("items")[0].GetProperty("nextCutoffDate").ValueKind);

        // El resumen es del tenant entero: el mismo con la lista activa y con la de saldados.
        foreach (var page in new[] { active, settledPage })
        {
            var summary = page.GetProperty("summary");
            Assert.Equal(2, summary.GetProperty("activeCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("settledCount").GetInt32());
            Assert.Equal(2, summary.GetProperty("pendingInstallmentCount").GetInt32());
            Assert.Equal([("COP", 100_000_000m), ("USD", 5_000m)], AmountsOf(summary.GetProperty("outstandingPrincipalByCurrency")));
            Assert.Equal([("USD", 200m)], AmountsOf(summary.GetProperty("pendingAmountByCurrency")));
            // 20 días del 1 al 20 de octubre sobre 31: 100.000.000 × 1,5 % y 5.000 × 2 %.
            Assert.Equal([("COP", 967_741.94m), ("USD", 64.52m)], AmountsOf(summary.GetProperty("accruedToDateByCurrency")));
        }

        Assert.Equal(967_741.94m, active.GetProperty("items")[0].GetProperty("accruedToDate").GetDecimal());
        Assert.Equal(200m, active.GetProperty("items")[1].GetProperty("pendingAmount").GetDecimal());
        Assert.Equal(LoanIdOf(cop), active.GetProperty("items")[0].GetProperty("id").GetGuid());

        var badStatus = await manager.GetAsync($"{LoansUrl()}?status=paused", Ct);
        var badSize = await manager.GetAsync($"{LoansUrl()}?pageSize=101", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badStatus.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(badStatus));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badSize.StatusCode);
    }

    [Fact]
    public async Task SearchMatchesTheCustomerOrTheLoanName()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var acme = await SeedCustomerAsync(factory, Tenant, "Acme SA");
        var zeta = await SeedCustomerAsync(factory, Tenant, "Zeta Ltda");
        await CreateAsync(manager, LoanBody(acme, name: "Capital de trabajo"));
        await CreateAsync(manager, LoanBody(zeta, name: "Puente Acme"));

        async Task<string[]> NamesAsync(string search)
        {
            var page = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}?search={Uri.EscapeDataString(search)}", Ct));
            return page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).Order().ToArray();
        }

        Assert.Equal(["Capital de trabajo", "Puente Acme"], await NamesAsync("acme"));
        Assert.Equal(["Puente Acme"], await NamesAsync("ZETA"));
        Assert.Equal(["Capital de trabajo"], await NamesAsync("trabajo"));
        Assert.Empty(await NamesAsync("xyz"));
        // % y _ son literales, no comodines.
        Assert.Empty(await NamesAsync("%"));
    }

    [Fact]
    public async Task TheListAndTheDetailCarryTheSameNextCutoffDate()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var endOfMonth = await CreateAsync(manager, LoanBody(customerId, name: "Fin de mes"));
        var onTheTwelfth = await CreateAsync(
            manager, LoanBody(customerId, name: "Día 12", disbursedOn: "2026-10-12", cutoffRule: "DisbursementDay"));

        var list = await JsonOfAsync(await manager.GetAsync(LoansUrl(), Ct));

        string? CutoffInList(JsonElement created) => list.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == LoanIdOf(created))
            .GetProperty("nextCutoffDate").GetString();

        Assert.Equal("2026-10-31", endOfMonth.GetProperty("loan").GetProperty("nextCutoffDate").GetString());
        Assert.Equal("2026-10-31", CutoffInList(endOfMonth));
        Assert.Equal("2026-11-11", onTheTwelfth.GetProperty("loan").GetProperty("nextCutoffDate").GetString());
        Assert.Equal("2026-11-11", CutoffInList(onTheTwelfth));
    }

    [Fact]
    public async Task ThePendingInstallmentCountIgnoresTheListFilters()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        // Agosto y septiembre, y septiembre: tres cuotas pendientes en el tenant.
        await CreateAsync(manager, LoanBody(customerId, name: "Uno", disbursedOn: "2026-08-01"));
        await CreateAsync(manager, LoanBody(customerId, name: "Dos", disbursedOn: "2026-09-01"));

        foreach (var query in new[] { "", "?status=settled", "?search=sin-resultados", "?page=2&pageSize=1" })
        {
            var page = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}{query}", Ct));

            Assert.Equal(3, page.GetProperty("summary").GetProperty("pendingInstallmentCount").GetInt32());
        }
    }

    [Fact]
    public async Task UpdateWithoutIfMatchIs428()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var detail = await CreateAsync(manager, LoanBody(customerId));

        var response = await PutAsync(manager, LoanIdOf(detail), LoanBody(customerId, name: "Otro"), ifMatch: null);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(response));
    }

    [Fact]
    public async Task UpdateWithAStaleVersionIs412()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var detail = await CreateAsync(manager, LoanBody(customerId));

        var response = await PutAsync(manager, LoanIdOf(detail), LoanBody(customerId, name: "Otro"), "\"99\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(response));
    }

    [Fact]
    public async Task WithoutMovementEveryTermCanChange()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var otherCustomer = await SeedCustomerAsync(factory, Tenant, "Otro SAS");
        var admin = await SeedMemberAsync(factory, "zoe@example.com", "Zoe Admin", "admin");
        var detail = await CreateAsync(manager, LoanBody(customerId));
        Assert.True(detail.GetProperty("canEditTerms").GetBoolean());

        var response = await PutAsync(
            manager,
            LoanIdOf(detail),
            LoanBody(otherCustomer, name: "Renombrado", principal: 200_000_000m, currency: "USD", rate: 2m, disbursedOn: "2026-08-20", cutoffRule: "DisbursementDay", assignee: admin, note: "  nota  "),
            IfMatchOf(detail));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await JsonOfAsync(response);
        var loan = updated.GetProperty("loan");
        Assert.Equal("Otro SAS", loan.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal("Renombrado", loan.GetProperty("name").GetString());
        Assert.Equal(200_000_000m, loan.GetProperty("principalAmount").GetDecimal());
        Assert.Equal(200_000_000m, loan.GetProperty("outstandingPrincipal").GetDecimal());
        Assert.Equal("USD", loan.GetProperty("currency").GetString());
        Assert.Equal(2m, loan.GetProperty("monthlyRatePercent").GetDecimal());
        Assert.Equal("DisbursementDay", loan.GetProperty("cutoffRule").GetString());
        Assert.Equal("nota", loan.GetProperty("note").GetString());
        Assert.Equal(admin, loan.GetProperty("assignee").GetProperty("memberId").GetGuid());
        // Moverlo al pasado genera las cuotas de los períodos ya cerrados: el 20 de agosto, el 20 de septiembre y hasta el 19 de octubre.
        Assert.Equal(["2026-09-20", "2026-08-20"], updated.GetProperty("installments").EnumerateArray().Select(item => item.GetProperty("periodStart").GetString()!).ToArray());
        Assert.False(updated.GetProperty("canEditTerms").GetBoolean());
        Assert.True(loan.GetProperty("version").GetInt64() > detail.GetProperty("loan").GetProperty("version").GetInt64());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, LoansAuditActions.LoanUpdated, LoanIdOf(detail).ToString()));
    }

    [Fact]
    public async Task CorrectingATinyPrincipalBillsThePeriodThatWasMarkedWithoutInterest()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        // 0,01 de capital: septiembre cierra con interés 0,00, sin cuota pero marcado generado. No hay movimiento: se puede editar.
        var detail = await CreateAsync(manager, LoanBody(customerId, principal: 0.01m, disbursedOn: "2026-09-10"));
        Assert.Equal(0, detail.GetProperty("installments").GetArrayLength());
        Assert.True(detail.GetProperty("canEditTerms").GetBoolean());

        var response = await PutAsync(manager, LoanIdOf(detail), LoanBody(customerId, principal: 100_000_000m, disbursedOn: "2026-09-10"), IfMatchOf(detail));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var installment = Assert.Single((await JsonOfAsync(response)).GetProperty("installments").EnumerateArray());
        Assert.Equal("2026-09-10", installment.GetProperty("periodStart").GetString());
        Assert.Equal("2026-09-30", installment.GetProperty("periodEnd").GetString());
        // 21 días de 30 al 1,5 % sobre 100.000.000: 1.500.000 x 21 / 30.
        Assert.Equal(1_050_000m, installment.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task UpdatingWithoutACurrencyKeepsTheLoanCurrencyWithoutMovement()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var detail = await CreateAsync(manager, LoanBody(customerId, currency: "USD"));

        var response = await PutAsync(manager, LoanIdOf(detail), LoanBody(customerId, currency: null, name: "Otro"), IfMatchOf(detail));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("USD", (await JsonOfAsync(response)).GetProperty("loan").GetProperty("currency").GetString());
    }

    [Fact]
    public async Task UpdatingWithoutACurrencyDoesNotLockTheTermsOfAForeignCurrencyLoanWithMovement()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var detail = await CreateAsync(manager, LoanBody(customerId, currency: "USD", disbursedOn: "2026-09-10"));
        Assert.False(detail.GetProperty("canEditTerms").GetBoolean());

        var response = await PutAsync(manager, LoanIdOf(detail), LoanBody(customerId, currency: null, disbursedOn: "2026-09-10", name: "Otro"), IfMatchOf(detail));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var loan = (await JsonOfAsync(response)).GetProperty("loan");
        Assert.Equal("USD", loan.GetProperty("currency").GetString());
        Assert.Equal("Otro", loan.GetProperty("name").GetString());
    }

    [Fact]
    public async Task WithMovementOnlyNameAssigneeAndNoteCanChange()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var admin = await SeedMemberAsync(factory, "zoe@example.com", "Zoe Admin", "admin");
        var detail = await CreateAsync(manager, LoanBody(customerId, disbursedOn: "2026-09-10"));
        Assert.False(detail.GetProperty("canEditTerms").GetBoolean());

        // 100000000.00 es el mismo capital que 100000000: no es terms_locked. El JSON va a mano para que el cero final llegue.
        var raw = $$"""
            {"customerId":"{{customerId}}","name":"Renombrado","principalAmount":100000000.00,"currency":"cop","monthlyRatePercent":1.5000,
             "disbursedOn":"2026-09-10","cutoffRule":"EndOfMonth","assigneeMemberId":"{{admin}}","note":"Con nota"}
            """;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{LoansUrl()}/{LoanIdOf(detail)}")
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", IfMatchOf(detail));

        var response = await manager.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await JsonOfAsync(response)).GetProperty("loan");
        Assert.Equal("Renombrado", updated.GetProperty("name").GetString());
        Assert.Equal("Con nota", updated.GetProperty("note").GetString());
        Assert.Equal(admin, updated.GetProperty("assignee").GetProperty("memberId").GetGuid());
        Assert.Equal(100_000_000m, updated.GetProperty("outstandingPrincipal").GetDecimal());
    }

    [Fact]
    public async Task ChangingATermWithMovementIsRejected()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var detail = await CreateAsync(manager, LoanBody(customerId, disbursedOn: "2026-09-10"));

        var response = await PutAsync(manager, LoanIdOf(detail), LoanBody(customerId, disbursedOn: "2026-09-10", rate: 2m, name: "Otro"), IfMatchOf(detail));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.loan.terms_locked", await CodeOfAsync(response));
        // Nada se escribió: ni la tasa ni el nombre.
        var reread = (await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}/{LoanIdOf(detail)}", Ct))).GetProperty("loan");
        Assert.Equal(1.5m, reread.GetProperty("monthlyRatePercent").GetDecimal());
        Assert.Equal("Novasteel", reread.GetProperty("name").GetString());
    }

    [Fact]
    public async Task DeleteRemovesALoanWithOnlyPendingInstallments()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var detail = await CreateAsync(manager, LoanBody(customerId, disbursedOn: "2026-08-01"));
        var id = LoanIdOf(detail);

        var response = await manager.DeleteAsync($"{LoansUrl()}/{id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var gone = await manager.GetAsync($"{LoansUrl()}/{id}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("loans.loan.not_found", await CodeOfAsync(gone));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, LoansAuditActions.LoanDeleted, id.ToString()));
    }

    [Fact]
    public async Task DeleteIsRejectedWithAPaidInstallment()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var detail = await CreateAsync(manager, LoanBody(customerId, disbursedOn: "2026-08-01"));
        var id = LoanIdOf(detail);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var installment = await scope.ServiceProvider.GetRequiredService<ILoanInstallmentRepository>().FindAsync(
                Tenant,
                new LoanId(id),
                detail.GetProperty("installments")[0].GetProperty("id").GetGuid(),
                Ct);
            installment!.MarkPaid(new DateOnly(2026, 10, 10), null, Guid.NewGuid(), new DateOnly(2026, 10, 20), NineLocal);
            await scope.ServiceProvider.GetRequiredService<ILoansUnitOfWork>().SaveChangesAsync(Ct);
        }

        var response = await manager.DeleteAsync($"{LoansUrl()}/{id}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.loan.has_movement", await CodeOfAsync(response));
        var reread = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}/{id}", Ct));
        Assert.False(reread.GetProperty("canDelete").GetBoolean());
    }

    [Fact]
    public async Task DeleteIsRejectedWithACapitalPayment()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var detail = await CreateAsync(manager, LoanBody(customerId));
        var id = LoanIdOf(detail);
        // Anulado: la fila sigue ahí, y por eso cuenta como movimiento.
        var payment = LoanCapitalPayment.Create(Tenant, new LoanId(id), 1_000_000m, new DateOnly(2026, 10, 10), null, NineLocal);
        payment.SetBalanceAfter(99_000_000m);
        payment.MarkVoided("error de digitación", NineLocal);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ILoanCapitalPaymentRepository>().Add(payment);
            await scope.ServiceProvider.GetRequiredService<ILoansUnitOfWork>().SaveChangesAsync(Ct);
        }

        var response = await manager.DeleteAsync($"{LoansUrl()}/{id}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.loan.has_movement", await CodeOfAsync(response));
        var reread = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}/{id}", Ct));
        Assert.False(reread.GetProperty("canEditTerms").GetBoolean());
        Assert.False(reread.GetProperty("canDelete").GetBoolean());
        Assert.Single(reread.GetProperty("capitalPayments").EnumerateArray());
    }

    [Fact]
    public async Task TheDetailAddsUpToWhatTheListShows()
    {
        var (database, factory) = await OpenAtNineAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId, disbursedOn: "2026-07-15"));
        var id = LoanIdOf(created);

        var detail = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}/{id}", Ct));
        var list = await JsonOfAsync(await manager.GetAsync(LoansUrl(), Ct));

        var fromList = list.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id);
        var fromDetail = detail.GetProperty("loan");
        // La misma fila, campo por campo: la pantalla nunca muestra una cifra que no cuadre con la otra.
        Assert.Equal(fromList.GetRawText(), fromDetail.GetRawText());

        var installments = detail.GetProperty("installments").EnumerateArray().ToList();
        Assert.Equal(installments.Count, fromDetail.GetProperty("pendingCount").GetInt32());
        Assert.Equal(installments.Sum(item => item.GetProperty("amount").GetDecimal()), fromDetail.GetProperty("pendingAmount").GetDecimal());
        Assert.All(installments, item => Assert.Equal(
            item.GetProperty("amount").GetDecimal(),
            item.GetProperty("segments").EnumerateArray().Sum(segment => segment.GetProperty("interest").GetDecimal())));
        var summary = list.GetProperty("summary");
        Assert.Equal(installments.Count, summary.GetProperty("pendingInstallmentCount").GetInt32());
        Assert.Equal(
            [("COP", fromDetail.GetProperty("pendingAmount").GetDecimal())],
            AmountsOf(summary.GetProperty("pendingAmountByCurrency")));
        Assert.Equal(
            fromDetail.GetProperty("accruedToDate").GetDecimal(),
            detail.GetProperty("currentPeriod").GetProperty("accruedAmount").GetDecimal());
        Assert.Equal(
            detail.GetProperty("currentPeriod").GetProperty("accruedAmount").GetDecimal(),
            detail.GetProperty("currentPeriod").GetProperty("segments").EnumerateArray().Sum(segment => segment.GetProperty("interest").GetDecimal()));
    }

    private static (string Currency, decimal Amount)[] AmountsOf(JsonElement array) => array.EnumerateArray()
        .Select(item => (item.GetProperty("currency").GetString()!, item.GetProperty("amount").GetDecimal()))
        .ToArray();

    // Sin capital y sin cuotas pendientes, que es como un préstamo llega a saldado; el abono que lo hace es de otra tarea.
    private static async Task SettleAsync(QepApiFactory factory, Guid loanId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var loan = await scope.ServiceProvider.GetRequiredService<ILoanRepository>().FindAsync(Tenant, new LoanId(loanId), Ct);
        loan!.SetOutstanding(0m, NineLocal);
        loan.RefreshStatus(0, NineLocal);
        await scope.ServiceProvider.GetRequiredService<ILoansUnitOfWork>().SaveChangesAsync(Ct);
    }
}
