using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Loans.Application;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;
using static Modules.Loans.IntegrationTests.LoansApiHarness;

namespace Modules.Loans.IntegrationTests;

/// <summary>
/// Abonos a capital, su preview y su anulación (spec 2026-10-31), por HTTP. Cada prueba mueve el reloj del tenant: arranca el
/// 20 de octubre de 2026 a las 9:00 de Bogotá, con el préstamo desembolsado el 1 de octubre y por lo tanto sin cuotas.
/// </summary>
public sealed class LoanCapitalPaymentApiTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly DateTimeOffset October20 = Local(2026, 10, 20);
    private static readonly DateTimeOffset October31 = Local(2026, 10, 31);
    private static readonly DateTimeOffset November1 = Local(2026, 11, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // En UTC: Npgsql sólo escribe instantes con offset 0, y el reloj del tenant los convierte a Bogotá.
    private static DateTimeOffset Local(int year, int month, int day) =>
        new DateTimeOffset(year, month, day, 9, 0, 0, TimeSpan.FromHours(-5)).ToUniversalTime();

    private static async Task<(PostgreSqlContainer Database, QepApiFactory Factory, MutableTenantClock Clock)> OpenAtAsync(DateTimeOffset now)
    {
        var database = await StartDatabaseAsync();
        var clock = new MutableTenantClock(now);
        var factory = new QepApiFactory(database.GetConnectionString(), services =>
        {
            services.RemoveAll<ITenantClock>();
            services.AddSingleton<ITenantClock>(clock);
        });
        await SeedTenantAsync(factory);
        return (database, factory, clock);
    }

    private static Dictionary<string, object?> LoanBody(Guid customerId, string disbursedOn = "2026-10-01") => new()
    {
        ["customerId"] = customerId,
        ["name"] = "Novasteel",
        ["principalAmount"] = 100_000_000m,
        ["currency"] = null,
        ["monthlyRatePercent"] = 1.5m,
        ["disbursedOn"] = disbursedOn,
        ["cutoffRule"] = "EndOfMonth",
        ["assigneeMemberId"] = null,
        ["note"] = null,
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

    private static async Task<Guid> CreateLoanAsync(HttpClient client, Guid customerId, string disbursedOn = "2026-10-01")
    {
        var response = await client.PostAsJsonAsync(LoansUrl(), LoanBody(customerId, disbursedOn), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOfAsync(response)).GetProperty("loan").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid loanId)
    {
        var response = await client.GetAsync($"{LoansUrl()}/{loanId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOfAsync(response);
    }

    private static string CapitalUrl(Guid loanId) => $"{LoansUrl()}/{loanId}/capital-payments";

    private static Task<HttpResponseMessage> PayCapitalAsync(HttpClient client, Guid loanId, decimal amount, string paidOn, string? note = null) =>
        client.PostAsJsonAsync(CapitalUrl(loanId), new { amount, paidOn, note }, Ct);

    private static Task<HttpResponseMessage> PreviewAsync(HttpClient client, Guid loanId, decimal amount, string paidOn) =>
        client.PostAsJsonAsync($"{CapitalUrl(loanId)}/preview", new { amount, paidOn }, Ct);

    private static Task<HttpResponseMessage> VoidAsync(HttpClient client, Guid loanId, Guid paymentId, string? reason) =>
        client.PostAsJsonAsync($"{CapitalUrl(loanId)}/{paymentId}/void", new { reason }, Ct);

    private static Guid FirstPaymentIdOf(JsonElement detail) =>
        detail.GetProperty("capitalPayments")[0].GetProperty("id").GetGuid();

    private static decimal OutstandingOf(JsonElement detail) =>
        detail.GetProperty("loan").GetProperty("outstandingPrincipal").GetDecimal();

    [Fact]
    public async Task ThePreviewOfAPartialPaymentWritesNothing()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        var before = await DetailAsync(manager, loanId);

        var response = await PreviewAsync(manager, loanId, 40_000_000m, "2026-10-15");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await JsonOfAsync(response);
        Assert.Equal(60_000_000m, preview.GetProperty("resultingPrincipal").GetDecimal());
        Assert.False(preview.GetProperty("settles").GetBoolean());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("finalInstallment").ValueKind);
        var after = await DetailAsync(manager, loanId);
        Assert.Equal(100_000_000m, OutstandingOf(after));
        Assert.Equal(0, after.GetProperty("capitalPayments").GetArrayLength());
        Assert.Equal(before.GetProperty("loan").GetProperty("version").GetInt64(), after.GetProperty("loan").GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task ThePreviewOfASettlementAnnouncesTheLastInstallment()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));

        var preview = await JsonOfAsync(await PreviewAsync(manager, loanId, 100_000_000m, "2026-10-15"));

        Assert.True(preview.GetProperty("settles").GetBoolean());
        Assert.Equal(0m, preview.GetProperty("resultingPrincipal").GetDecimal());
        var final = preview.GetProperty("finalInstallment");
        Assert.Equal(677419.35m, final.GetProperty("amount").GetDecimal());
        Assert.Equal("2026-10-15", final.GetProperty("periodEnd").GetString());
        Assert.Equal(31, final.GetProperty("periodDays").GetInt32());
        var segment = Assert.Single(final.GetProperty("segments").EnumerateArray());
        Assert.Equal(14, segment.GetProperty("days").GetInt32());
    }

    [Fact]
    public async Task ThePreviewMatchesWhatThePaymentCreates()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        var preview = await JsonOfAsync(await PreviewAsync(manager, loanId, 100_000_000m, "2026-10-15"));

        var created = await PayCapitalAsync(manager, loanId, 100_000_000m, "2026-10-15");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var installment = Assert.Single((await JsonOfAsync(created)).GetProperty("installments").EnumerateArray());
        var announced = preview.GetProperty("finalInstallment");
        Assert.Equal(announced.GetProperty("amount").GetDecimal(), installment.GetProperty("amount").GetDecimal());
        Assert.Equal(announced.GetProperty("periodStart").GetString(), installment.GetProperty("periodStart").GetString());
        Assert.Equal(announced.GetProperty("periodEnd").GetString(), installment.GetProperty("periodEnd").GetString());
        Assert.Equal(announced.GetProperty("segments").GetRawText(), installment.GetProperty("segments").GetRawText());
    }

    [Fact]
    public async Task ThePreviewFailsLikeThePayment()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var clean = await CreateLoanAsync(manager, customerId);
        // Desembolsado en agosto: trae las cuotas de agosto y septiembre.
        var withPending = await CreateLoanAsync(manager, customerId, "2026-08-15");

        (Guid LoanId, decimal Amount, string PaidOn, string Code)[] cases =
        [
            (withPending, 1_000m, "2026-10-15", "loans.capital_payment.pending_installments"),
            (clean, 100_000_001m, "2026-10-15", "loans.capital_payment.exceeds_balance"),
            (clean, 1_000m, "2026-10-21", "loans.capital_payment.date_out_of_period"),
        ];
        foreach (var (loanId, amount, paidOn, code) in cases)
        {
            var preview = await PreviewAsync(manager, loanId, amount, paidOn);
            var payment = await PayCapitalAsync(manager, loanId, amount, paidOn);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, payment.StatusCode);
            Assert.Equal(code, await CodeOfAsync(preview));
            Assert.Equal(code, await CodeOfAsync(payment));
        }
    }

    [Fact]
    public async Task APartialPaymentLowersTheCapital()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));

        var response = await PayCapitalAsync(manager, loanId, 40_000_000m, "2026-10-15", "  Transferencia  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await JsonOfAsync(response);
        Assert.Equal(60_000_000m, OutstandingOf(detail));
        var payment = detail.GetProperty("capitalPayments")[0];
        Assert.Equal(40_000_000m, payment.GetProperty("amount").GetDecimal());
        Assert.Equal(60_000_000m, payment.GetProperty("balanceAfter").GetDecimal());
        Assert.Equal("Transferencia", payment.GetProperty("note").GetString());
        Assert.Equal(payment.GetProperty("id").GetGuid(), detail.GetProperty("voidableCapitalPaymentId").GetGuid());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, LoansAuditActions.CapitalPaymentCreated, payment.GetProperty("id").GetString()!));
    }

    [Fact]
    public async Task OctoberEndsAt1170967Point74()
    {
        var (database, factory, clock) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        Assert.Equal(HttpStatusCode.Created, (await PayCapitalAsync(manager, loanId, 40_000_000m, "2026-10-15")).StatusCode);

        // Sin worker en la prueba: el primer movimiento del 1 de noviembre es el que convierte octubre en cuota.
        clock.UtcNow = November1;
        var blocked = await PayCapitalAsync(manager, loanId, 1_000m, "2026-11-01");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);

        var installment = Assert.Single((await DetailAsync(manager, loanId)).GetProperty("installments").EnumerateArray());
        Assert.Equal(1170967.74m, installment.GetProperty("amount").GetDecimal());
        var segments = installment.GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal(2, segments.Count);
        Assert.Equal(677419.35m, segments[0].GetProperty("interest").GetDecimal());
        Assert.Equal(493548.39m, segments[1].GetProperty("interest").GetDecimal());
    }

    [Fact]
    public async Task PendingInstallmentsBlockThePayment()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"), "2026-08-15");

        var detail = await DetailAsync(manager, loanId);
        var response = await PayCapitalAsync(manager, loanId, 1_000m, "2026-10-15");

        Assert.Equal("PendingInstallments", detail.GetProperty("capitalPaymentBlock").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("capitalPaymentWindow").ValueKind);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.capital_payment.pending_installments", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ACapitalPaymentOnTheLastDayOfThePeriodIsAccepted()
    {
        var (database, factory, _) = await OpenAtAsync(October31);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));

        var response = await PayCapitalAsync(manager, loanId, 40_000_000m, "2026-10-31");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(60_000_000m, OutstandingOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task ACapitalPaymentOverAClosedUngeneratedPeriodGeneratesItAndIsRejected()
    {
        var (database, factory, clock) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        clock.UtcNow = November1;

        var response = await PayCapitalAsync(manager, loanId, 1_000m, "2026-11-01");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.capital_payment.pending_installments", await CodeOfAsync(response));
        // El rechazo habla de una cuota: tiene que estar guardada y a la vista.
        var detail = await DetailAsync(manager, loanId);
        var installment = Assert.Single(detail.GetProperty("installments").EnumerateArray());
        Assert.Equal("2026-10-31", installment.GetProperty("periodEnd").GetString());
        Assert.Equal(0, detail.GetProperty("capitalPayments").GetArrayLength());
    }

    [Fact]
    public async Task ThePreviewOverAClosedPeriodDoesNotGenerate()
    {
        var (database, factory, clock) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        clock.UtcNow = November1;

        var response = await PreviewAsync(manager, loanId, 1_000m, "2026-11-01");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.capital_payment.pending_installments", await CodeOfAsync(response));
        var detail = await DetailAsync(manager, loanId);
        Assert.Equal(0, detail.GetProperty("installments").GetArrayLength());
        Assert.Equal("PeriodClosing", detail.GetProperty("capitalPaymentBlock").GetString());
    }

    [Fact]
    public async Task SettlingLeavesTheLoanActiveUntilTheLastInstallmentIsPaid()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));

        var settled = await JsonOfAsync(await PayCapitalAsync(manager, loanId, 100_000_000m, "2026-10-15"));

        Assert.Equal("Active", settled.GetProperty("loan").GetProperty("status").GetString());
        Assert.Equal(0m, OutstandingOf(settled));
        Assert.Equal(1, settled.GetProperty("loan").GetProperty("pendingCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, settled.GetProperty("loan").GetProperty("nextCutoffDate").ValueKind);
        var installmentId = settled.GetProperty("installments")[0].GetProperty("id").GetGuid();

        var paid = await manager.PostAsJsonAsync($"{LoansUrl()}/{loanId}/installments/{installmentId}/pay", new { paidOn = "2026-10-16", note = (string?)null }, Ct);

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.Equal("Settled", (await JsonOfAsync(paid)).GetProperty("loan").GetProperty("status").GetString());
    }

    [Fact]
    public async Task VoidingTheLatestPaymentRestoresTheCapitalAndKeepsTheRow()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        var paymentId = FirstPaymentIdOf(await JsonOfAsync(await PayCapitalAsync(manager, loanId, 40_000_000m, "2026-10-15")));

        var response = await VoidAsync(manager, loanId, paymentId, "  Error de digitación  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await JsonOfAsync(response);
        Assert.Equal(100_000_000m, OutstandingOf(detail));
        var payment = Assert.Single(detail.GetProperty("capitalPayments").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, payment.GetProperty("voidedAt").ValueKind);
        Assert.Equal("Error de digitación", payment.GetProperty("voidReason").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("voidableCapitalPaymentId").ValueKind);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, LoansAuditActions.CapitalPaymentVoided, paymentId.ToString()));
    }

    [Fact]
    public async Task VoidingAnUnknownPaymentIs404()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));

        var response = await VoidAsync(manager, loanId, Guid.NewGuid(), "x");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("loans.capital_payment.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task NoPaymentIsOfferedForVoidingWhileAClosedPeriodIsNotGeneratedYet()
    {
        var (database, factory, clock) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        Assert.Equal(HttpStatusCode.Created, (await PayCapitalAsync(manager, loanId, 40_000_000m, "2026-10-15")).StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await DetailAsync(manager, loanId)).GetProperty("voidableCapitalPaymentId").ValueKind);
        clock.UtcNow = November1;

        // El 1 de noviembre octubre ya cerró pero el worker aún no lo genera: anular daría installment_generated.
        var detail = await DetailAsync(manager, loanId);

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("voidableCapitalPaymentId").ValueKind);
        Assert.Equal("PeriodClosing", detail.GetProperty("capitalPaymentBlock").GetString());
    }

    [Fact]
    public async Task VoidingWithoutAReasonIs422()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        var paymentId = FirstPaymentIdOf(await JsonOfAsync(await PayCapitalAsync(manager, loanId, 40_000_000m, "2026-10-15")));

        var response = await VoidAsync(manager, loanId, paymentId, null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        var errors = (await JsonOfAsync(response)).GetProperty("errors");
        Assert.Contains(errors.EnumerateObject(), field => string.Equals(field.Name, "Reason", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VoidingAPaymentThatIsNotTheLatestIsRejected()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        var first = FirstPaymentIdOf(await JsonOfAsync(await PayCapitalAsync(manager, loanId, 10_000_000m, "2026-10-10")));
        Assert.Equal(HttpStatusCode.Created, (await PayCapitalAsync(manager, loanId, 10_000_000m, "2026-10-15")).StatusCode);

        var response = await VoidAsync(manager, loanId, first, "x");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.capital_payment.not_latest", await CodeOfAsync(response));
    }

    [Fact]
    public async Task VoidingAfterAnInstallmentWasGeneratedIsRejected()
    {
        var (database, factory, clock) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        var paymentId = FirstPaymentIdOf(await JsonOfAsync(await PayCapitalAsync(manager, loanId, 40_000_000m, "2026-10-15")));
        clock.UtcNow = November1;

        var response = await VoidAsync(manager, loanId, paymentId, "x");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.capital_payment.installment_generated", await CodeOfAsync(response));
        // La generación se guarda aunque la anulación se rechace.
        Assert.Single((await DetailAsync(manager, loanId)).GetProperty("installments").EnumerateArray());
    }

    [Fact]
    public async Task ThePaymentThatSettledCannotBeVoided()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        var paymentId = FirstPaymentIdOf(await JsonOfAsync(await PayCapitalAsync(manager, loanId, 100_000_000m, "2026-10-15")));

        var response = await VoidAsync(manager, loanId, paymentId, "x");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.capital_payment.installment_generated", await CodeOfAsync(response));
    }

    [Fact]
    public async Task VoidingAZeroInterestSettlementReactivatesTheLoan()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        // Saldar el primer día del período no causa interés: no hay cuota y el préstamo queda saldado de inmediato.
        var settled = await JsonOfAsync(await PayCapitalAsync(manager, loanId, 100_000_000m, "2026-10-01"));
        Assert.Equal("Settled", settled.GetProperty("loan").GetProperty("status").GetString());
        Assert.Equal(0, settled.GetProperty("installments").GetArrayLength());

        var response = await VoidAsync(manager, loanId, FirstPaymentIdOf(settled), "Error de digitación");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await JsonOfAsync(response);
        Assert.Equal("Active", detail.GetProperty("loan").GetProperty("status").GetString());
        Assert.Equal(100_000_000m, OutstandingOf(detail));
    }

    [Fact]
    public async Task ASettledLoanHasNoNextCutoffDate()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));
        Assert.Equal(HttpStatusCode.Created, (await PayCapitalAsync(manager, loanId, 100_000_000m, "2026-10-01")).StatusCode);

        var detail = await DetailAsync(manager, loanId);
        var list = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}?status=Settled", Ct));

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("loan").GetProperty("nextCutoffDate").ValueKind);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("nextCutoffDate").ValueKind);
    }

    [Fact]
    public async Task TwoSimultaneousPaymentsCannotOverdrawTheCapital()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var loanId = await CreateLoanAsync(manager, await SeedCustomerAsync(factory, Tenant, "Novasteel SAS"));

        var responses = await Task.WhenAll(
            PayCapitalAsync(manager, loanId, 60_000_000m, "2026-10-15"),
            PayCapitalAsync(manager, loanId, 60_000_000m, "2026-10-15"));

        // Una gana. La otra pierde con 412 si cargó el préstamo antes de que la primera guardara, o con 422 exceeds_balance si
        // llegó después: en ningún caso el capital queda en negativo.
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        var loser = Assert.Single(responses, response => response.StatusCode != HttpStatusCode.Created);
        var expectedCode = loser.StatusCode switch
        {
            HttpStatusCode.PreconditionFailed => "concurrency.conflict",
            HttpStatusCode.UnprocessableEntity => "loans.capital_payment.exceeds_balance",
            _ => throw new Xunit.Sdk.XunitException($"Unexpected status {loser.StatusCode} for the losing payment."),
        };
        Assert.Equal(expectedCode, await CodeOfAsync(loser));
        Assert.Equal(40_000_000m, OutstandingOf(await DetailAsync(manager, loanId)));
    }

    [Fact]
    public async Task AnInactiveCustomerCanStillPay()
    {
        var (database, factory, _) = await OpenAtAsync(October20);
        await using var _1 = database;
        using var _2 = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var loanId = await CreateLoanAsync(manager, customerId);
        await DeactivateCustomerAsync(factory, customerId);

        var response = await PayCapitalAsync(manager, loanId, 40_000_000m, "2026-10-15");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
