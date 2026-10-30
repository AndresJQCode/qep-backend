using System.Globalization;
using System.Net;
using System.Text;

namespace Modules.Notifications.Application;

public enum BillingEmailKind
{
    Reminder = 1,
    Overdue = 2,
}

/// <summary>
/// <c>Amount</c> llega escrito («US$ 20,00»): símbolo y decimales son del catálogo de monedas de Tenancy, que esta capa no ve, así
/// que el adaptador de Bootstrapper lo resuelve con el mismo formateador que la plantilla de WhatsApp. El correo no tiene uno propio.
/// </summary>
public sealed record BillingEmailLine(string ServiceName, string Amount, DateOnly DueDate, int DaysOverdue);

/// <summary>
/// Lo que las plantillas de cobro necesitan para armar el aviso al cliente. Notifications no puede referenciar Billing
/// (ruling F7), así que este record es el espejo de <c>BillingCustomerMessage</c> y el adaptador de Bootstrapper lo mapea.
/// <c>TotalByCurrency</c>: un total ya escrito por moneda, en el orden en que se muestran.
/// </summary>
public sealed record BillingCustomerEmailInput(
    BillingEmailKind Kind,
    string ToAddress,
    string CustomerName,
    string TenantName,
    IReadOnlyList<BillingEmailLine> Lines,
    IReadOnlyList<string> TotalByCurrency,
    DateOnly NearestDueDate);

/// <summary><c>Amount</c> ya escrito, como en <see cref="BillingEmailLine"/>. <c>FailureCode</c>: el fallo del último aviso del
/// cobro, o null si no falló.</summary>
public sealed record BillingDigestEmailRow(
    string CustomerName,
    string ServiceName,
    string Amount,
    DateOnly DueDate,
    string? FailureCode);

public sealed record BillingDigestEmailInput(
    string ToAddress,
    string MemberName,
    string TenantName,
    IReadOnlyList<BillingDigestEmailRow> DueSoon,
    IReadOnlyList<BillingDigestEmailRow> Overdue,
    IReadOnlyList<BillingDigestEmailRow> FailedReminders,
    IReadOnlyList<string> OverdueTotalByCurrency);

/// <summary>
/// Texto compartido por las tres plantillas de cobro: totales, fecha y tabla. Los nombres de tenant, cliente y servicio los
/// escribe una persona, así que en HTML van escapados (mismo criterio que <see cref="InvitationEmailTemplate"/>) y en texto
/// plano crudos.
/// </summary>
internal static class BillingEmailParts
{
    /// <summary>Un total por moneda unido con «y»: «$ 150.000 y US$ 20,00».</summary>
    public static string Totals(IReadOnlyList<string> totals) =>
        totals.Count <= 1
            ? string.Join(string.Empty, totals)
            : string.Join(", ", totals.Take(totals.Count - 1)) + " y " + totals[^1];

    // InvariantCulture: la `/` del patrón es separador de cultura, no un literal.
    public static string Date(DateOnly date, EmailRegionalFormat format) =>
        date.ToString(format.DateFormat, CultureInfo.InvariantCulture);

    public static string Html(string value) => WebUtility.HtmlEncode(value);

    public static string HtmlTable(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows)
    {
        var html = new StringBuilder("<table style=\"border-collapse:collapse\"><tr>");
        foreach (var header in headers)
        {
            html.Append("<th style=\"text-align:left;padding:4px 12px\">").Append(Html(header)).Append("</th>");
        }

        html.Append("</tr>");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            foreach (var cell in row)
            {
                html.Append("<td style=\"padding:4px 12px\">").Append(Html(cell)).Append("</td>");
            }

            html.Append("</tr>");
        }

        return html.Append("</table>").ToString();
    }
}
