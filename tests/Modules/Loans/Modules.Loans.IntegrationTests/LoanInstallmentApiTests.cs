using System.Net;
using System.Net.Http.Json;
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
/// «Por cobrar» y el pago de cuotas (spec 2026-10-31), por HTTP. El reloj fijo es el 20 de octubre de 2026 a las 9:00 de Bogotá,
/// así que un préstamo desembolsado en agosto o septiembre ya trae sus períodos cerrados como cuotas.
/// </summary>
public sealed class LoanInstallmentApiTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    // En UTC: Npgsql sólo escribe instantes con offset 0, y el reloj del tenant los convierte a Bogotá.
    private static readonly DateTimeOffset NineLocal = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(-5)).ToUniversalTime();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAtAsync(DateTimeOffset? now = null)
    {
        var database = await StartDatabaseAsync();
        var clock = new MutableTenantClock(now ?? NineLocal);
        var factory = new QepApiFactory(database.GetConnectionString(), services =>
        {
            services.RemoveAll<ITenantClock>();
            services.AddSingleton<ITenantClock>(clock);
        });
        await SeedTenantAsync(factory);
        return (database, factory);
    }

    private static Dictionary<string, object?> LoanBody(
        Guid customerId, string name = "Novasteel", string disbursedOn = "2026-08-15", Guid? assignee = null) => new()
    {
        ["customerId"] = customerId,
        ["name"] = name,
        ["principalAmount"] = 100_000_000m,
        ["currency"] = null,
        ["monthlyRatePercent"] = 1.5m,
        ["disbursedOn"] = disbursedOn,
        ["cutoffRule"] = "EndOfMonth",
        ["assigneeMemberId"] = assignee,
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

    private static async Task<JsonElement> CreateAsync(HttpClient client, Dictionary<string, object?> body)
    {
        var response = await client.PostAsJsonAsync(LoansUrl(), body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonOfAsync(response);
    }

    private static Guid LoanIdOf(JsonElement detail) => detail.GetProperty("loan").GetProperty("id").GetGuid();

    // Del más reciente al más viejo, como las manda el detalle.
    private static List<JsonElement> InstallmentsOf(JsonElement detail) => detail.GetProperty("installments").EnumerateArray().ToList();

    private static string PayUrl(Guid loanId, Guid installmentId) => $"{LoansUrl()}/{loanId}/installments/{installmentId}/pay";

    private static string PaymentUrl(Guid loanId, Guid installmentId) => $"{LoansUrl()}/{loanId}/installments/{installmentId}/payment";

    private static Task<HttpResponseMessage> PayAsync(HttpClient client, Guid loanId, Guid installmentId, string paidOn, string? note = null) =>
        client.PostAsJsonAsync(PayUrl(loanId, installmentId), new { paidOn, note }, Ct);

    private static Task<HttpResponseMessage> CorrectAsync(HttpClient client, Guid loanId, Guid installmentId, string paidOn, string? note = null) =>
        client.PutAsJsonAsync(PaymentUrl(loanId, installmentId), new { paidOn, note }, Ct);

    // Sin capital, que es como un préstamo llega a saldado; el abono que lo hace es de otra tarea.
    private static async Task ZeroPrincipalAsync(QepApiFactory factory, Guid loanId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var loan = await scope.ServiceProvider.GetRequiredService<ILoanRepository>().FindAsync(Tenant, new LoanId(loanId), Ct);
        loan!.SetOutstanding(0m, NineLocal);
        await scope.ServiceProvider.GetRequiredService<ILoansUnitOfWork>().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task PayingMarksTheInstallmentAndRecordsWhoPaid()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var memberId = await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId));
        var loanId = LoanIdOf(created);
        var installmentId = InstallmentsOf(created)[^1].GetProperty("id").GetGuid();
        var versionBefore = created.GetProperty("loan").GetProperty("version").GetInt64();

        var response = await PayAsync(manager, loanId, installmentId, "2026-10-05", "  Transferencia  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await JsonOfAsync(response);
        var paid = InstallmentsOf(detail).Single(item => item.GetProperty("id").GetGuid() == installmentId);
        Assert.Equal("Paid", paid.GetProperty("status").GetString());
        var payment = paid.GetProperty("payment");
        Assert.Equal("2026-10-05", payment.GetProperty("paidOn").GetString());
        Assert.Equal("Transferencia", payment.GetProperty("note").GetString());
        Assert.Equal(memberId, payment.GetProperty("paidBy").GetProperty("memberId").GetGuid());
        Assert.Equal("Ana Cobros", payment.GetProperty("paidBy").GetProperty("displayName").GetString());
        Assert.Equal(1, detail.GetProperty("loan").GetProperty("pendingCount").GetInt32());
        // Pagar toca el préstamo: choca con un abono en curso.
        Assert.True(detail.GetProperty("loan").GetProperty("version").GetInt64() > versionBefore);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, LoansAuditActions.InstallmentPaid, installmentId.ToString()));
    }

    [Fact]
    public async Task PayingTwiceIs422NotPending()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId));
        var installmentId = InstallmentsOf(created)[^1].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PayAsync(manager, LoanIdOf(created), installmentId, "2026-10-05")).StatusCode);

        var response = await PayAsync(manager, LoanIdOf(created), installmentId, "2026-10-06");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.installment.not_pending", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AFuturePaymentDateIsRejectedInTheTenantZone()
    {
        // 23:30 del 31 de octubre en Bogotá ya es el 1 de noviembre en UTC: para el tenant el 1 todavía es futuro.
        var now = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(-5)).ToUniversalTime();
        var (database, factory) = await OpenAtAsync(now);
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId, disbursedOn: "2026-09-01"));
        var installmentId = InstallmentsOf(created)[0].GetProperty("id").GetGuid();

        var future = await PayAsync(manager, LoanIdOf(created), installmentId, "2026-11-01");
        var today = await PayAsync(manager, LoanIdOf(created), installmentId, "2026-10-31");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);
        Assert.Equal("loans.payment.date_in_future", await CodeOfAsync(future));
        Assert.Equal(HttpStatusCode.OK, today.StatusCode);
    }

    [Fact]
    public async Task AnyPendingInstallmentCanBePaidInAnyOrder()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId));
        var installments = InstallmentsOf(created);
        Assert.Equal(2, installments.Count);

        // La más reciente primero: nada obliga a pagar en orden.
        var response = await PayAsync(manager, LoanIdOf(created), installments[0].GetProperty("id").GetGuid(), "2026-10-05");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await JsonOfAsync(response);
        Assert.Equal("Paid", InstallmentsOf(detail)[0].GetProperty("status").GetString());
        Assert.Equal("Pending", InstallmentsOf(detail)[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnInstallmentOfAnotherLoanIsNotFound()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var first = await CreateAsync(manager, LoanBody(customerId, "Uno"));
        var second = await CreateAsync(manager, LoanBody(customerId, "Dos"));
        var foreignInstallment = InstallmentsOf(second)[0].GetProperty("id").GetGuid();

        var response = await PayAsync(manager, LoanIdOf(first), foreignInstallment, "2026-10-05");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("loans.installment.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task CorrectingChangesDateAndNote()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var memberId = await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId));
        var loanId = LoanIdOf(created);
        var installmentId = InstallmentsOf(created)[^1].GetProperty("id").GetGuid();
        var paid = await JsonOfAsync(await PayAsync(manager, loanId, installmentId, "2026-10-05", "Primera"));
        var versionAfterPay = paid.GetProperty("loan").GetProperty("version").GetInt64();

        var response = await CorrectAsync(manager, loanId, installmentId, "2026-10-07", "Corregida");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await JsonOfAsync(response);
        var payment = InstallmentsOf(detail).Single(item => item.GetProperty("id").GetGuid() == installmentId).GetProperty("payment");
        Assert.Equal("2026-10-07", payment.GetProperty("paidOn").GetString());
        Assert.Equal("Corregida", payment.GetProperty("note").GetString());
        // Quién pagó no cambia al corregir, y el préstamo no se toca.
        Assert.Equal(memberId, payment.GetProperty("paidBy").GetProperty("memberId").GetGuid());
        Assert.Equal(versionAfterPay, detail.GetProperty("loan").GetProperty("version").GetInt64());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, LoansAuditActions.InstallmentPaymentUpdated, installmentId.ToString()));
    }

    [Fact]
    public async Task CorrectingAnUnpaidInstallmentIsRejected()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId));
        var installmentId = InstallmentsOf(created)[^1].GetProperty("id").GetGuid();

        var response = await CorrectAsync(manager, LoanIdOf(created), installmentId, "2026-10-07");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("loans.installment.not_paid", await CodeOfAsync(response));
    }

    [Fact]
    public async Task RemovingThePaymentReturnsItToPending()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId));
        var loanId = LoanIdOf(created);
        var installmentId = InstallmentsOf(created)[^1].GetProperty("id").GetGuid();
        await PayAsync(manager, loanId, installmentId, "2026-10-05", "Nota");

        var response = await manager.DeleteAsync(PaymentUrl(loanId, installmentId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await JsonOfAsync(response);
        var row = InstallmentsOf(detail).Single(item => item.GetProperty("id").GetGuid() == installmentId);
        Assert.Equal("Pending", row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("payment").ValueKind);
        Assert.Equal(2, detail.GetProperty("loan").GetProperty("pendingCount").GetInt32());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, LoansAuditActions.InstallmentPaymentRemoved, installmentId.ToString()));
        var again = await manager.DeleteAsync(PaymentUrl(loanId, installmentId), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("loans.installment.not_paid", await CodeOfAsync(again));
    }

    [Fact]
    public async Task PayingTheLastInstallmentWithoutCapitalSettlesTheLoan()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId, disbursedOn: "2026-09-01"));
        var loanId = LoanIdOf(created);
        await ZeroPrincipalAsync(factory, loanId);
        var installmentId = InstallmentsOf(created).Single().GetProperty("id").GetGuid();

        var detail = await JsonOfAsync(await PayAsync(manager, loanId, installmentId, "2026-10-05"));

        Assert.Equal("Settled", detail.GetProperty("loan").GetProperty("status").GetString());
        Assert.Equal(0, detail.GetProperty("loan").GetProperty("pendingCount").GetInt32());
    }

    [Fact]
    public async Task RemovingThePaymentReactivatesASettledLoan()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId, disbursedOn: "2026-09-01"));
        var loanId = LoanIdOf(created);
        await ZeroPrincipalAsync(factory, loanId);
        var installmentId = InstallmentsOf(created).Single().GetProperty("id").GetGuid();
        await PayAsync(manager, loanId, installmentId, "2026-10-05");

        var response = await manager.DeleteAsync(PaymentUrl(loanId, installmentId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await JsonOfAsync(response);
        Assert.Equal("Active", detail.GetProperty("loan").GetProperty("status").GetString());
        Assert.Equal(1, detail.GetProperty("loan").GetProperty("pendingCount").GetInt32());
        var reread = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}/{loanId}", Ct));
        Assert.Equal("Active", reread.GetProperty("loan").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ThePendingListIsOrderedByPeriodAndSummarizesTheTenant()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var memberId = await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var novasteel = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var acme = await SeedCustomerAsync(factory, Tenant, "Acme Ltda");
        var older = await CreateAsync(manager, LoanBody(novasteel, "Novasteel", "2026-07-15", memberId));
        var newer = await CreateAsync(manager, LoanBody(acme, "Acme", "2026-09-01"));

        var response = await manager.GetAsync($"{LoansUrl()}/installments?search=acme", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await JsonOfAsync(response);
        // La búsqueda deja afuera a Novasteel, pero el resumen es del tenant entero.
        Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        var summary = page.GetProperty("summary");
        var allInstallments = InstallmentsOf(older).Concat(InstallmentsOf(newer)).ToList();
        Assert.Equal(allInstallments.Count, summary.GetProperty("pendingCount").GetInt32());
        Assert.Equal(
            allInstallments.Sum(item => item.GetProperty("amount").GetDecimal()),
            summary.GetProperty("pendingAmountByCurrency").EnumerateArray().Single().GetProperty("amount").GetDecimal());
        var oldest = summary.GetProperty("oldest");
        Assert.Equal("2026-07-31", oldest.GetProperty("periodEnd").GetString());
        Assert.Equal("Novasteel SAS", oldest.GetProperty("customerName").GetString());
        Assert.Equal("COP", oldest.GetProperty("currency").GetString());

        var all = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}/installments", Ct));
        var rows = all.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(allInstallments.Count, rows.Count);
        var ends = rows.Select(row => row.GetProperty("periodEnd").GetString()!).ToList();
        Assert.Equal(ends.OrderBy(end => end, StringComparer.Ordinal).ToList(), ends);
        var first = rows[0];
        Assert.Equal(LoanIdOf(older), first.GetProperty("loanId").GetGuid());
        Assert.Equal("Novasteel", first.GetProperty("loanName").GetString());
        Assert.Equal("Novasteel SAS", first.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal(memberId, first.GetProperty("assignee").GetProperty("memberId").GetGuid());
        Assert.Equal("COP", first.GetProperty("currency").GetString());
        Assert.False(first.GetProperty("isImported").GetBoolean());
        var withoutAssignee = rows.First(row => row.GetProperty("loanId").GetGuid() == LoanIdOf(newer));
        Assert.Equal(JsonValueKind.Null, withoutAssignee.GetProperty("assignee").ValueKind);
    }

    [Fact]
    public async Task ThePendingListSearchesCustomerAndLoan()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        var novasteel = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var acme = await SeedCustomerAsync(factory, Tenant, "Acme Ltda");
        var byCustomer = await CreateAsync(manager, LoanBody(novasteel, "Capital de trabajo"));
        var byName = await CreateAsync(manager, LoanBody(acme, "Bodega norte"));

        var customerHit = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}/installments?search=nova", Ct));
        var loanHit = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}/installments?search=bodega", Ct));
        var miss = await JsonOfAsync(await manager.GetAsync($"{LoansUrl()}/installments?search=zzz", Ct));

        Assert.All(customerHit.GetProperty("items").EnumerateArray(), row => Assert.Equal(LoanIdOf(byCustomer), row.GetProperty("loanId").GetGuid()));
        Assert.Equal(2, customerHit.GetProperty("total").GetInt32());
        Assert.All(loanHit.GetProperty("items").EnumerateArray(), row => Assert.Equal(LoanIdOf(byName), row.GetProperty("loanId").GetGuid()));
        Assert.Equal(2, loanHit.GetProperty("total").GetInt32());
        Assert.Equal(0, miss.GetProperty("total").GetInt32());
        Assert.Empty(miss.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task AReaderCannotPay()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var manager = CreateManager(factory);
        using var reader = CreateReader(factory);
        await SeedCallerMembershipAsync(factory, "Ana Cobros");
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var created = await CreateAsync(manager, LoanBody(customerId));
        var installmentId = InstallmentsOf(created)[^1].GetProperty("id").GetGuid();

        var response = await PayAsync(reader, LoanIdOf(created), installmentId, "2026-10-05");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await BodyAsync(response);
        if (body.Contains("\"code\"", StringComparison.Ordinal))
        {
            using var problem = System.Text.Json.JsonDocument.Parse(body);
            Assert.Equal("authorization.denied", problem.RootElement.GetProperty("code").GetString());
        }

        // El lector sí puede leer la lista.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{LoansUrl()}/installments", Ct)).StatusCode);
    }
}
