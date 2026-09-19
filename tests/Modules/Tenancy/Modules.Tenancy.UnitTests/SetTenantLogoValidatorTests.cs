using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class SetTenantLogoValidatorTests
{
    private readonly SetTenantLogoValidator _validator = new();

    private static readonly TenantId TenantId = new(Guid.CreateVersion7());

    [Fact]
    public void RejectsAnEmptyFileId()
    {
        var result = _validator.Validate(new SetTenantLogoCommand(TenantId, Guid.Empty, 1, "corr-1"));

        var failure = Assert.Single(result.Errors);
        Assert.Equal("FileId", failure.PropertyName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveExpectedVersion(long expectedVersion)
    {
        var result = _validator.Validate(
            new SetTenantLogoCommand(TenantId, Guid.CreateVersion7(), expectedVersion, "corr-1"));

        var failure = Assert.Single(result.Errors);
        Assert.Equal("ExpectedVersion", failure.PropertyName);
    }

    [Fact]
    public void AcceptsAValidCommand()
    {
        var result = _validator.Validate(
            new SetTenantLogoCommand(TenantId, Guid.CreateVersion7(), 1, "corr-1"));

        Assert.True(result.IsValid);
    }
}
