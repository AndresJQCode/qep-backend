using Modules.Authorization.Application;

namespace Modules.Authorization.UnitTests;

/// <summary>Spec 2026-10-08 §2: fuera del tenant operador, todo <c>operator.*</c> se descarta.</summary>
public sealed class OperatorPermissionFilterTests
{
    private static readonly string[] Mixed =
        ["tenancy.settings.read", "operator.tenants.read", "operator.modules.manage", "operators.fake", "x.operator.y"];

    [Fact]
    public void OutsideTheOperatorTenantEveryOperatorPermissionIsDropped() =>
        Assert.Equal(
            ["tenancy.settings.read", "operators.fake", "x.operator.y"],
            OperatorPermissionFilter.Apply(Mixed, isOperatorTenant: false));

    [Fact]
    public void InsideTheOperatorTenantNothingIsDropped() =>
        Assert.Equal(Mixed, OperatorPermissionFilter.Apply(Mixed, isOperatorTenant: true));

    // pos.* tiene que pasar intacto: el filtro sólo mira el prefijo operator.
    [Fact]
    public void PosPermissionsAreNotTouched() =>
        Assert.Equal(
            ["pos.sale.read", "pos.register.operate"],
            OperatorPermissionFilter.Apply(["pos.sale.read", "pos.register.operate"], isOperatorTenant: false));

    [Theory]
    [InlineData("operator.tenants.read", true)]
    [InlineData("operator.", true)]
    [InlineData("Operator.tenants.read", false)]
    [InlineData("operators.fake", false)]
    public void OnlyTheExactPrefixCounts(string permission, bool expected) =>
        Assert.Equal(expected, OperatorPermissionFilter.IsOperatorPermission(permission));
}
