using BuildingBlocks.Application;
using Modules.Geography.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Geography.UnitTests;

/// <summary>
/// Spec 2026-10-26 §6: la segunda capa de los endpoints de operador de ciudades es la de Tenancy
/// (<see cref="OperatorAuthorization"/>), con los permisos de <see cref="GeographyOperatorPermissions"/>. El tenant de la ruta es
/// el del claim, el permiso está en los claims y ese tenant es el operador (<c>Platform:OperatorTenantId</c>, por
/// <see cref="IOperatorTenant"/>). Cualquier falla: 403 <c>authorization.denied</c>.
/// </summary>
public sealed class GeographyOperatorAuthorizationTests
{
    private static readonly TenantId Operator = TenantId.New();

    [Fact]
    public void AnotherTenantInTheRouteIs403()
    {
        var context = new Context(Operator, GeographyOperatorPermissions.CitiesManage);

        AssertDenied(() => OperatorAuthorization.EnsureAuthorized(
            context, TenantId.New(), GeographyOperatorPermissions.CitiesManage, new FixedOperatorTenant(Operator)));
    }

    [Fact]
    public void WithoutThePermissionIs403()
    {
        var context = new Context(Operator, GeographyOperatorPermissions.CitiesRead);

        AssertDenied(() => OperatorAuthorization.EnsureAuthorized(
            context, Operator, GeographyOperatorPermissions.CitiesManage, new FixedOperatorTenant(Operator)));
    }

    [Fact]
    public void ANonOperatorTenantIs403EvenWithThePermission()
    {
        var tenant = TenantId.New();
        var context = new Context(tenant, GeographyOperatorPermissions.CitiesRead);

        AssertDenied(() => OperatorAuthorization.EnsureAuthorized(
            context, tenant, GeographyOperatorPermissions.CitiesRead, new FixedOperatorTenant(Operator)));
    }

    [Fact]
    public void TheOperatorWithThePermissionPasses()
    {
        var context = new Context(Operator, GeographyOperatorPermissions.CitiesRead, GeographyOperatorPermissions.CitiesManage);

        OperatorAuthorization.EnsureAuthorized(
            context, Operator, GeographyOperatorPermissions.CitiesRead, new FixedOperatorTenant(Operator));
        OperatorAuthorization.EnsureAuthorized(
            context, Operator, GeographyOperatorPermissions.CitiesManage, new FixedOperatorTenant(Operator));
    }

    private static void AssertDenied(Action authorize)
    {
        var error = Assert.Throws<RequestForbiddenException>(authorize);
        Assert.Equal("authorization.denied", error.Code);
    }

    private sealed class FixedOperatorTenant(TenantId operatorTenantId) : IOperatorTenant
    {
        public bool IsOperator(Guid tenantId) => operatorTenantId.Value == tenantId;
    }

    private sealed class Context(TenantId tenantId, params string[] permissions) : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId => tenantId;

        public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
    }
}
