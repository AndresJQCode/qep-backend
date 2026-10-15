using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.9: una fila del espejo al DTO de la pantalla. <c>createdBy</c> sale del diccionario de nombres en una
/// <c>Free</c> y es <c>null</c> en una <c>External</c>; el texto, los parámetros y <c>sendable</c> salen de los <c>components</c>.</summary>
public sealed class ManagedTemplateMappingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    private const string Components = """
        [{"type":"HEADER","format":"TEXT","text":"Oferta"},
         {"type":"BODY","text":"Hola {{first_name}}, {{product}} con descuento"},
         {"type":"FOOTER","text":"QEP"}]
        """;

    [Fact]
    public void AFreeRowResolvesCreatedByFromTheNamesAndReadsTheTextFromComponents()
    {
        var member = Guid.CreateVersion7();
        var row = MessageTemplate.Pending(TenantId, ConnectionId, "900", "promo", "es", "MARKETING", "NAMED", "PENDING", Components, MessageTemplateKind.Free, member, Now);

        var dto = ManagedTemplateMapping.ToDto(row, new Dictionary<Guid, string> { [member] = "Laura Gómez" });

        Assert.Equal(new MemberRefDto(member, "Laura Gómez"), dto.CreatedBy);
        Assert.Equal(("Free", "PENDING", "900", "promo"), (dto.Kind, dto.Status, dto.MetaTemplateId, dto.Name));
        Assert.Equal(("Oferta", "Hola {{first_name}}, {{product}} con descuento", "QEP"), (dto.Header, dto.Body, dto.Footer));
        Assert.Equal(["first_name", "product"], dto.Parameters);
        Assert.Equal(TemplateCatalog.Named, dto.ParameterFormat);
        Assert.True(dto.Sendable);
        Assert.Null(dto.UnsupportedReason);
    }

    [Fact]
    public void AFreeRowWhoseCreatorLeftShowsTheDeletedMemberName()
    {
        var member = Guid.CreateVersion7();
        var row = MessageTemplate.Pending(TenantId, ConnectionId, "900", "promo", "es", "MARKETING", "NAMED", "PENDING", Components, MessageTemplateKind.Free, member, Now);

        var dto = ManagedTemplateMapping.ToDto(row, new Dictionary<Guid, string>());

        Assert.Equal(MessageMapping.DeletedMemberName, dto.CreatedBy!.DisplayName);
    }

    [Fact]
    public void AnExternalRowHasNoCreatorAndKeepsItsRejectionReason()
    {
        var row = MessageTemplate.Mirrored(TenantId, ConnectionId, "901", "externa", "es", "UTILITY", "POSITIONAL", "REJECTED", "INVALID_FORMAT", Components, Now);

        var dto = ManagedTemplateMapping.ToDto(row, new Dictionary<Guid, string>());

        Assert.Null(dto.CreatedBy);
        Assert.Equal(("External", "REJECTED", "INVALID_FORMAT"), (dto.Kind, dto.Status, dto.StatusReason));
    }

    [Fact]
    public void ADocumentHeaderIsNotSendableAndSaysWhy()
    {
        var row = MessageTemplate.Mirrored(TenantId, ConnectionId, "902", "qep_cotizacion", "es", "UTILITY", "NAMED", "APPROVED", null,
            """[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}"}]""", Now);

        var dto = ManagedTemplateMapping.ToDto(row, new Dictionary<Guid, string>());

        Assert.False(dto.Sendable);
        Assert.Equal(TemplateUnsupportedReasons.MediaHeader, dto.UnsupportedReason);
        Assert.Null(dto.Header);
    }
}
