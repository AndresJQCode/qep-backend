using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>El canal viaja con el token en claro, en memoria: nada que lo imprima puede mostrarlo.</summary>
public sealed class WhatsAppChannelTests
{
    // Review Focus 5.
    [Fact]
    public void ChannelToStringHidesTheToken()
    {
        var id = Guid.CreateVersion7();

        var text = new ZenviaWhatsAppChannel(id, "Zenvia", "tok-SENTINEL", "573001234567", "tpl", null).ToString();

        Assert.DoesNotContain("tok-SENTINEL", text, StringComparison.Ordinal);
        Assert.Contains(id.ToString(), text, StringComparison.Ordinal);
    }
}
