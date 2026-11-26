using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>Task-notices spec 2026-11-22 §5.6, D-7 and P-2: the email of an escalated assignment. The test texts carry no accents on
/// purpose: <c>WebUtility.HtmlEncode</c> writes them as numeric entities, and these tests look at the escape of <c>&lt;</c>.</summary>
public sealed class SupportTaskAssignedEmailTemplateTests
{
    private const string Link = "https://qep.qcode.co/support/abc";

    [Fact]
    public void CarriesNumberSubjectAndLink()
    {
        var message = SupportTaskAssignedEmailTemplate.Render("ana@example.com", true, "Bruno Diaz", "SOP-000127", "Cambiar la impresora", Link);

        Assert.Equal("ana@example.com", message.ToAddress);
        Assert.Equal("Te asignaron la tarea SOP-000127", message.Subject);
        Assert.Contains("Bruno Diaz te asignó la tarea SOP-000127", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("SOP-000127", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Cambiar la impresora", message.TextBody, StringComparison.Ordinal);
        Assert.Contains(Link, message.TextBody, StringComparison.Ordinal);
        Assert.Contains(Link, message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Cambiar la impresora", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodesTheSubjectInHtml()
    {
        var message = SupportTaskAssignedEmailTemplate.Render("ana@example.com", true, "<i>Bruno</i>", "SOP-000127", "<b>x</b>", Link);

        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;i&gt;Bruno&lt;/i&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>x</b>", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutActorUsesTheImpersonalSentence()
    {
        var message = SupportTaskAssignedEmailTemplate.Render("ana@example.com", true, null, "SOP-000127", "Cambiar la impresora", Link);

        Assert.Contains("Te asignaron la tarea SOP-000127", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("te asignó", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ACustomerTicketSaysTicketNotTask()
    {
        var message = SupportTaskAssignedEmailTemplate.Render("ana@example.com", false, "Bruno Diaz", "SOP-000127", "No llego la factura", Link);

        Assert.Equal("Te asignaron el ticket SOP-000127", message.Subject);
        Assert.Contains("el ticket", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("tarea", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Ábrelo en QEP", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkPointsToTheTask()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal($"https://qep.qcode.co/support/{id:D}", AccountLinks.SupportTask("https://qep.qcode.co/", id));
    }
}
