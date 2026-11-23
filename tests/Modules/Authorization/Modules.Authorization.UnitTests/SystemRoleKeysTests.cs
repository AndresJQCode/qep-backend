using Modules.Authorization.Domain;

namespace Modules.Authorization.UnitTests;

public sealed class SystemRoleKeysTests
{
    // Spec 2026-11-21 (Agenda), D-13: un rol custom llamado como un rol de sistema dejaría el catálogo con dos definiciones.
    [Theory]
    [InlineData("scheduling_manager")]
    [InlineData("scheduling_provider")]
    [InlineData("cashier")]
    public void ASystemRoleKeyIsReserved(string key) => Assert.True(SystemRoleKeys.IsReserved(key));

    [Fact]
    public void TheSchedulingRolesHaveTheirConstants()
    {
        Assert.Equal("scheduling_manager", SystemRoleKeys.SchedulingManager);
        Assert.Equal("scheduling_provider", SystemRoleKeys.SchedulingProvider);
    }

    [Fact]
    public void EveryKeyIsListedOnce() => Assert.Equal(SystemRoleKeys.All.Count, SystemRoleKeys.All.Distinct().Count());
}
