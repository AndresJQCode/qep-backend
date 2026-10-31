using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

public sealed class TemplateDetailsJsonTests
{
    [Fact]
    public void ParseSendReadsWhatSerializeWrote()
    {
        var json = TemplateDetailsJson.Serialize("pedido", "es_CO", "UTILITY", new Dictionary<string, string> { ["1"] = "Ana", ["2"] = "42" });

        var send = TemplateDetailsJson.ParseSend(json);

        Assert.NotNull(send);
        Assert.Equal("pedido", send.Name);
        Assert.Equal("es_CO", send.Language);
        Assert.Equal(new Dictionary<string, string> { ["1"] = "Ana", ["2"] = "42" }, send.Parameters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"template\":{\"name\":\"x\"}}")]
    public void ParseSendIsNullOnGarbage(string? json) => Assert.Null(TemplateDetailsJson.ParseSend(json));
}
