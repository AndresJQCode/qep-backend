using System.Net;
using System.Net.Http.Json;
using Modules.Scheduling.Application;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// Search and create the customer of an appointment through the Agenda itself (plan 2026-11-21, correction 5): the scheduling roles carry
/// no Customers permission, so the lookup goes through <c>ISchedulingCustomerLookup</c> and only needs <c>appointment.manage</c>.
/// </summary>
public sealed class SchedulingCustomersApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACustomerIsCreatedWithNameAndPhoneAndFoundBySearch()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/customers", new { name = "Ana Ruiz", phone = "3001234567" });
        var found = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/customers?search=Ruiz");

        Assert.Equal("Ana Ruiz", created.GetProperty("name").GetString());
        Assert.Equal("3001234567", created.GetProperty("phone").GetString());
        var match = Assert.Single(found.EnumerateArray());
        Assert.Equal(created.GetProperty("id").GetGuid(), match.GetProperty("id").GetGuid());
        Assert.Equal("Ana Ruiz", match.GetProperty("name").GetString());
        Assert.Equal("3001234567", match.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task TheSearchFindsByPhoneAndNeverReturnsAnotherTenantsCustomers()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var other = await NewTenantAsync(world.Factory, "Clínica Sur");
        await CreateCustomerAsync(tenant, "Luis Mora", "3109998877");
        await CreateCustomerAsync(other, "Luis Mora", "3109998877");

        var byPhone = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/customers?search=3109998877");

        Assert.Single(byPhone.EnumerateArray());
    }

    [Fact]
    public async Task ACustomerNeedsAName()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        var response = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/customers", new { name = " ", phone = "3001234567" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "name"));
    }

    [Fact]
    public async Task ACustomerCanBeCreatedWithoutAPhone()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/customers", new { name = "Sin Teléfono" });

        Assert.Equal(System.Text.Json.JsonValueKind.Null, created.GetProperty("phone").ValueKind);
    }

    [Fact]
    public async Task WithoutAppointmentManageTheCustomerRoutesAre403()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var readOnly = tenant.Client(null, SchedulingPermissions.AppointmentRead);

        var search = await readOnly.GetAsync($"{tenant.Url}/customers?search=a", Ct);
        var create = await readOnly.PostAsJsonAsync($"{tenant.Url}/customers", new { name = "Ana" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, search.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task AnotherTenantCannotUseTheRoutesOfThisOne()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var stranger = await NewTenantAsync(world.Factory, "Clínica Sur");

        var response = await stranger.Admin.GetAsync($"{tenant.Url}/customers?search=a", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(response)));
    }
}
