using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

public sealed class BillingEmailTemplatesTests
{
    private static readonly EmailRegionalFormat Colombian =
        EmailRegionalFormat.Colombian(TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static BillingCustomerEmailInput Input(
        BillingEmailKind kind,
        IReadOnlyList<BillingEmailLine> lines,
        IReadOnlyList<BillingEmailTotal> totals,
        string tenant = "Verde SAS",
        string customer = "Ana") =>
        new(kind, "ana@cliente.co", customer, tenant, lines, totals, lines.Min(line => line.DueDate));

    private static BillingEmailLine Line(string service, decimal amount, string currency, int days = 0) =>
        new(service, amount, currency, new DateOnly(2026, 3, 10), days);

    [Fact]
    public void ReminderUsesTheSoftSubjectAndTheTenantFormat()
    {
        var message = BillingReminderEmailTemplate.Render(
            Input(BillingEmailKind.Reminder, [Line("Hosting", 150000m, "COP")], [new("COP", 150000m)]),
            Colombian);

        Assert.Equal("ana@cliente.co", message.ToAddress);
        Assert.Equal("Tu servicio Hosting vence el 10/03/2026", message.Subject);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("$ 150.000", body, StringComparison.Ordinal);
            Assert.Contains("10/03/2026", body, StringComparison.Ordinal);
            Assert.Contains("Si ya realizaste el pago, ignora este mensaje.", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReminderWithTwoServicesListsBothAndOneTotalPerCurrency()
    {
        var message = BillingReminderEmailTemplate.Render(
            Input(
                BillingEmailKind.Reminder,
                [Line("Hosting", 150000m, "COP"), Line("Dominio", 20m, "USD")],
                [new("COP", 150000m), new("USD", 20m)]),
            Colombian);

        Assert.Equal("Tus servicios vencen el 10/03/2026", message.Subject);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("Hosting", body, StringComparison.Ordinal);
            Assert.Contains("Dominio", body, StringComparison.Ordinal);
            Assert.Contains("$ 150.000 y USD 20", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OverdueUsesTheFirmSubjectWithTheCountAndTheDaysOverdue()
    {
        var message = BillingOverdueEmailTemplate.Render(
            Input(
                BillingEmailKind.Overdue,
                [Line("Hosting", 150000m, "COP", days: 5), Line("Dominio", 20m, "USD", days: 12)],
                [new("COP", 150000m), new("USD", 20m)]),
            Colombian);

        Assert.Equal("Tienes 2 cobro(s) vencido(s)", message.Subject);
        Assert.Contains("12", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("5", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Escríbenos para ponerte al día.", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplatesEscapeTenantCustomerAndServiceNamesInHtml()
    {
        var lines = new[] { Line("<b>Hosting</b>", 10m, "COP", days: 1) };
        var totals = new BillingEmailTotal[] { new("COP", 10m) };

        foreach (var kind in new[] { BillingEmailKind.Reminder, BillingEmailKind.Overdue })
        {
            var input = Input(kind, lines, totals, tenant: "<i>Verde</i>", customer: "<u>Ana</u>");
            var message = kind == BillingEmailKind.Reminder
                ? BillingReminderEmailTemplate.Render(input, Colombian)
                : BillingOverdueEmailTemplate.Render(input, Colombian);

            Assert.Contains("&lt;b&gt;Hosting&lt;/b&gt;", message.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("&lt;i&gt;Verde&lt;/i&gt;", message.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("&lt;u&gt;Ana&lt;/u&gt;", message.HtmlBody, StringComparison.Ordinal);
            Assert.DoesNotContain("<b>", message.HtmlBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DigestOmitsEmptySectionsAndCountsInTheSubject()
    {
        var row = new BillingDigestEmailRow("Ana <x>", "Hosting", 150000m, "COP", new DateOnly(2026, 3, 10), null);
        var failed = row with { FailureCode = "no_phone" };
        var digest = new BillingDigestEmailInput(
            "luis@verde.co", "Luis", "Verde SAS",
            DueSoon: [row, row],
            Overdue: [],
            FailedReminders: [failed],
            OverdueTotalByCurrency: []);

        var message = BillingDigestEmailTemplate.Render(digest, Colombian);

        Assert.Equal("Cobros de hoy: 2 por vencer, 0 vencidos, 1 avisos fallidos", message.Subject);
        Assert.Contains("Por vencer", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Avisos fallidos", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Vencidos", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Ana &lt;x&gt;", message.HtmlBody, StringComparison.Ordinal);

        var withOverdue = digest with { Overdue = [row], OverdueTotalByCurrency = [new("COP", 150000m)] };
        var second = BillingDigestEmailTemplate.Render(withOverdue, Colombian);
        Assert.Contains("Vencidos", second.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("$ 150.000", second.TextBody, StringComparison.Ordinal);
    }
}
