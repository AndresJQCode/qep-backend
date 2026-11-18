using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// Ruling W11 (b): la consulta que decide qué tenants puede vincular un usuario como distribuidor ata la membresía owner a
/// <b>ese</b> tenant, no sólo al id de <c>owner_membership_id</c> (que no tiene FK). Se mira el SQL sin abrir una base, igual
/// que <see cref="TenancyDbContextMappingTests"/>; la prueba con Postgres es <c>TenantOwnershipQueryTests</c> (integración).
/// </summary>
public sealed class TenantOwnershipQuerySqlTests
{
    [Fact]
    public void TheOwnerMembershipMustBelongToTheSameTenant()
    {
        using var context = new TenancyDbContextFactory().CreateDbContext([]);

        var sql = TenantOwnershipQuery.Owned(context, Guid.CreateVersion7()).ToQueryString();

        Assert.Contains("owner_membership_id", sql, StringComparison.Ordinal);
        Assert.Contains(".tenant_id", sql, StringComparison.Ordinal);
    }
}
