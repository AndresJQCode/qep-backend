using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-11 §4.1: la etiqueta (nombre 1–40 recortado, color de una paleta fija, tope de 100 por
/// tenant) y la asignación a un cliente (tope de 20, sin repetidas).</summary>
public sealed class TagTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static Tag TagNamed(string name, TagColor color = TagColor.Blue) =>
        Tag.Create(TagId.New(), TenantId, name, color, Now);

    [Fact]
    public void CreateStartsAtVersionOne()
    {
        var tag = TagNamed("Mayoristas", TagColor.Green);

        Assert.Equal(TenantId, tag.TenantId);
        Assert.Equal("Mayoristas", tag.Name);
        Assert.Equal(TagColor.Green, tag.Color);
        Assert.Equal(1, tag.Version);
        Assert.Equal(Now, tag.CreatedAt);
        Assert.Equal(Now, tag.UpdatedAt);
    }

    [Fact]
    public void CreateTrimsTheName()
    {
        Assert.Equal("VIP", TagNamed("  VIP  ").Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankName(string name)
    {
        var error = Assert.Throws<CustomersDomainException>(() => TagNamed(name));

        Assert.Equal("customers.tag.name_required", error.Code);
    }

    [Fact]
    public void CreateAcceptsExactlyTheMaximumLength()
    {
        Assert.Equal(Tag.NameMaxLength, TagNamed(new string('a', Tag.NameMaxLength)).Name.Length);
    }

    [Fact]
    public void CreateRejectsANameOverTheMaximum()
    {
        var error = Assert.Throws<CustomersDomainException>(() => TagNamed(new string('a', Tag.NameMaxLength + 1)));

        Assert.Equal("customers.tag.name_too_long", error.Code);
    }

    [Fact]
    public void UpdateRenamesRecolorsAndBumpsTheVersion()
    {
        var tag = TagNamed("VIP");
        var later = Now.AddMinutes(5);

        tag.Update("Clientes VIP", TagColor.Purple, later);

        Assert.Equal("Clientes VIP", tag.Name);
        Assert.Equal(TagColor.Purple, tag.Color);
        Assert.Equal(2, tag.Version);
        Assert.Equal(later, tag.UpdatedAt);
    }

    [Fact]
    public void AFailedUpdateLeavesTheTagAsItWas()
    {
        var tag = TagNamed("VIP");

        Assert.Throws<CustomersDomainException>(() => tag.Update(" ", TagColor.Red, Now.AddMinutes(5)));

        Assert.Equal("VIP", tag.Name);
        Assert.Equal(TagColor.Blue, tag.Color);
        Assert.Equal(1, tag.Version);
    }

    [Theory]
    [InlineData("gray", TagColor.Gray)]
    [InlineData("red", TagColor.Red)]
    [InlineData("orange", TagColor.Orange)]
    [InlineData("yellow", TagColor.Yellow)]
    [InlineData("green", TagColor.Green)]
    [InlineData("teal", TagColor.Teal)]
    [InlineData("blue", TagColor.Blue)]
    [InlineData("purple", TagColor.Purple)]
    [InlineData("PURPLE", TagColor.Purple)]
    public void TheColorTravelsByName(string wire, TagColor expected)
    {
        Assert.Equal(expected, TagColorParser.Parse(wire));
        Assert.Equal(expected.ToString().ToLowerInvariant(), expected.ToWireValue());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("magenta")]
    [InlineData("7")]
    public void AnUnknownColorFailsInsteadOfFallingBackToADefault(string? wire)
    {
        var error = Assert.Throws<CustomersDomainException>(() => TagColorParser.Parse(wire));

        Assert.Equal("customers.tag.color_invalid", error.Code);
    }

    [Fact]
    public void TheTenantCanHoldOneHundredTags()
    {
        Tag.EnsureTenantCapacity(Tag.MaxPerTenant - 1);
    }

    [Fact]
    public void TheHundredAndFirstTagIsRejected()
    {
        var error = Assert.Throws<CustomersDomainException>(() => Tag.EnsureTenantCapacity(Tag.MaxPerTenant));

        Assert.Equal("customers.tag.limit_reached", error.Code);
    }

    [Fact]
    public void ACustomerCanHoldTwentyTags()
    {
        var ids = Enumerable.Range(0, CustomerTagAssignment.MaxPerCustomer).Select(_ => TagId.New()).ToArray();

        CustomerTagAssignment.EnsureValidSelection(ids);
    }

    [Fact]
    public void TheTwentyFirstTagOnACustomerIsRejected()
    {
        var ids = Enumerable.Range(0, CustomerTagAssignment.MaxPerCustomer + 1).Select(_ => TagId.New()).ToArray();

        var error = Assert.Throws<CustomersDomainException>(() => CustomerTagAssignment.EnsureValidSelection(ids));

        Assert.Equal("customers.customer.tag_limit_reached", error.Code);
    }

    [Fact]
    public void ARepeatedTagInTheSelectionIsRejected()
    {
        var id = TagId.New();

        var error = Assert.Throws<CustomersDomainException>(() => CustomerTagAssignment.EnsureValidSelection([id, id]));

        Assert.Equal("customers.customer.tag_duplicated", error.Code);
    }

    [Fact]
    public void AnAssignmentRemembersWhoAndWhen()
    {
        var customerId = CustomerId.New();
        var tagId = TagId.New();
        var user = Guid.CreateVersion7();

        var assignment = CustomerTagAssignment.Create(customerId, tagId, TenantId, user, Now);

        Assert.Equal(customerId, assignment.CustomerId);
        Assert.Equal(tagId, assignment.TagId);
        Assert.Equal(TenantId, assignment.TenantId);
        Assert.Equal(user, assignment.AssignedByUserId);
        Assert.Equal(Now, assignment.AssignedAt);
    }
}
