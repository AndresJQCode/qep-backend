using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Fase 4a de Soporte (plan G8, Review Focus 5): el «quién soy» de <c>IsMe</c>. Fuera de una sesión (el worker del hilo, la consulta
/// pública, el radicado del formulario) no hay a quién marcar: el puerto responde <c>null</c> sin tocar el contexto de ejecución, que
/// lanzaría sin <c>HttpContext</c>.
/// </summary>
public sealed class SupportViewerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid User = Guid.Parse("01900000-0000-7000-8000-0000000000c2");
    private static readonly Guid Member = Guid.Parse("01900000-0000-7000-8000-0000000000c3");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItIsNullWithoutAnHttpContext()
    {
        var memberships = new RecordingMemberships();
        var viewer = new SupportViewer(new HttpContextAccessor(), new ThrowingExecutionContext(), memberships);

        Assert.Null(await viewer.MemberIdAsync(Tenant, Ct));
        Assert.Equal(0, memberships.Calls);
    }

    [Fact]
    public async Task ItIsNullWithoutAnAuthenticatedUser()
    {
        var memberships = new RecordingMemberships();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var viewer = new SupportViewer(accessor, new ThrowingExecutionContext(), memberships);

        Assert.Null(await viewer.MemberIdAsync(Tenant, Ct));
        Assert.Equal(0, memberships.Calls);
    }

    [Fact]
    public async Task AnAuthenticatedUserIsTheirActiveMembership()
    {
        var memberships = new RecordingMemberships();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", User.ToString())], authenticationType: "Test")),
        };
        var viewer = new SupportViewer(new HttpContextAccessor { HttpContext = context }, new FixedExecutionContext(User), memberships);

        Assert.Equal(Member, await viewer.MemberIdAsync(Tenant, Ct));
        Assert.Equal(1, memberships.Calls);
    }

    // Revisión del bloque A: autenticado pero sin sujeto (una clave de API, más adelante): no es miembro, y no lanza.
    [Fact]
    public async Task ItIsNullForAnAuthenticatedCallerWithoutASubject()
    {
        var memberships = new RecordingMemberships();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("api_key", "k1")], authenticationType: "ApiKey")),
        };
        var viewer = new SupportViewer(new HttpContextAccessor { HttpContext = context }, new ThrowingExecutionContext(), memberships);

        Assert.Null(await viewer.MemberIdAsync(Tenant, Ct));
        Assert.Equal(0, memberships.Calls);
    }

    private sealed class RecordingMemberships : IMembershipDirectory
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<Guid?>(userId == User && tenantId == Tenant ? Member : null);
        }

        public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingExecutionContext : IExecutionContext
    {
        public Guid SubjectId => throw new InvalidOperationException("No active HTTP execution context.");

        public TenantId TenantId => throw new InvalidOperationException("No active HTTP execution context.");

        public bool HasPermission(string permission) => throw new InvalidOperationException("No active HTTP execution context.");
    }

    private sealed class FixedExecutionContext(Guid subjectId) : IExecutionContext
    {
        public Guid SubjectId => subjectId;

        public TenantId TenantId => new(Tenant);

        public bool HasPermission(string permission) => false;
    }
}
