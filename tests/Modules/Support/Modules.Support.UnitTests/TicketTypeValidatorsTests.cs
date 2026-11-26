using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>
/// Los validadores de alta y edición de un tipo cubren cada guarda de <c>SupportTicketType</c> (M5): un cuerpo que pasa el validador
/// nunca llega al dominio a lanzar un <c>ArgumentException</c> que saldría como 500.
/// </summary>
public sealed class TicketTypeValidatorsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static CreateTicketTypeCommand Create(string? name = "Garantía", int? term = 15, bool? legal = true) =>
        CreateTicketTypeCommand.From(Tenant, new TicketTypeRequest(name, term, legal, true, true, null));

    private static UpdateTicketTypeCommand Update(string? name = "Garantía", int? term = 15, bool? legal = true) =>
        UpdateTicketTypeCommand.From(Tenant, Guid.CreateVersion7(), new TicketTypeRequest(name, term, legal, true, true, true), 1);

    [Fact]
    public void AValidTypePasses()
    {
        Assert.True(new CreateTicketTypeValidator().Validate(Create()).IsValid);
        Assert.True(new UpdateTicketTypeValidator().Validate(Update()).IsValid);
        Assert.True(new CreateTicketTypeValidator().Validate(Create(term: null, legal: false)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankNameFails(string? name)
    {
        Assert.Equal(["Name"], new CreateTicketTypeValidator().Validate(Create(name: name)).Errors.Select(error => error.PropertyName));
        Assert.Equal(["Name"], new UpdateTicketTypeValidator().Validate(Update(name: name)).Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void ANameLongerThan80Fails()
    {
        var name = new string('a', 81);

        Assert.Equal(["Name"], new CreateTicketTypeValidator().Validate(Create(name: name)).Errors.Select(error => error.PropertyName));
        Assert.Equal(["Name"], new UpdateTicketTypeValidator().Validate(Update(name: name)).Errors.Select(error => error.PropertyName));
        // Se mide recortado, como lo guarda el dominio.
        Assert.True(new CreateTicketTypeValidator().Validate(Create(name: $"  {new string('a', 80)}  ")).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void ATermOf0Or366Fails(int term)
    {
        Assert.Equal(
            ["TermBusinessDays"],
            new CreateTicketTypeValidator().Validate(Create(term: term)).Errors.Select(error => error.PropertyName));
        Assert.Equal(
            ["TermBusinessDays"],
            new UpdateTicketTypeValidator().Validate(Update(term: term)).Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void LegalWithoutTermFailsOnTermIsLegal()
    {
        Assert.Equal(
            ["TermIsLegal"],
            new CreateTicketTypeValidator().Validate(Create(term: null, legal: true)).Errors.Select(error => error.PropertyName));
        Assert.Equal(
            ["TermIsLegal"],
            new UpdateTicketTypeValidator().Validate(Update(term: null, legal: true)).Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void MissingFlagsFailOnTheirFields()
    {
        var create = CreateTicketTypeCommand.From(Tenant, new TicketTypeRequest("Garantía", 15, null, null, null, null));
        var update = UpdateTicketTypeCommand.From(Tenant, Guid.CreateVersion7(), new TicketTypeRequest("Garantía", 15, null, null, null, null), 0);

        Assert.Equal(
            ["TermIsLegal", "RequiresFormalResponse", "IsPublished"],
            new CreateTicketTypeValidator().Validate(create).Errors.Select(error => error.PropertyName));
        Assert.Equal(
            ["TermIsLegal", "RequiresFormalResponse", "IsPublished", "IsActive", "ExpectedVersion"],
            new UpdateTicketTypeValidator().Validate(update).Errors.Select(error => error.PropertyName));
    }
}
