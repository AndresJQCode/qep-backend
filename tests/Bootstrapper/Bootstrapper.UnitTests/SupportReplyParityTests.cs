using System.Reflection;
using Modules.Messaging.Application;
using Modules.Support.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Soporte no ve Mensajería, así que copia tres reglas suyas: el valor de un parámetro de plantilla de Meta, el tope de texto de Instagram
/// y los motivos con que el envío de Soporte rechaza una respuesta. Este proyecto ve los dos módulos: si una copia diverge, se pone rojo
/// acá y no en producción (un 422 de Soporte que Mensajería no habría dado, o un motivo nuevo que cae al código genérico).
/// </summary>
public sealed class SupportReplyParityTests
{
    public static TheoryData<string> TemplateValues => new()
    {
        string.Empty,
        "   ",
        "Ya quedó resuelto",
        "Línea uno\nLínea dos",
        "Con retorno\r",
        "Con\ttabulador",
        "Cuatro    espacios",
        "Cinco     espacios",
        new string('a', 1024),
        new string('a', 1025),
        "  Recortado al guardar  ",
        "Emoji 😀 y tildes: está, acción",
    };

    [Theory]
    [MemberData(nameof(TemplateValues))]
    public void TheTemplateRuleMatchesMessaging(string value) =>
        Assert.Equal(TemplateParameterRules.IsValid(value), SupportReplyTextRules.FitsTemplate(value));

    [Fact]
    public void ANullTemplateValueIsRejectedByBoth()
    {
        Assert.False(TemplateParameterRules.IsValid(null));
        Assert.False(SupportReplyTextRules.FitsTemplate(null));
    }

    [Fact]
    public void TheTemplateLimitsMatchMessaging()
    {
        Assert.Equal(TemplateParameterRules.MaxValueLength, SupportReplyTextRules.TemplateValueMaxLength);
        Assert.Equal(TemplateParameterRules.MaxConsecutiveSpaces, SupportReplyTextRules.MaxConsecutiveSpaces);
    }

    [Fact]
    public void TheInstagramTextLimitMatchesMessaging() =>
        Assert.Equal(SendMessageValidator.InstagramTextMaxLength, SupportReplyTextRules.InstagramTextMaxLength);

    // Tarea 11: el adjunto de una respuesta pasa por la sesión de subida y la confirmación de Mensajería. El nombre, el pie y el permiso
    // que pide la sesión son copias: si divergen, Soporte aceptaría un adjunto que Mensajería rechaza o la pantalla ofrecería uno que no
    // se puede subir.
    [Fact]
    public void TheAttachmentRulesMatchMessaging()
    {
        Assert.Equal(CreateMediaUploadSessionValidator.MaxFileNameLength, SupportReplyAttachmentRules.FileNameMaxLength);
        Assert.Equal(SendMediaMessageValidator.MaxCaptionLength, SupportReplyAttachmentRules.CaptionMaxLength);
        Assert.Equal(MessagingPermissions.ConversationManage, SupportReplyAttachmentRules.UploadPermission);
    }

    // Los códigos que sólo pone Soporte (Tarea 8a): CustomerNotFound lo pone el adaptador cuando abrir la conversación choca con un
    // cliente borrado; los demás son motivos de reply-options o del envío de apertura. Todo lo demás es copia de Mensajería.
    private static readonly string[] SupportOnlyCodes =
    [
        SupportChannelReasons.CustomerNotFound,
        SupportChannelReasons.NoConversation,
        SupportChannelReasons.MessagingDisabled,
        SupportChannelReasons.NoMobile,
        SupportChannelReasons.NoEmail,
        SupportChannelReasons.NoAuthor,
        SupportChannelReasons.PortalOnly,
        SupportChannelReasons.PortalUnavailable,
        SupportChannelReasons.FormUnpublished,
    ];

    [Fact]
    public void TheRejectionReasonsMatchMessaging()
    {
        var support = ConstantsOf(typeof(SupportChannelReasons)).Where(reason => !SupportOnlyCodes.Contains(reason));

        Assert.Equal(ConstantsOf(typeof(SupportReplyReasons)).Order(), support.Order());
    }

    private static IEnumerable<string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);
}
