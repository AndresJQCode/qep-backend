using Modules.Customers.Application;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-24 §3.2 y desvío F3: las reglas de los ids a fusionar, las mismas de la vista previa y de la fusión.
/// Todas fallan sobre <c>MergedCustomerIds</c>, la clave que el formulario sabe marcar.</summary>
public sealed class CustomerMergeValidatorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Survivor = Guid.CreateVersion7();

    public static TheoryData<string> InvalidIds() => ["null", "empty", "eleven", "guid-empty", "repeated", "survivor"];

    private static Guid[]? IdsFor(string label)
    {
        var one = Guid.CreateVersion7();
        return label switch
        {
            "null" => null,
            "empty" => [],
            "eleven" => Enumerable.Range(0, 11).Select(_ => Guid.CreateVersion7()).ToArray(),
            "guid-empty" => [Guid.Empty],
            "repeated" => [one, one],
            "survivor" => [Survivor],
            _ => throw new ArgumentOutOfRangeException(nameof(label)),
        };
    }

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public void InvalidMergedIdsAre422OnMergedCustomerIds(string label)
    {
        var result = new GetCustomerMergePreviewValidator().Validate(
            new GetCustomerMergePreviewQuery(Tenant, Survivor, IdsFor(label)));

        Assert.False(result.IsValid);
        Assert.All(result.Errors, error => Assert.Equal("MergedCustomerIds", error.PropertyName));
    }

    [Theory]
    [InlineData("eleven", "Elige entre 1 y 10 clientes para fusionar.")]
    [InlineData("repeated", "No repitas un cliente.")]
    [InlineData("survivor", "El cliente que queda no puede estar entre los que se fusionan.")]
    public void EachRuleSaysItsMessage(string label, string message)
    {
        var result = new GetCustomerMergePreviewValidator().Validate(
            new GetCustomerMergePreviewQuery(Tenant, Survivor, IdsFor(label)));

        Assert.Equal(message, Assert.Single(result.Errors).ErrorMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void OneToTenDistinctIdsPass(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).ToArray();

        var result = new GetCustomerMergePreviewValidator().Validate(new GetCustomerMergePreviewQuery(Tenant, Survivor, ids));

        Assert.True(result.IsValid);
    }
}
