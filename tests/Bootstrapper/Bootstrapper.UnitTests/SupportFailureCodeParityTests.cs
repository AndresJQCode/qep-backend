using System.Reflection;
using Modules.Messaging.Application;
using Modules.Support.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Tarea 8a: un código de falla nuevo de Soporte va a <see cref="SupportChannelReasons"/> y, si sale de Mensajería, también a su copia en
/// <see cref="SupportReplyReasons"/> (Soporte no ve Mensajería). Este proyecto ve los dos módulos: un motivo de Mensajería que no está en
/// el vocabulario de Soporte llegaría a la pantalla sin traducción.
/// </summary>
public sealed class SupportFailureCodeParityTests
{
    [Fact]
    public void EveryMessagingSupportReplyCodeIsInTheSupportVocabulary() =>
        Assert.All(ConstantsOf(typeof(SupportReplyReasons)), code => Assert.Contains(code, SupportChannelReasons.All));

    [Fact]
    public void TheVocabularyHasNoDuplicateValues()
    {
        var constants = ConstantsOf(typeof(SupportChannelReasons)).ToList();

        Assert.Equal(constants.Count, constants.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(constants.Count, SupportChannelReasons.All.Count);
    }

    private static IEnumerable<string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);
}
