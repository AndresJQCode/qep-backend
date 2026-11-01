using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Loans.IntegrationTests.LoansApiHarness;

namespace Modules.Loans.IntegrationTests;

/// <summary>
/// La sonda de retención de Loans: Identity no borra a un usuario huérfano mientras alguna de sus membresías sea la encargada de
/// un préstamo o haya marcado el pago de una cuota.
/// </summary>
public sealed class LoansUserReferenceProbeTests
{
    private static readonly Guid Tenant = Guid.Parse(LoansApiHarness.TenantId);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Hoy en Bogotá, que es el reloj del tenant: la fecha UTC puede ser la de mañana y el pago se rechazaría por futuro.
    private static DateOnly TodayLocal => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<Guid> UserOfAsync(QepApiFactory factory, Guid membershipId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Memberships
            .Where(membership => membership.Id == new MembershipId(membershipId))
            .Select(membership => membership.UserId)
            .SingleAsync(Ct);
    }

    private static async Task<bool> RetainsAsync(QepApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetServices<IUserReferenceProbe>().Single(candidate => candidate.Source == "loans");
        return await probe.HasReferencesAsync(userId, Ct);
    }

    private static async Task<JsonElement> CreateLoanAsync(HttpClient client, Guid customerId, Guid? assignee)
    {
        var response = await client.PostAsJsonAsync(LoansUrl(), new Dictionary<string, object?>
        {
            ["customerId"] = customerId,
            ["name"] = "Novasteel",
            ["principalAmount"] = 100_000_000m,
            ["currency"] = null,
            ["monthlyRatePercent"] = 1.5m,
            // Desembolsado hace meses: el préstamo nace con cuotas cerradas que se pueden pagar.
            ["disbursedOn"] = Iso(new DateOnly(TodayLocal.Year, TodayLocal.Month, 1).AddMonths(-3)),
            ["cutoffRule"] = "EndOfMonth",
            ["assigneeMemberId"] = assignee,
            ["note"] = null,
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task AnAssigneeRetainsTheUser()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var assignee = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        var assigneeUser = await UserOfAsync(factory, assignee);
        Assert.False(await RetainsAsync(factory, assigneeUser));

        using var manager = CreateManager(factory);
        await CreateLoanAsync(manager, customerId, assignee);

        Assert.True(await RetainsAsync(factory, assigneeUser));
    }

    [Fact]
    public async Task WhoPaidAnInstallmentRetainsTheUser()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var caller = await SeedCallerMembershipAsync(factory, "Caro Cajera");
        var callerUser = await UserOfAsync(factory, caller);
        using var manager = CreateManager(factory);
        var created = await CreateLoanAsync(manager, customerId, null);
        Assert.False(await RetainsAsync(factory, callerUser));

        var loanId = created.GetProperty("loan").GetProperty("id").GetGuid();
        var installmentId = created.GetProperty("installments").EnumerateArray().Last().GetProperty("id").GetGuid();
        var paid = await manager.PostAsJsonAsync(
            $"{LoansUrl()}/{loanId}/installments/{installmentId}/pay", new { paidOn = Iso(TodayLocal), note = (string?)null }, Ct);
        Assert.True(paid.IsSuccessStatusCode, await BodyAsync(paid));

        Assert.True(await RetainsAsync(factory, callerUser));
    }

    [Fact]
    public async Task AnUnrelatedUserIsNotRetained()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Novasteel SAS");
        var assignee = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        var bystander = await SeedMemberAsync(factory, "beto@example.com", "Beto Ajeno", "admin");
        var bystanderUser = await UserOfAsync(factory, bystander);
        using var manager = CreateManager(factory);

        await CreateLoanAsync(manager, customerId, assignee);

        Assert.False(await RetainsAsync(factory, bystanderUser));
    }
}
