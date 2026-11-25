using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// Los montos llegan ya escritos (símbolo, decimales y separadores los pone el adaptador de Bootstrapper con el catálogo de
/// monedas de Tenancy): la plantilla sólo los ubica y los une con «y».
/// </summary>
public sealed class BillingEmailTemplatesTests
{
    private static readonly EmailRegionalFormat Colombian =
        EmailRegionalFormat.Colombian(TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static BillingCustomerEmailInput Input(
        BillingEmailKind kind,
        IReadOnlyList<BillingEmailLine> lines,
        IReadOnlyList<string> totals,
        string tenant = "Verde SAS",
        string customer = "Ana") =>
        new(kind, "ana@cliente.co", customer, tenant, lines, totals, lines.Min(line => line.DueDate));

    private static BillingEmailLine Line(string service, string amount, int days = 0) =>
        new(service, amount, new DateOnly(2026, 3, 10), days);

    [Fact]
    public void ReminderUsesTheSoftSubjectAndTheTenantFormat()
    {
        var message = BillingReminderEmailTemplate.Render(
            Input(BillingEmailKind.Reminder, [Line("Hosting", "$ 150.000")], ["$ 150.000"]),
            Colombian);

        Assert.Equal("ana@cliente.co", message.ToAddress);
        Assert.Equal("Tu servicio Hosting vence el 10/03/2026", message.Subject);
        Assert.Contains("- Hosting: $ 150.000, vence el 10/03/2026\n", message.TextBody, StringComparison.Ordinal);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("Total: $ 150.000", body, StringComparison.Ordinal);
            Assert.Contains("Si ya realizaste el pago, ignora este mensaje.", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReminderWithTwoServicesListsBothAndOneTotalPerCurrency()
    {
        var message = BillingReminderEmailTemplate.Render(
            Input(
                BillingEmailKind.Reminder,
                [Line("Hosting", "$ 150.000"), Line("Dominio", "US$ 20,00")],
                ["$ 150.000", "US$ 20,00"]),
            Colombian);

        Assert.Equal("Tus servicios vencen el 10/03/2026", message.Subject);
        Assert.Contains("- Dominio: US$ 20,00, vence el 10/03/2026\n", message.TextBody, StringComparison.Ordinal);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("Hosting", body, StringComparison.Ordinal);
            Assert.Contains("Total: $ 150.000 y US$ 20,00", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OverdueUsesTheFirmSubjectWithTheCountAndTheDaysOverdue()
    {
        var message = BillingOverdueEmailTemplate.Render(
            Input(
                BillingEmailKind.Overdue,
                [Line("Hosting", "$ 150.000", days: 5), Line("Dominio", "US$ 20,00", days: 12)],
                ["$ 150.000", "US$ 20,00"]),
            Colombian);

        Assert.Equal("Tienes 2 cobro(s) vencido(s)", message.Subject);
        Assert.Contains("- Hosting: $ 150.000, venció el 10/03/2026 (5 días de atraso)\n", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("- Dominio: US$ 20,00, venció el 10/03/2026 (12 días de atraso)\n", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("<td style=\"padding:4px 12px\">10/03/2026</td><td style=\"padding:4px 12px\">12</td>", message.HtmlBody, StringComparison.Ordinal);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("Total vencido: $ 150.000 y US$ 20,00", body, StringComparison.Ordinal);
            Assert.Contains("Escríbenos para ponerte al día.", body, StringComparison.Ordinal);
            Assert.Contains("Si ya pagaste, ignora este mensaje.", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TemplatesEscapeTenantCustomerAndServiceNamesInHtml()
    {
        var lines = new[] { Line("<b>Hosting</b>", "$ 10", days: 1) };

        foreach (var kind in new[] { BillingEmailKind.Reminder, BillingEmailKind.Overdue })
        {
            var input = Input(kind, lines, ["$ 10"], tenant: "<i>Verde</i>", customer: "<u>Ana</u>");
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
        var row = new BillingDigestEmailRow("Ana <x>", "Hosting", "$ 150.000", new DateOnly(2026, 3, 10), null);
        var failed = row with { FailureCode = "no_phone" };
        var digest = new BillingDigestEmailInput(
            "luis@verde.co", "Luis", "Verde SAS",
            DueSoon: [row, row],
            Overdue: [],
            FailedReminders: [failed],
            OverdueTotalByCurrency: [],
            PriceReviews: [],
            HeldCharges: []);

        var message = BillingDigestEmailTemplate.Render(digest, Colombian);

        Assert.Equal("Cobros de hoy: 2 por vencer, 0 vencidos, 1 avisos fallidos", message.Subject);
        Assert.Contains("Por vencer", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Avisos fallidos", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Vencidos", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Ana &lt;x&gt;", message.HtmlBody, StringComparison.Ordinal);

        var usd = row with { ServiceName = "Dominio", Amount = "US$ 20,00" };
        var withOverdue = digest with { Overdue = [row, usd], OverdueTotalByCurrency = ["$ 150.000", "US$ 20,00"] };
        var second = BillingDigestEmailTemplate.Render(withOverdue, Colombian);
        Assert.Contains("Vencidos", second.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("- Ana <x> / Dominio: US$ 20,00, 10/03/2026\n", second.TextBody, StringComparison.Ordinal);
        Assert.Contains("Total vencido: $ 150.000 y US$ 20,00", second.TextBody, StringComparison.Ordinal);
    }

    private static BillingDigestEmailInput Digest(
        IReadOnlyList<BillingDigestEmailRow> dueSoon,
        IReadOnlyList<BillingPriceReviewEmailRow> priceReviews,
        IReadOnlyList<BillingPriceReviewEmailRow> heldCharges) =>
        new("luis@verde.co", "Luis", "Verde SAS", dueSoon, [], [], [], priceReviews, heldCharges);

    [Fact]
    public void TheDigestDrawsPriceReviewsAndHeldCharges()
    {
        var missing = new BillingPriceReviewEmailRow(
            "Ana <x>", "Hosting", "$ 150.000", new DateOnly(2026, 3, 20), new DateOnly(2026, 3, 15), 2025, true, null);
        var manual = missing with { CustomerName = "Beto", IpcYear = null, IpcMissing = false };
        var held = new BillingPriceReviewEmailRow(
            "Carla", "Dominio", "US$ 20,00", new DateOnly(2026, 3, 12), new DateOnly(2026, 3, 7), 2025, false,
            new DateOnly(2026, 3, 10));
        var failed = new BillingDigestEmailRow("Dora", "Soporte", "$ 10", new DateOnly(2026, 3, 10), "no_phone");

        var message = BillingDigestEmailTemplate.Render(
            Digest([], [missing, manual], [held]) with { FailedReminders = [failed] }, Colombian);

        Assert.Equal(
            "Cobros de hoy: 0 por vencer, 0 vencidos, 1 avisos fallidos, 2 precios por revisar, 1 cobros retenidos",
            message.Subject);
        var text = message.TextBody;
        Assert.Contains(
            "- Ana <x> / Hosting: $ 150.000, revisión 20/03/2026, se genera 15/03/2026, IPC 2025 (falta cargarlo)\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "- Beto / Hosting: $ 150.000, revisión 20/03/2026, se genera 15/03/2026, IPC —\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "- Carla / Dominio: US$ 20,00, revisión 12/03/2026, se genera 07/03/2026, IPC 2025, retenido desde 10/03/2026\n",
            text,
            StringComparison.Ordinal);
        // Después de los avisos fallidos, primero lo que está por revisar y luego lo retenido.
        var failedAt = text.IndexOf("Avisos fallidos", StringComparison.Ordinal);
        var reviewsAt = text.IndexOf("Precios por revisar", StringComparison.Ordinal);
        var heldAt = text.IndexOf("Cobros retenidos", StringComparison.Ordinal);
        Assert.True(failedAt >= 0 && failedAt < reviewsAt && reviewsAt < heldAt, text);
        var html = message.HtmlBody;
        Assert.Contains("<h3>Precios por revisar</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Cobros retenidos</h3>", html, StringComparison.Ordinal);
        Assert.Contains(">Precio actual</th>", html, StringComparison.Ordinal);
        Assert.Contains(">Se genera</th>", html, StringComparison.Ordinal);
        Assert.Contains(">2025 (falta cargarlo)</td>", html, StringComparison.Ordinal);
        Assert.Contains("Ana &lt;x&gt;", html, StringComparison.Ordinal);
        // «Retenido desde» es columna de los retenidos y de nadie más.
        Assert.Equal(
            html.IndexOf(">Retenido desde</th>", StringComparison.Ordinal),
            html.LastIndexOf(">Retenido desde</th>", StringComparison.Ordinal));
        Assert.True(
            html.IndexOf(">Retenido desde</th>", StringComparison.Ordinal)
                > html.IndexOf("<h3>Cobros retenidos</h3>", StringComparison.Ordinal),
            html);
    }

    [Fact]
    public void EmptyReviewSectionsAreNotDrawn()
    {
        var row = new BillingDigestEmailRow("Ana", "Hosting", "$ 150.000", new DateOnly(2026, 3, 10), null);

        var message = BillingDigestEmailTemplate.Render(Digest([row], [], []), Colombian);

        Assert.Equal("Cobros de hoy: 1 por vencer, 0 vencidos, 0 avisos fallidos", message.Subject);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.DoesNotContain("Precios por revisar", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Cobros retenidos", body, StringComparison.Ordinal);
        }
    }
}
