using System.Globalization;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BuildingBlocks.Application;
using Modules.ServiceContracts.Application;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.ServiceContracts.IntegrationTests.ServiceContractsApiHarness;

namespace Modules.ServiceContracts.IntegrationTests;

/// <summary>
/// La sonda de retención de ServiceContracts (spec cobros-recurrentes §3.1): Identity no borra a un usuario huérfano mientras alguna de sus
/// membresías sea encargada de un perfil o haya marcado un pago.
/// </summary>
public sealed class ServiceContractsUserReferenceProbeTests
{
    private static readonly Guid Tenant = Guid.Parse(ServiceContractsApiHarness.TenantId);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> UserOfAsync(QepApiFactory factory, Guid membershipId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Memberships
            .Where(membership => membership.Id == new Modules.Tenancy.Domain.MembershipId(membershipId))
            .Select(membership => membership.UserId)
            .SingleAsync(Ct);
    }

    private static async Task<bool> RetainsAsync(QepApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetServices<IUserReferenceProbe>().Single(candidate => candidate.Source == "billing");
        return await probe.HasReferencesAsync(userId, Ct);
    }

    [Fact]
    public async Task TheProbeRetainsAUserWhoIsAnAssigneeOrMarkedAPayment()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var assignee = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        var caller = await SeedCallerMembershipAsync(factory, "Caro Cajera");
        var bystander = await SeedMemberAsync(factory, "beto@example.com", "Beto Ajeno", "admin");
        var assigneeUser = await UserOfAsync(factory, assignee);
        var callerUser = await UserOfAsync(factory, caller);
        var bystanderUser = await UserOfAsync(factory, bystander);

        // Sin servicios ni perfiles nadie queda retenido.
        Assert.False(await RetainsAsync(factory, assigneeUser));
        Assert.False(await RetainsAsync(factory, callerUser));

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));
        using var manager = CreateManager(factory);
        var created = await manager.PostAsJsonAsync($"{ServiceContractsUrl()}/services", new
        {
            customerId,
            name = "Hosting",
            price = 100000m,
            cycle = "Monthly",
            firstDueDate = new DateOnly(today.Year, today.Month, Math.Min(today.Day, 28)).AddMonths(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            profile = new { channel = "Email", assigneeMemberId = assignee },
        }, Ct);
        var detail = (await created.Content.ReadFromJsonAsync<ServiceDetailDto>(Ct))!;

        Assert.True(await RetainsAsync(factory, assigneeUser));
        Assert.False(await RetainsAsync(factory, callerUser));
        Assert.False(await RetainsAsync(factory, bystanderUser));

        var chargeId = detail.Charges[0].Id;
        var paid = await manager.PostAsJsonAsync(
            $"{ServiceContractsUrl()}/charges/{chargeId}/pay", new { paidOn = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), note = (string?)null }, Ct);
        Assert.True(paid.IsSuccessStatusCode, await BodyAsync(paid));

        Assert.True(await RetainsAsync(factory, callerUser));
        Assert.False(await RetainsAsync(factory, bystanderUser));
    }
}
