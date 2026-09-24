using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// La búsqueda del roster por código de asesor (spec 2026-09-24, D8): exacta sobre el código, no
/// por contenido. Unitaria y no de integración porque los correos que siembra la integración
/// (<c>invitee-{guid}@example.com</c>) tienen dígitos, y <c>?search=1</c> los encontraría por
/// correo. Acá los correos y nombres no tienen ninguno.
/// </summary>
public sealed class ListMembershipsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);
    private static readonly TenantId TenantUnderTest = TenantId.New();

    // Review Focus 4: "1" no trae a 12, 21 ni 100.
    [Fact]
    public async Task SearchingOneDoesNotMatchTwelveTwentyOneOrOneHundred()
    {
        var (memberships, emails) = Roster(
            ("ana@example.com", "Ana Pérez", 12),
            ("carlos@example.com", "Carlos Mejía", 21),
            ("diana@example.com", "Diana Ríos", 100));

        var list = await Handler(memberships, emails).HandleAsync(
            new ListMembershipsQuery(TenantUnderTest, Search: "1"), TestContext.Current.CancellationToken);

        Assert.Empty(list.Items);
        Assert.Equal(0, list.Counts.Total);
    }

    [Fact]
    public async Task SearchingACodeFindsExactlyThatMember()
    {
        var (memberships, emails) = Roster(
            ("ana@example.com", "Ana Pérez", 1),
            ("carlos@example.com", "Carlos Mejía", 12),
            ("diana@example.com", "Diana Ríos", null));

        var list = await Handler(memberships, emails).HandleAsync(
            new ListMembershipsQuery(TenantUnderTest, Search: "12"), TestContext.Current.CancellationToken);

        var only = Assert.Single(list.Items);
        Assert.Equal("carlos@example.com", only.Email);
        Assert.Equal(12, only.AdvisorCode);
    }

    // D2: se guarda como integer, así que 0012 y 12 son el mismo código.
    [Fact]
    public async Task SearchingACodeWithLeadingZerosFindsTheSameMember()
    {
        var (memberships, emails) = Roster(("carlos@example.com", "Carlos Mejía", 12));

        var list = await Handler(memberships, emails).HandleAsync(
            new ListMembershipsQuery(TenantUnderTest, Search: " 0012 "), TestContext.Current.CancellationToken);

        Assert.Equal(12, Assert.Single(list.Items).AdvisorCode);
    }

    // La búsqueda por código se suma a la de correo y nombre, no la reemplaza.
    [Fact]
    public async Task TextSearchStillMatchesByNameAndEmail()
    {
        var (memberships, emails) = Roster(
            ("ana@example.com", "Ana Pérez", 12),
            ("carlos@example.com", "Carlos Mejía", null));

        var byName = await Handler(memberships, emails).HandleAsync(
            new ListMembershipsQuery(TenantUnderTest, Search: "mejía"), TestContext.Current.CancellationToken);
        var byEmail = await Handler(memberships, emails).HandleAsync(
            new ListMembershipsQuery(TenantUnderTest, Search: "ana@"), TestContext.Current.CancellationToken);

        Assert.Equal("carlos@example.com", Assert.Single(byName.Items).Email);
        Assert.Equal("ana@example.com", Assert.Single(byEmail.Items).Email);
    }

    private static ListMembershipsHandler Handler(
        IReadOnlyList<Membership> memberships,
        IReadOnlyDictionary<Guid, string> emails) =>
        new(
            new InMemoryMembershipRepository([.. memberships]),
            new InMemoryTenantRepository(Tenant.Create(
                TenantUnderTest, "acme", "Acme", "es-CO", "America/Bogota", "yyyy-MM-dd",
                MembershipId.New(), Now)),
            new StubUserDirectory(emails),
            new RosterReadExecutionContext(TenantUnderTest),
            new FixedClock(Now));

    private static (IReadOnlyList<Membership> Memberships, IReadOnlyDictionary<Guid, string> Emails)
        Roster(params (string Email, string DisplayName, int? AdvisorCode)[] people)
    {
        var memberships = new List<Membership>();
        var emails = new Dictionary<Guid, string>();
        foreach (var (email, displayName, advisorCode) in people)
        {
            var userId = Guid.CreateVersion7();
            emails[userId] = email;
            memberships.Add(Membership.Invite(
                MembershipId.New(),
                userId,
                TenantUnderTest,
                displayName,
                ["advisor"],
                "invitation",
                $"token-{userId:N}",
                $"hash-{userId:N}",
                Now.AddHours(-1),
                Membership.DefaultInvitationTimeToLive,
                advisorCode));
        }

        return (memberships, emails);
    }
}
