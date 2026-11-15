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

    // CustomerNotFound es sólo de Soporte: lo pone el adaptador cuando abrir la conversación choca con un cliente borrado.
    [Fact]
    public void TheRejectionReasonsMatchMessaging()
    {
        var support = ConstantsOf(typeof(SupportChannelReasons)).Where(reason => reason != SupportChannelReasons.CustomerNotFound);

        Assert.Equal(ConstantsOf(typeof(SupportReplyReasons)).Order(), support.Order());
    }

    private static IEnumerable<string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);
}
