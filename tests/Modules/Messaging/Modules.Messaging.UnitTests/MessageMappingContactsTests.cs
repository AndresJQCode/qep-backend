using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Un <c>contacts</c> entrante guarda en <c>details</c> el arreglo <c>contacts</c> de Meta tal cual
/// (<c>WebhookPayloadParser</c>). Antes sólo viajaba el nombre en <c>text</c>; estas pruebas amarran que los
/// teléfonos y correos lleguen a la burbuja, y que un <c>details</c> roto deje <c>contacts</c> en <c>null</c> sin romper la página.</summary>
public sealed class MessageMappingContactsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly IReadOnlyDictionary<Guid, string> Names = new Dictionary<Guid, string>();
    private static readonly IReadOnlyDictionary<Guid, ReplyTargetRow> NoTargets = new Dictionary<Guid, ReplyTargetRow>();

    // La forma es la del ejemplo de la documentación del webhook de Meta (messages[].contacts): addresses, birthday,
    // emails, name, org, phones y urls; el segundo contacto trae sólo el nombre, como uno que el cliente guardó sin datos.
    private const string MetaContacts = """
        [
          {
            "addresses": [{ "city": "Medellín", "country": "Colombia", "country_code": "CO", "state": "Antioquia", "street": "Calle 10 # 43-12", "type": "WORK", "zip": "050021" }],
            "birthday": "1990-04-12",
            "emails": [{ "email": "laura.gomez@ferreteriaandina.co", "type": "WORK" }],
            "name": { "formatted_name": "Laura Gómez", "first_name": "Laura", "last_name": "Gómez" },
            "org": { "company": "Ferretería Andina", "department": "Compras", "title": "Jefe de compras" },
            "phones": [
              { "phone": "+57 300 123 4567", "wa_id": "573001234567", "type": "CELL" },
              { "phone": "+57 604 444 5566", "type": "WORK" }
            ],
            "urls": [{ "url": "https://ferreteriaandina.co", "type": "WORK" }]
          },
          {
            "name": { "formatted_name": "Pedro", "first_name": "Pedro" }
          }
        ]
        """;

    private static MessageRow Row(MessageKind kind, string? details, string? text = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), MessageDirection.Inbound, kind, text, null, details, MessageStatus.Delivered, null,
            DateTimeOffset.UnixEpoch, null, null, null, null);

    [Fact]
    public void AContactsMessageCarriesNamesOrganizationPhonesAndEmails()
    {
        var dto = MessageMapping.ToDto(Row(MessageKind.Contacts, MetaContacts, "Laura Gómez, Pedro"), TenantId, Names, NoTargets);

        Assert.NotNull(dto.Contacts);
        Assert.Equal(2, dto.Contacts.Count);

        var laura = dto.Contacts[0];
        Assert.Equal("Laura Gómez", laura.Name);
        Assert.Equal("Ferretería Andina", laura.Organization);
        Assert.Equal(
            new[]
            {
                new SharedContactPhoneDto("+57 300 123 4567", "573001234567", "CELL"),
                new SharedContactPhoneDto("+57 604 444 5566", null, "WORK"),
            },
            laura.Phones);
        Assert.Equal(new[] { new SharedContactEmailDto("laura.gomez@ferreteriaandina.co", "WORK") }, laura.Emails);

        var pedro = dto.Contacts[1];
        Assert.Equal("Pedro", pedro.Name);
        Assert.Null(pedro.Organization);
        Assert.Empty(pedro.Phones);
        Assert.Empty(pedro.Emails);
    }

    [Fact]
    public void PhonesWithoutNumberAndEmailsWithoutAddressAreSkipped()
    {
        const string details = """
            [{ "name": { "formatted_name": "Ana" },
               "phones": [{ "type": "HOME" }, { "wa_id": "573009876543", "type": "CELL" }],
               "emails": [{ "type": "HOME" }, { "email": "ana@correo.co" }] }]
            """;

        var contact = Assert.Single(MessageMapping.ToDto(Row(MessageKind.Contacts, details), TenantId, Names, NoTargets).Contacts!);

        Assert.Equal(new[] { new SharedContactPhoneDto(null, "573009876543", "CELL") }, contact.Phones);
        Assert.Equal(new[] { new SharedContactEmailDto("ana@correo.co", null) }, contact.Emails);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("""{ "name": { "formatted_name": "Ana" } }""")]
    public void MalformedDetailsLeaveContactsNull(string? details) =>
        Assert.Null(MessageMapping.ToDto(Row(MessageKind.Contacts, details, "Ana"), TenantId, Names, NoTargets).Contacts);

    [Fact]
    public void AMessageThatIsNotContactsHasNoContacts() =>
        Assert.Null(MessageMapping.ToDto(Row(MessageKind.Text, MetaContacts, "hola"), TenantId, Names, NoTargets).Contacts);
}
