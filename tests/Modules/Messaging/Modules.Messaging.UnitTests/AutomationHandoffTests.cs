using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-24 D-T12: la causa de un traspaso se limpia antes de guardarse en el hilo; ningún texto libre llega.</summary>
public sealed class AutomationHandoffTests
{
    private static AutomationHandoff WithTitle(string? title) =>
        AutomationHandoff.Create(AutomationHandoffKind.Block, null, "handoff", title, title, null, null);

    private static AutomationHandoff WithCode(string? code) =>
        AutomationHandoff.Create(
            AutomationHandoffKind.SendFailed, code, "handoff", null, null, code,
            new AutomationHandoffHttp("policy-lookup", null, code, code, null, null));

    [Fact]
    public void TitlesAndNamesAreFittedTo120WithAnEllipsis()
    {
        var longTitle = new string('a', 300);

        var fitted = AutomationHandoff.Create(
            AutomationHandoffKind.SendFailed, null, "handoff", longTitle, longTitle, null,
            new AutomationHandoffHttp("policy-lookup", longTitle, "timeout", null, null, longTitle));

        Assert.Equal(AutomationHandoff.TextMaxLength, fitted.NodeTitle!.Length);
        Assert.EndsWith("…", fitted.NodeTitle, StringComparison.Ordinal);
        Assert.Equal(AutomationHandoff.TextMaxLength, fitted.ConnectionName!.Length);
        Assert.Equal(AutomationHandoff.TextMaxLength, fitted.Http!.NodeTitle!.Length);
        Assert.Equal(AutomationHandoff.TextMaxLength, fitted.Http.ConnectionName!.Length);
        Assert.Null(WithTitle("   ").NodeTitle);
        Assert.Null(WithTitle("   ").ConnectionName);
        Assert.Equal("Pasar a un asesor", WithTitle("  Pasar a un asesor ").NodeTitle);
    }

    [Theory]
    [InlineData("messaging.connection_unavailable")]
    [InlineData("http_404")]
    [InlineData("secret_unreadable")]
    public void ACodeKeepsItsValue(string code)
    {
        var handoff = WithCode(code);

        Assert.Equal(code, handoff.Code);
        Assert.Equal(code, handoff.ConnectionReason);
        Assert.Equal(code, handoff.Http!.Outcome);
        Assert.Equal(code, handoff.Http.ErrorCode);
    }

    [Theory]
    [InlineData("Ya existe una orden")]
    [InlineData("HTTP_404")]
    [InlineData("a b")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("")]
    public void ACodeThatIsNotACodeIsDropped(string code)
    {
        var handoff = WithCode(code);

        Assert.Null(handoff.Code);
        Assert.Null(handoff.ConnectionReason);
        // El http conserva su nodo, así que no se descarta entero: sólo los códigos que no lo son.
        Assert.Null(handoff.Http!.Outcome);
        Assert.Null(handoff.Http.ErrorCode);
    }

    [Fact]
    public void ANodeIdLongerThan64IsDropped()
    {
        var longId = new string('n', 65);

        var handoff = AutomationHandoff.Create(
            AutomationHandoffKind.Block, null, longId, "Pasar a un asesor", null, null,
            new AutomationHandoffHttp(longId, "Buscar", "error_status", "http_404", 404, null));

        Assert.Null(handoff.NodeId);
        Assert.Null(handoff.Http!.NodeId);
        Assert.Equal("error_status", handoff.Http.Outcome);
        Assert.Equal(new string('n', 64), AutomationHandoff.Create(
            AutomationHandoffKind.Block, null, new string('n', 64), null, null, null, null).NodeId);
    }

    [Theory]
    [InlineData(99, null)]
    [InlineData(600, null)]
    [InlineData(404, 404)]
    [InlineData(100, 100)]
    [InlineData(599, 599)]
    public void AStatusOutsideHttpRangeIsDropped(int status, int? expected)
    {
        var handoff = AutomationHandoff.Create(
            AutomationHandoffKind.Block, null, "handoff", null, null, null,
            new AutomationHandoffHttp("policy-lookup", null, "error_status", null, status, null));

        Assert.Equal(expected, handoff.Http!.Status);
    }

    [Fact]
    public void AnHttpWithoutNodeAndOutcomeIsDropped()
    {
        var empty = AutomationHandoff.Create(
            AutomationHandoffKind.Block, null, "handoff", null, null, null,
            new AutomationHandoffHttp(null, null, null, null, null, null));
        var emptyAfterCleaning = AutomationHandoff.Create(
            AutomationHandoffKind.Block, null, "handoff", null, null, null,
            new AutomationHandoffHttp(new string('n', 65), "Buscar", "No es un código", "http_404", 404, "API de Clasic"));

        Assert.Null(empty.Http);
        Assert.Null(emptyAfterCleaning.Http);
    }
}
