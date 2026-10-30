using System.Globalization;
using System.Net;
using System.Text;

namespace Modules.Notifications.Application;

public enum BillingEmailKind
{
    Reminder = 1,
    Overdue = 2,
}

public sealed record BillingEmailLine(string ServiceName, decimal Amount, string Currency, DateOnly DueDate, int DaysOverdue);

public sealed record BillingEmailTotal(string Currency, decimal Amount);

/// <summary>
/// Lo que las plantillas de cobro necesitan para armar el aviso al cliente. Notifications no puede referenciar Billing
/// (ruling F7), así que este record es el espejo de <c>BillingCustomerMessage</c> y el adaptador de Bootstrapper lo mapea.
/// </summary>
public sealed record BillingCustomerEmailInput(
    BillingEmailKind Kind,
    string ToAddress,
    string CustomerName,
    string TenantName,
    IReadOnlyList<BillingEmailLine> Lines,
    IReadOnlyList<BillingEmailTotal> TotalByCurrency,
    DateOnly NearestDueDate);

/// <summary><c>FailureCode</c>: el fallo del último aviso del cobro, o null si no falló.</summary>
public sealed record BillingDigestEmailRow(
    string CustomerName,
    string ServiceName,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    string? FailureCode);

public sealed record BillingDigestEmailInput(
    string ToAddress,
    string MemberName,
    string TenantName,
    IReadOnlyList<BillingDigestEmailRow> DueSoon,
    IReadOnlyList<BillingDigestEmailRow> Overdue,
    IReadOnlyList<BillingDigestEmailRow> FailedReminders,
    IReadOnlyList<BillingEmailTotal> OverdueTotalByCurrency);

/// <summary>
/// Texto compartido por las tres plantillas de cobro: monto, fecha y tabla. Los nombres de tenant, cliente y servicio los
/// escribe una persona, así que en HTML van escapados (mismo criterio que <see cref="InvitationEmailTemplate"/>) y en texto
/// plano crudos.
/// </summary>
internal static class BillingEmailParts
{
    /// <summary>
    /// COP con «$» y sin decimales; el resto con su código y sin decimales cuando el monto es entero («USD 20»). Notifications
    /// no ve el catálogo de monedas de Tenancy, así que esta es la regla local del correo.
    /// </summary>
    public static string Money(decimal amount, string currency, EmailRegionalFormat format)
    {
        var isCop = string.Equals(currency, "COP", StringComparison.OrdinalIgnoreCase);
        var decimals = isCop || amount % 1m == 0m ? 0 : 2;
        var prefix = isCop ? "$" : currency.ToUpperInvariant();
        return $"{prefix} {amount.ToString($"N{decimals}", format.Numbers)}";
    }

    /// <summary>Un total por moneda unido con «y»: «$ 150.000 y USD 20».</summary>
    public static string Totals(IReadOnlyList<BillingEmailTotal> totals, EmailRegionalFormat format)
    {
        var parts = totals.Select(total => Money(total.Amount, total.Currency, format)).ToArray();
        return parts.Length <= 1
            ? string.Join(string.Empty, parts)
            : string.Join(", ", parts[..^1]) + " y " + parts[^1];
    }

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
