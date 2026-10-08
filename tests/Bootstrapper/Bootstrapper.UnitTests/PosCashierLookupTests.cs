using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El nombre del cajero que el POS congela en la caja y en el ticket: nunca null para una membresía
/// del tenant, nunca el GUID de la membresía.
/// </summary>
public sealed class PosCashierLookupTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithoutDisplayNameNorEmailTheNameIsTheFallbackLabel()
    {
        var membership = Cashier(Tenant, displayName: null);
        var lookup = new PosCashierLookup(
            new InMemoryMembershipRepository(membership), new StubUserDirectory(new Dictionary<Guid, string>()));

        var name = await lookup.FindNameAsync(Tenant.Value, membership.Id.Value, TestContext.Current.CancellationToken);

        Assert.Equal("Cajero", name);
    }

    [Fact]
    public async Task WithoutDisplayNameTheEmailIsUsed()
    {
        var membership = Cashier(Tenant, displayName: null);
        var lookup = new PosCashierLookup(
            new InMemoryMembershipRepository(membership),
            new StubUserDirectory(new Dictionary<Guid, string> { [membership.UserId] = "cajera@example.com" }));

        var name = await lookup.FindNameAsync(Tenant.Value, membership.Id.Value, TestContext.Current.CancellationToken);

        Assert.Equal("cajera@example.com", name);
    }

    [Fact]
    public async Task TheDisplayNameWinsOverTheEmail()
    {
        var membership = Cashier(Tenant, displayName: "Ana Pérez");
        var lookup = new PosCashierLookup(
            new InMemoryMembershipRepository(membership),
            new StubUserDirectory(new Dictionary<Guid, string> { [membership.UserId] = "ana@example.com" }));

        var name = await lookup.FindNameAsync(Tenant.Value, membership.Id.Value, TestContext.Current.CancellationToken);

        Assert.Equal("Ana Pérez", name);
    }

    [Fact]
    public async Task AMembershipOfAnotherTenantIsNull()
    {
        var foreign = Cashier(new TenantId(Guid.CreateVersion7()), displayName: "Marta Ruiz");
        var lookup = new PosCashierLookup(
            new InMemoryMembershipRepository(foreign), new StubUserDirectory(new Dictionary<Guid, string>()));

        var name = await lookup.FindNameAsync(Tenant.Value, foreign.Id.Value, TestContext.Current.CancellationToken);

        Assert.Null(name);
    }

    // El dominio no deja crear una membresía sin nombre; las filas heredadas sí pueden traerlo en
    // null (display_name es nullable en la base), y es justo el caso que el respaldo cubre.
    private static Membership Cashier(TenantId tenant, string? displayName)
    {
        var userId = Guid.CreateVersion7();
        var membership = Membership.Invite(
            MembershipId.New(), userId, tenant, displayName ?? "temporal", ["cashier"], "invitation",
            $"token-{userId:N}", $"hash-{userId:N}", Now.AddHours(-1), Membership.DefaultInvitationTimeToLive);
        if (displayName is null)
        {
            typeof(Membership).GetProperty(nameof(Membership.DisplayName))!.SetValue(membership, null);
        }

        return membership;
    }
}
