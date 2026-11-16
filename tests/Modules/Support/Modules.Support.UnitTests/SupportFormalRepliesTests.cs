using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Revisión del Bloque A: el nombre del responsable que firma es una foto del nombre visible de la membresía, que no pasó por
/// las reglas del documento. Se limpia antes de <c>SupportReplyDocument.Create</c>, así un nombre largo o con caracteres de control no
/// llega al dominio a lanzar (un 500).</summary>
public sealed class SupportFormalRepliesTests
{
    [Fact]
    public void TheSignerNameIsTrimmedWithoutControlCharactersAndCutTo200()
    {
        Assert.Equal("Beto Asesor", SupportFormalReplies.SignerName("  Beto\u0007 Asesor\n "));
        Assert.Equal(SupportReplyDocument.SignerNameMaxLength, SupportFormalReplies.SignerName(new string('a', 300)).Length);
        Assert.Equal("Sin nombre", SupportFormalReplies.SignerName(null));
        Assert.Equal("Sin nombre", SupportFormalReplies.SignerName(" \t\u0001 "));
    }
}
