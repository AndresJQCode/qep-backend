using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

public sealed class SupportTicketTypeTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheDefaultsAreTheEightOfTheSpecInOrder()
    {
        var types = SupportTicketTypeDefaults.For(TenantId, Now);

        Assert.Equal(
            [
                "Petición general", "Petición de documentos o información", "Consulta", "Queja", "Reclamo", "Sugerencia",
                "Felicitación", "Soporte técnico",
            ],
            types.Select(type => type.Name));
        Assert.Equal([15, 10, 30, 15, 15, 15, 15, null], types.Select(type => type.TermBusinessDays));
        Assert.Equal([true, true, true, true, true, true, true, false], types.Select(type => type.TermIsLegal));
        Assert.Equal([true, true, true, true, true, false, false, false], types.Select(type => type.RequiresFormalResponse));
        Assert.All(types, type =>
        {
            Assert.True(type.IsPublished);
            Assert.True(type.IsActive);
            Assert.Equal(TenantId, type.TenantId);
            Assert.Equal(1, type.Version);
            Assert.Equal(Now, type.CreatedAt);
            Assert.Equal(Now, type.UpdatedAt);
        });
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], types.Select(type => type.Position));
        Assert.Equal(8, types.Select(type => type.Id).Distinct().Count());
    }

    [Fact]
    public void ATypeWithoutTermCannotBeLegal() =>
        Assert.Throws<ArgumentException>(() => SupportTicketType.Create(
            SupportTicketTypeId.New(), TenantId, "Garantía", termBusinessDays: null, termIsLegal: true,
            requiresFormalResponse: false, isPublished: true, position: 9, Now));

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void TheTermIsBetweenOneAnd365BusinessDays(int term) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportTicketType.Create(
            SupportTicketTypeId.New(), TenantId, "Garantía", term, termIsLegal: false,
            requiresFormalResponse: false, isPublished: true, position: 9, Now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ATypeNeedsAName(string name) =>
        Assert.Throws<ArgumentException>(() => SupportTicketType.Create(
            SupportTicketTypeId.New(), TenantId, name, 15, termIsLegal: false,
            requiresFormalResponse: false, isPublished: true, position: 9, Now));

    [Fact]
    public void TheNameIsTrimmedAndAtMost80Characters()
    {
        var type = SupportTicketType.Create(
            SupportTicketTypeId.New(), TenantId, "  Garantía  ", 20, termIsLegal: false,
            requiresFormalResponse: true, isPublished: false, position: 9, Now);

        Assert.Equal("Garantía", type.Name);
        Assert.Equal(20, type.TermBusinessDays);
        Assert.False(type.IsPublished);
        Assert.True(type.IsActive);
        Assert.Throws<ArgumentException>(() => SupportTicketType.Create(
            SupportTicketTypeId.New(), TenantId, new string('a', 81), 20, termIsLegal: false,
            requiresFormalResponse: false, isPublished: true, position: 9, Now));
    }

    [Fact]
    public void UpdatingChangesEveryFieldAndBumpsTheVersion()
    {
        var type = SupportTicketTypeDefaults.For(TenantId, Now)[3];
        var later = Now.AddHours(1);

        type.Update("Queja formal", 20, termIsLegal: true, requiresFormalResponse: false, isPublished: false, isActive: false, later);

        Assert.Equal("Queja formal", type.Name);
        Assert.Equal(20, type.TermBusinessDays);
        Assert.True(type.TermIsLegal);
        Assert.False(type.RequiresFormalResponse);
        Assert.False(type.IsPublished);
        Assert.False(type.IsActive);
        Assert.Equal(later, type.UpdatedAt);
        Assert.Equal(2, type.Version);
    }

    [Fact]
    public void UpdatingWithTheSameValuesIsANoOp()
    {
        var type = SupportTicketTypeDefaults.For(TenantId, Now)[3];

        type.Update("Queja", 15, termIsLegal: true, requiresFormalResponse: true, isPublished: true, isActive: true, Now.AddHours(1));

        Assert.Equal(1, type.Version);
        Assert.Equal(Now, type.UpdatedAt);
    }

    [Fact]
    public void UpdatingToNoTermWhileLegalIsRejected()
    {
        var type = SupportTicketTypeDefaults.For(TenantId, Now)[3];

        Assert.Throws<ArgumentException>(() =>
            type.Update("Queja", null, termIsLegal: true, requiresFormalResponse: true, isPublished: true, isActive: true, Now));
        Assert.Equal(15, type.TermBusinessDays);
        Assert.Equal(1, type.Version);
    }

    [Fact]
    public void MovingChangesThePositionOnlyWhenItDiffers()
    {
        var type = SupportTicketTypeDefaults.For(TenantId, Now)[0];

        type.MoveTo(1, Now.AddHours(1));
        Assert.Equal(1, type.Version);

        type.MoveTo(5, Now.AddHours(2));
        Assert.Equal(5, type.Position);
        Assert.Equal(2, type.Version);
        Assert.Equal(Now.AddHours(2), type.UpdatedAt);
    }
}
