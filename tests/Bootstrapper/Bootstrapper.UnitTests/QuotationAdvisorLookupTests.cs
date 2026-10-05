using Modules.Quotations.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Cómo resuelve el composition root las asesoras de una cotización o de un pedido: sólo las
/// membresías pedidas, nunca el tenant entero, y nunca una de otro tenant.
/// </summary>
public sealed class QuotationAdvisorLookupTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FindReadsOnlyTheRequestedMembershipsInsteadOfTheWholeTenant()
    {
        var ana = Advisor(Tenant, "Ana Pérez", 7);
        var notRequested = Advisor(Tenant, "Luisa Gómez", 8);
        var otherTenant = Advisor(new TenantId(Guid.CreateVersion7()), "Marta Ruiz", 9);
        var memberships = new InMemoryMembershipRepository(
            ana.Membership, notRequested.Membership, otherTenant.Membership);
        var lookup = new QuotationAdvisorLookup(
            memberships,
            new StubUserDirectory(new Dictionary<Guid, string>
            {
                [ana.Membership.UserId] = ana.Email,
                [notRequested.Membership.UserId] = notRequested.Email,
                [otherTenant.Membership.UserId] = otherTenant.Email,
            }));

        var advisors = await lookup.FindAsync(
            Tenant.Value,
            [ana.Membership.Id.Value, otherTenant.Membership.Id.Value, Guid.CreateVersion7()],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, memberships.ListByTenantCalls);
        Assert.Equal(1, memberships.ListByIdsCalls);
        var found = Assert.Single(advisors);
        Assert.Equal(ana.Membership.Id.Value, found.Key);
        Assert.Equal(new QuotationAdvisor(ana.Email, "Ana Pérez", 7), found.Value);
    }

    [Fact]
    public async Task FindWithoutIdsDoesNotReachTenancy()
    {
        var memberships = new InMemoryMembershipRepository();
        var lookup = new QuotationAdvisorLookup(
            memberships, new StubUserDirectory(new Dictionary<Guid, string>()));

        var advisors = await lookup.FindAsync(Tenant.Value, [], TestContext.Current.CancellationToken);

        Assert.Empty(advisors);
        Assert.Equal(0, memberships.ListByTenantCalls);
        Assert.Equal(0, memberships.ListByIdsCalls);
    }

    private static (Membership Membership, string Email) Advisor(
        TenantId tenant, string displayName, int advisorCode)
    {
        var userId = Guid.CreateVersion7();
        var membership = Membership.Invite(
            MembershipId.New(),
            userId,
            tenant,
            displayName,
            ["advisor"],
            "invitation",
            $"token-{userId:N}",
            $"hash-{userId:N}",
            Now.AddHours(-1),
            Membership.DefaultInvitationTimeToLive,
            advisorCode);
        return (membership, $"asesora-{userId:N}@example.com");
    }
}
