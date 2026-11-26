using Modules.Siigo.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-20 (vendedores vinculados) §4.1: los miembros que el paso «Vendedores» ofrece. Todas las membresías activas del tenant,
/// más las que se piden aparte (las que ya tienen vínculo) aunque no estén activas, marcadas así. Nunca una de otro tenant.
/// </summary>
public sealed class SiigoMemberDirectoryTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListsTheActiveMembersAndTheLinkedOnesThatAreNotActive()
    {
        var vanesa = Member(Tenant, "Vanesa Hernández", accept: true);
        var invited = Member(Tenant, "Sin aceptar", accept: false);
        var linkedButGone = Member(Tenant, "Ya no está", accept: false);
        var otherTenant = Member(new TenantId(Guid.CreateVersion7()), "Otra empresa", accept: true);
        var directory = new SiigoMemberDirectory(
            new InMemoryMembershipRepository(vanesa.Membership, invited.Membership, linkedButGone.Membership, otherTenant.Membership),
            new StubUserDirectory(new Dictionary<Guid, string>
            {
                [vanesa.Membership.UserId] = vanesa.Email,
                [linkedButGone.Membership.UserId] = linkedButGone.Email,
            }));

        var members = await directory.ListAsync(
            Tenant.Value, [linkedButGone.Membership.Id.Value, otherTenant.Membership.Id.Value], TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new SiigoMemberRef(vanesa.Membership.Id.Value, "Vanesa Hernández", vanesa.Email, Active: true),
                new SiigoMemberRef(linkedButGone.Membership.Id.Value, "Ya no está", linkedButGone.Email, Active: false),
            ],
            members.OrderByDescending(member => member.Active));
    }

    private static (Membership Membership, string Email) Member(TenantId tenant, string displayName, bool accept)
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
            null);
        if (accept)
        {
            membership.Accept(Now);
        }

        return (membership, $"asesora-{userId:N}@example.com");
    }
}
