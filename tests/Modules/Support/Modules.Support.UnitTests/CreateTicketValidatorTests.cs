using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>
/// El validador del alta manual cubre cada guarda del dominio (asunto, descripción) y cada columna del solicitante (M5): un cuerpo
/// que lo pasa nunca llega a <c>SupportTicket.CreateManual</c> a lanzar un <c>ArgumentException</c> ni revienta un INSERT por largo.
/// El tipo de documento se juzga con el país del solicitante o, sin él, con el del tenant (plan D7).
/// </summary>
public sealed class CreateTicketValidatorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static readonly TicketRequesterRequest Ana =
        new("Ana", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, null, "Calle 1 # 2-3");

    private static CreateTicketCommand Command(
        string? subject = "No me llegó la factura",
        string? description = "Pedí la factura y no llegó.",
        string? medium = "Phone",
        string? channel = "Email",
        TicketRequesterRequest? requester = null,
        string tenantCountry = "CO") =>
        CreateTicketCommand.From(
            Tenant,
            new CreateTicketRequest(Guid.CreateVersion7(), subject, description, medium, channel, null, requester ?? Ana, null))
        with
        { TenantCountry = tenantCountry };

    private static List<string> Errors(CreateTicketCommand command) =>
        new CreateTicketValidator().Validate(command).Errors.Select(error => error.PropertyName).ToList();

    [Fact]
    public void AValidTicketPasses()
    {
        Assert.Empty(Errors(Command()));
        Assert.Empty(Errors(Command(requester: Ana with { DocumentType = "NIE", CountryCode = "es" })));
    }

    // Spec 2026-11-22 D5: un ticket externo toma su fecha del término del tipo; mandar dueOn es un error del campo, no se ignora.
    [Fact]
    public void ADueDateIsNotPartOfAnExternalTicket()
    {
        var withDueOn = Command() with { DueOn = "2026-11-30" };

        Assert.Equal(["DueOn"], Errors(withDueOn));
        Assert.Empty(Errors(Command() with { DueOn = null }));
    }

    // Spec 2026-11-22 D3: None es el canal de una tarea y comparte el enum con los de verdad; ningún alta lo elige.
    [Fact]
    public void NoneIsNotAReplyChannel() =>
        Assert.Equal(["ReplyChannel"], Errors(Command(channel: "None")));

    [Fact]
    public void NieIsNotAllowedForColombia()
    {
        Assert.Equal(["Requester.DocumentType"], Errors(Command(requester: Ana with { DocumentType = "NIE", CountryCode = "CO" })));
        // Sin país del solicitante manda el del tenant.
        Assert.Equal(["Requester.DocumentType"], Errors(Command(requester: Ana with { DocumentType = "NIE" })));
        Assert.Empty(Errors(Command(requester: Ana with { DocumentType = "NIE" }, tenantCountry: "ES")));
    }

    [Theory]
    [InlineData("NIT")]
    [InlineData("CC")]
    [InlineData("CE")]
    public void ANumericTypeWithLettersFailsOnTheNumber(string type) =>
        Assert.Equal(
            ["Requester.DocumentNumber"],
            Errors(Command(requester: Ana with { DocumentType = type, DocumentNumber = "10203A4050" })));

    [Fact]
    public void APassportWithLettersIsAccepted() =>
        Assert.Empty(Errors(Command(requester: Ana with { DocumentType = "PASAPORTE", DocumentNumber = "AB123456" })));

    [Fact]
    public void ADocumentNumberWithoutTypeFails()
    {
        Assert.Equal(["Requester.DocumentType"], Errors(Command(requester: Ana with { DocumentType = null })));
        Assert.Equal(["Requester.DocumentNumber"], Errors(Command(requester: Ana with { DocumentNumber = "  " })));
    }

    [Fact]
    public void AMobileWithoutPlusFails()
    {
        Assert.Equal(["Requester.MobilePhone"], Errors(Command(requester: Ana with { MobilePhone = "3001234567" })));
        Assert.Equal(["Requester.MobilePhone"], Errors(Command(requester: Ana with { MobilePhone = "+0300123" })));
    }

    // Revisión final, m4: el celular se juzga recortado, como lo guarda el normalizador y como se miden los demás campos.
    [Fact]
    public void AMobileWithSurroundingSpacesPasses() =>
        Assert.Empty(Errors(Command(requester: Ana with { MobilePhone = " +573001234567 " })));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptySubjectFails(string? subject)
    {
        Assert.Equal(["Subject"], Errors(Command(subject: subject)));
    }

    [Fact]
    public void TextLongerThanItsColumnFails()
    {
        Assert.Equal(["Subject"], Errors(Command(subject: new string('a', 201))));
        Assert.Equal(["Description"], Errors(Command(description: new string('a', 5001))));
        Assert.Equal(["Description"], Errors(Command(description: " ")));
        Assert.Equal(
            ["Requester.FirstNames", "Requester.LastNames", "Requester.BusinessName", "Requester.DocumentNumber", "Requester.Email",
             "Requester.LandlinePhone", "Requester.Address"],
            Errors(Command(requester: Ana with
            {
                FirstNames = new string('a', 161),
                LastNames = new string('a', 161),
                BusinessName = new string('a', 161),
                DocumentNumber = new string('1', 33),
                Email = new string('a', 250) + "@x.co",
                LandlinePhone = new string('1', 33),
                Address = new string('a', 201),
            })));
    }

    [Fact]
    public void UnknownEnumsAndCountryFail()
    {
        Assert.Equal(["ManualMedium"], Errors(Command(medium: "Fax")));
        Assert.Equal(["ManualMedium"], Errors(Command(medium: "phone")));
        Assert.Equal(["ReplyChannel"], Errors(Command(channel: "Sms")));
        Assert.Equal(["Requester.Email"], Errors(Command(requester: Ana with { Email = "ana-at-example" })));
        Assert.Equal(["Requester.CountryCode"], Errors(Command(requester: Ana with { CountryCode = "COL" })));
    }

    // Fase 4a (plan G1): el anónimo se responde sólo por la página, sin cliente ni foto.
    [Fact]
    public void AnonymousNeedsPortalAndNoCustomerNorRequester()
    {
        CreateTicketCommand Anonymous(string channel, Guid? customerId, TicketRequesterRequest? requester) =>
            CreateTicketCommand.From(
                Tenant,
                new CreateTicketRequest(
                    Guid.CreateVersion7(), "Asunto", "Descripción", "Phone", channel, customerId, requester, null, Anonymous: true))
            with
            { TenantCountry = "CO" };

        Assert.Empty(Errors(Anonymous("Portal", null, null)));
        Assert.Equal(["ReplyChannel"], Errors(Anonymous("Email", null, null)));
        Assert.Equal(["CustomerId"], Errors(Anonymous("Portal", Guid.CreateVersion7(), null)));
        Assert.Equal(["Requester"], Errors(Anonymous("Portal", null, Ana)));
    }

    [Fact]
    public void PortalWithoutAnonymousIsRejected()
    {
        Assert.Equal(["ReplyChannel"], Errors(Command(channel: "Portal")));
    }

    [Fact]
    public void AMissingTypeFails()
    {
        var command = CreateTicketCommand.From(
            Tenant, new CreateTicketRequest(null, "Asunto", "Descripción", "Phone", "Email", null, Ana, null)) with
        { TenantCountry = "CO" };

        Assert.Equal(["TicketTypeId"], Errors(command));
    }
}
