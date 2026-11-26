using System.Globalization;
using System.Net;
using System.Text;

namespace Modules.Notifications.Application;

/// <summary>Una línea del correo, con cada valor ya formateado: la plantilla imprime texto, no calcula.</summary>
public sealed record PurchaseOrderEmailLine(string Code, string Name, string Quantity, string UnitCost, string Subtotal);

/// <summary>
/// Lo que necesita el correo de una orden de compra al proveedor. Todo llega formateado con el formato regional del tenant (lo arma
/// Compras desde el mismo documento del PDF); Notifications no conoce el módulo de Compras. <paramref name="Notes"/> es null sin notas.
/// <paramref name="ShowsAmounts"/> en <c>false</c> (orden vinculada que todavía no tiene precio, decisión del owner 2026-11-19) omite
/// la tabla de montos: costo, subtotal y totales. El PDF adjunto sí lleva «—».
/// </summary>
public sealed record PurchaseOrderEmailInput(
    string ToAddress,
    string TenantName,
    string Number,
    string IssuedOn,
    string SupplierName,
    string Currency,
    string? Notes,
    IReadOnlyList<PurchaseOrderEmailLine> Lines,
    string Subtotal,
    string Tax,
    string Total,
    string FileName,
    ReadOnlyMemory<byte> Pdf,
    bool ShowsAmounts = true);

/// <summary>
/// El correo que recibe el proveedor al enviarle una orden de compra: resumen en el cuerpo y el PDF adjunto. Tutea, como todo texto
/// del producto. El cuerpo HTML escapa todo lo que escribe una persona (nombre del proveedor, notas, nombres de producto).
/// </summary>
public static class PurchaseOrderEmailTemplate
{
    public const string TemplateRef = "purchases.order-sent.v1";

    public static EmailMessage Render(PurchaseOrderEmailInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var subject = $"Orden de compra {input.Number} de {input.TenantName}";

        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Hola {input.SupplierName},\n\n")
            .Append(CultureInfo.InvariantCulture, $"{input.TenantName} te envía la orden de compra {input.Number}, emitida el {input.IssuedOn}. ")
            .Append("Encuentras el detalle abajo y el PDF adjunto.\n\n");
        foreach (var line in input.Lines)
        {
            text.Append(CultureInfo.InvariantCulture, $"- {line.Code} · {line.Name}: {line.Quantity}");
            if (input.ShowsAmounts)
            {
                text.Append(CultureInfo.InvariantCulture, $" x {line.UnitCost} = {line.Subtotal}");
            }

            text.Append('\n');
        }

        if (input.ShowsAmounts)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nSubtotal: {input.Subtotal}\nIVA: {input.Tax}\nTotal ({input.Currency}): {input.Total}\n");
        }

        if (!string.IsNullOrWhiteSpace(input.Notes))
        {
            text.Append(CultureInfo.InvariantCulture, $"\nNotas: {input.Notes}\n");
        }

        var html = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"<p>Hola {Html(input.SupplierName)},</p>")
            .Append(CultureInfo.InvariantCulture, $"<p>{Html(input.TenantName)} te envía la orden de compra <strong>{Html(input.Number)}</strong>, ")
            .Append(CultureInfo.InvariantCulture, $"emitida el {Html(input.IssuedOn)}. Encuentras el detalle abajo y el PDF adjunto.</p>")
            .Append("<table cellpadding=\"6\" cellspacing=\"0\" border=\"1\" style=\"border-collapse:collapse\">")
            .Append(input.ShowsAmounts
                ? "<tr><th>Código</th><th>Producto</th><th>Cantidad</th><th>Costo</th><th>Subtotal</th></tr>"
                : "<tr><th>Código</th><th>Producto</th><th>Cantidad</th></tr>");
        foreach (var line in input.Lines)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{Html(line.Code)}</td><td>{Html(line.Name)}</td>")
                .Append(CultureInfo.InvariantCulture, $"<td>{Html(line.Quantity)}</td>");
            if (input.ShowsAmounts)
            {
                html.Append(CultureInfo.InvariantCulture, $"<td>{Html(line.UnitCost)}</td><td>{Html(line.Subtotal)}</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</table>");
        if (input.ShowsAmounts)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>Subtotal: {Html(input.Subtotal)}<br>IVA: {Html(input.Tax)}<br>")
                .Append(CultureInfo.InvariantCulture, $"Total ({Html(input.Currency)}): <strong>{Html(input.Total)}</strong></p>");
        }

        if (!string.IsNullOrWhiteSpace(input.Notes))
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>Notas: {Html(input.Notes)}</p>");
        }

        return new EmailMessage(
            input.ToAddress,
            subject,
            html.ToString(),
            text.ToString(),
            [new EmailAttachment(input.FileName, "application/pdf", input.Pdf)]);
    }

    private static string Html(string text) => WebUtility.HtmlEncode(text);
}
