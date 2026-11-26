using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class CustomerFieldPolicyTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void SavingImpliesBilling(bool onSave, bool onBilling, bool expected) =>
        Assert.Equal(expected, new FieldRequirement(onSave, onBilling).IsConsistent);

    [Fact]
    public void TheDefaultRequiresEverythingInBothMoments() =>
        Assert.Equal(new CustomerFieldPolicy(new(true, true), new(true, true)), CustomerFieldPolicy.Default);

    [Fact]
    public void ThePolicyIsConsistentOnlyWhenBothFieldsAre()
    {
        Assert.True(CustomerFieldPolicy.Default.IsConsistent);
        Assert.False(new CustomerFieldPolicy(new(true, false), FieldRequirement.Always).IsConsistent);
        Assert.False(new CustomerFieldPolicy(FieldRequirement.Always, new(true, false)).IsConsistent);
    }
}
