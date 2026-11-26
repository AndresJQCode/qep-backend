using BuildingBlocks.Application;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary><c>GET …/orders/{orderId}/siigo-invoice/{invoiceId}/pdf</c> (<c>siigo.invoice.issue</c>).</summary>
public sealed record GetSiigoInvoicePdfQuery(Guid TenantId, Guid OrderId, Guid InvoiceId) : IQuery<SiigoInvoicePdfDto>;

/// <summary>El PDF de una factura de Siigo, ya decodificado, con el nombre con que se descarga.</summary>
public sealed record SiigoInvoicePdfDto(byte[] Content, string FileName);

/// <summary>
/// El PDF de la factura, que vive en Siigo y no se guarda aquí: se pide en cada descarga con las credenciales de la conexión de la fila.
/// Mismo permiso que generar la factura (de fábrica <c>admin</c> y <c>billing</c>) y la misma doble capa: la política de la ruta y
/// <c>SiigoAuthorization</c> (403, nunca 404). La fila tiene que ser de ese pedido y de ese tenant, y existir en Siigo con su id
/// (<see cref="SiigoInvoice.HasPdf"/>): una rechazada, una pendiente o una resuelta a mano sin id no tienen PDF, y ahí es 404 sin llamar
/// a Siigo.
/// </summary>
public sealed class GetSiigoInvoicePdfHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoApi api,
    ISiigoInvoiceRepository invoices) : IQueryHandler<GetSiigoInvoicePdfQuery, SiigoInvoicePdfDto>
{
    public async Task<SiigoInvoicePdfDto> HandleAsync(GetSiigoInvoicePdfQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);

        var row = await SiigoInvoiceSupport.FindWithPdfAsync(invoices, query.TenantId, query.OrderId, query.InvoiceId, cancellationToken);
        var content = await SiigoInvoiceSupport.LoadPdfAsync(connections, api, row, cancellationToken);
        return new SiigoInvoicePdfDto(content, FileNameOf(row));
    }

    // El número es de Siigo (FV-1-123): se limpia lo que un nombre de archivo no admite, por si trae otra cosa.
    private static string FileNameOf(SiigoInvoice row)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. (row.Number ?? string.Empty).Where(character => !invalid.Contains(character))]).Trim();
        return $"{(name.Length > 0 ? name : "factura-siigo")}.pdf";
    }
}
