using Modules.Audit.Infrastructure;

namespace Modules.Audit.UnitTests;

public sealed class AuditOptionsValidatorTests
{
    private readonly AuditOptionsValidator _validator = new();

    [Fact]
    public void DefaultsAreValid()
    {
        var result = _validator.Validate(name: null, new AuditOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0, 730)]
    [InlineData(2555, 0)]
    [InlineData(-1, -1)]
    public void NonPositiveRetentionFails(int security, int operational)
    {
        var options = new AuditOptions
        {
            SecurityRetentionDays = security,
            OperationalRetentionDays = operational,
        };

        var result = _validator.Validate(name: null, options);

        Assert.True(result.Failed);
    }
}
