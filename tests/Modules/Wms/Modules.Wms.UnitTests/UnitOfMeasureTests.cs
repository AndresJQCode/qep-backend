using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.UnitTests;

public sealed class UnitOfMeasureTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public void TheSystemDefaultsAreTheSixOfTheSpec()
    {
        var defaults = UnitOfMeasure.SystemDefaults(Tenant, Now);

        Assert.Equal(
            new[] { ("UND", 0), ("KG", 3), ("G", 0), ("L", 3), ("ML", 0), ("M", 2) },
            defaults.Select(u => (u.Code.Value, u.Precision)).ToArray());
        Assert.All(defaults, u => Assert.True(u.IsSystem));
        Assert.All(defaults, u => Assert.Equal(Tenant, u.TenantId));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    public void PrecisionOutsideZeroToSixIsRejected(int precision)
    {
        var error = Assert.Throws<WmsDomainException>(() =>
            UnitOfMeasure.Create(Tenant, UomCode.Parse("CJ"), "Caja", precision, false, Now));

        Assert.Equal(WmsErrorCodes.UomPrecisionInvalid, error.Code);
    }

    [Fact]
    public void TheCodeIsNormalizedAndValidated()
    {
        Assert.Equal("UND", UomCode.Parse("und").Value);

        var error = Assert.Throws<WmsDomainException>(() => UomCode.Parse("U N"));
        Assert.Equal(WmsErrorCodes.UomCodeInvalid, error.Code);
    }
}
