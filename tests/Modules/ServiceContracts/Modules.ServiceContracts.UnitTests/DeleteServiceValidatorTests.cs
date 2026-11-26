using Modules.ServiceContracts.Application;

namespace Modules.ServiceContracts.UnitTests;

public sealed class DeleteServiceValidatorTests
{
    private static bool Fails(long version) =>
        !new DeleteServiceValidator().Validate(new DeleteServiceCommand(Guid.NewGuid(), Guid.NewGuid(), version)).IsValid;

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public void TheExpectedVersionMustBePositive(long version, bool rejected) =>
        Assert.Equal(rejected, Fails(version));
}
