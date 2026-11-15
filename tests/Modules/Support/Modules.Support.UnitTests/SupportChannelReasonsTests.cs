using System.Reflection;
using System.Text.RegularExpressions;
using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>
/// Tarea 8a: <see cref="SupportChannelReasons"/> es el único vocabulario de los códigos que la pantalla lee de Soporte
/// (<c>ticket_replies.failure_code</c>, <c>ticket_access.last_failure_code</c> y el <c>reason</c> de <c>reply-options</c>). El frontend
/// traduce por esta lista.
/// </summary>
public sealed class SupportChannelReasonsTests
{
    // Los motivos que reply-options puede devolver (GetReplyOptionsHandler y SupportReplyRules.ThreadAsync), escritos por su valor en el
    // cable: si alguno cambia de texto o sale del vocabulario, la pantalla deja de traducirlo.
    public static TheoryData<string> ReplyOptionsReasons => new()
    {
        "no_conversation",
        "messaging_disabled",
        "window_closed",
        "contact_blocked",
        "template_not_approved",
        "no_mobile",
        "no_email",
        "connection_unavailable",
        "portal_only",
        "portal_unavailable",
    };

    [Fact]
    public void EveryCodeIsSnakeCaseAscii()
    {
        Assert.NotEmpty(SupportChannelReasons.All);
        Assert.All(SupportChannelReasons.All, code => Assert.Matches(new Regex("^[a-z]+(_[a-z]+)*$"), code));
    }

    [Theory]
    [MemberData(nameof(ReplyOptionsReasons))]
    public void ReplyOptionsReasonsAreInTheVocabulary(string reason) =>
        Assert.Contains(reason, SupportChannelReasons.All);

    [Fact]
    public void TheOpeningCodesAreInTheVocabulary()
    {
        Assert.Contains(SupportChannelReasons.NoAuthor, SupportChannelReasons.All);
        Assert.Equal("no_author", SupportChannelReasons.NoAuthor);
        Assert.Contains(SupportChannelReasons.FormUnpublished, SupportChannelReasons.All);
        Assert.Equal("form_unpublished", SupportChannelReasons.FormUnpublished);
    }

    [Fact]
    public void AllHoldsEveryConstant()
    {
        var constants = typeof(SupportChannelReasons)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(constants.SetEquals(SupportChannelReasons.All));
    }
}
