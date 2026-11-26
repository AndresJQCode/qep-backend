using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>El pedido y la configuración de la conexión que lo factura, ya validados (moneda, empresa y conexión configurada).</summary>
internal sealed record SiigoInvoicingContext(SiigoInvoiceInput Input, SiigoSettings Settings);

/// <summary>Lo que comparten los casos de uso de facturación (spec 2026-11-18 §5.4 y §6.1): la doble capa de autorización, la carga y
/// validación del pedido, las credenciales, la auditoría y el resumen de errores de Siigo.</summary>
internal static class SiigoInvoiceSupport
{
    private const string SupportedCurrency = "COP";

    /// <summary>Tenant de la ruta, permiso <c>siigo.invoice.issue</c> y módulo <c>siigo</c> (403, nunca 404).</summary>
    public static Task AuthorizeAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, CancellationToken cancellationToken) =>
        SiigoAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, SiigoPermissions.InvoiceIssue, cancellationToken);

    /// <summary>
    /// El pedido (con las mismas precondiciones de la marca manual: anulado, ya facturado, sin pagar, datos del cliente) y la configuración
    /// de la conexión que lo factura. Moneda distinta de COP: <c>siigo.invoice.currency_unsupported</c>; cuenta de cobro sin empresa:
    /// <c>no_company</c>; empresa sin conexión configurada: <c>no_connection</c> (spec §6.1). Todos 422 y todos antes de cualquier llamada.
    /// </summary>
    public static async Task<SiigoInvoicingContext> LoadAsync(
        ISiigoOrderSource orders,
        ISiigoSettingsRepository settingsRepository,
        Guid tenantId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var input = await orders.GetAsync(tenantId, orderId, cancellationToken)
            ?? throw new ResourceNotFoundException("order.order.not_found", $"Order '{orderId}' was not found.");
        if (!string.Equals(input.Currency, SupportedCurrency, StringComparison.OrdinalIgnoreCase))
        {
            throw new SiigoDomainException(SiigoErrorCodes.InvoiceCurrencyUnsupported, "Only orders in Colombian pesos can be invoiced in Siigo.");
        }

        if (input.CompanyId is not { } companyId)
        {
            throw new SiigoDomainException(SiigoErrorCodes.InvoiceNoCompany, "The billing account of the order has no company.");
        }

        var settings = await settingsRepository.FindByCompanyAsync(
                tenantId, companyId, [.. input.Lines.Select(line => line.VariantId).Distinct()], cancellationToken)
            ?? throw new SiigoDomainException(
                SiigoErrorCodes.InvoiceNoConnection, "No Siigo connection is configured to invoice with the company of the order.");
        return new SiigoInvoicingContext(input, settings);
    }

    /// <summary>Las credenciales de la conexión, o 409 <c>siigo.connection_unavailable</c> (pausada, clave rechazada, ilegible…).</summary>
    public static async Task<SiigoCredentials> CredentialsAsync(
        ISiigoConnectionDirectory connections, Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var resolution = await connections.ResolveAsync(tenantId, connectionId, cancellationToken);
        return resolution.Credentials
            ?? throw new ResourceConflictException(
                SiigoErrorCodes.ConnectionUnavailable,
                $"The Siigo connection cannot be used right now ({resolution.UnavailableReason}).");
    }

    /// <summary>
    /// La fila de ese pedido y tenant que tiene PDF en Siigo (<see cref="SiigoInvoice.HasPdf"/>), o 404 <c>siigo.invoice.not_found</c>:
    /// otro pedido, otro tenant, rechazada, pendiente o resuelta a mano sin id son lo mismo, y nunca se llama a Siigo.
    /// </summary>
    public static async Task<SiigoInvoice> FindWithPdfAsync(
        ISiigoInvoiceRepository invoices, Guid tenantId, Guid orderId, Guid invoiceId, CancellationToken cancellationToken) =>
        (await invoices.ListByOrderAsync(tenantId, orderId, cancellationToken))
            .FirstOrDefault(value => value.Id == invoiceId && value.HasPdf)
        ?? throw new ResourceNotFoundException(SiigoErrorCodes.InvoiceNotFound, "The order has no Siigo invoice with a PDF under that id.");

    /// <summary>El PDF de la fila, pedido a Siigo con las credenciales de su conexión. Lo comparten la descarga y el envío por WhatsApp.</summary>
    public static async Task<byte[]> LoadPdfAsync(
        ISiigoConnectionDirectory connections, ISiigoApi api, SiigoInvoice row, CancellationToken cancellationToken)
    {
        var credentials = await CredentialsAsync(connections, row.TenantId, row.ConnectionId, cancellationToken);
        return await api.GetInvoicePdfAsync(credentials, row.SiigoInvoiceId!, cancellationToken);
    }

    public static SiigoInvoiceKind ParseKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? SiigoInvoiceKind.Order : Enum.Parse<SiigoInvoiceKind>(kind, ignoreCase: true);

    public static bool IsKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind) || Enum.TryParse<SiigoInvoiceKind>(kind, ignoreCase: true, out _);

    /// <summary>La fila que cuenta de cada tipo: la vigente si la hay y, si no, la última (una <c>Rejected</c> sigue a la vista para que la
    /// pantalla muestre el rechazo). Primero la del pedido.</summary>
    public static IReadOnlyList<SiigoInvoice> Current(IEnumerable<SiigoInvoice> rows) =>
        [
            .. rows
                .GroupBy(row => row.Kind)
                .Select(group => group.Where(row => row.IsLive).OrderByDescending(row => row.CreatedAt).FirstOrDefault()
                    ?? group.OrderByDescending(row => row.CreatedAt).First())
                .OrderBy(row => row.Kind),
        ];

    /// <summary>Si es la de flete y la del pedido sigue abierta (<c>Pending</c> o <c>Unknown</c>): sólo sale después de que la del pedido
    /// existe, así que nunca salió y no hay nada que mirar en Siigo.</summary>
    public static bool IsFreightWaitingForTheOrder(SiigoInvoice row, IEnumerable<SiigoInvoice> rows) =>
        row.Kind == SiigoInvoiceKind.Freight && rows.Any(value => value is { Kind: SiigoInvoiceKind.Order, IsOpen: true });

    /// <summary>El código <c>siigo.invoice.*</c> de un intento que no se sabe cómo terminó (<c>SiigoInvoiceOutcome.Transient.Reason</c>). Lo que
    /// el tenant no puede corregir (Partner-Id o token que faltan) cae en <c>unavailable</c>: quien lo arregla lo ve en Grafana.</summary>
    public static string TransientCodeOf(string reason) => reason switch
    {
        "timeout" => SiigoErrorCodes.InvoiceTimeout,
        "network" => SiigoErrorCodes.InvoiceNetwork,
        "http_429" => SiigoErrorCodes.InvoiceRateLimited,
        _ => SiigoErrorCodes.InvoiceUnavailable,
    };

    /// <summary>Si la petición de la factura pudo haber salido hacia Siigo (D-2, spec 2026-11-24). Sin <c>Partner-Id</c> no se llama, y
    /// sin token la autenticación no llegó a dar con qué mandarla: en los demás motivos no se sabe, incluido un timeout o una falla de red
    /// de <c>/auth</c>, que llegan igual que los de la factura y se tratan como sin respuesta.</summary>
    public static bool LeftQep(string transientReason) =>
        transientReason is not ("partner_id_missing" or "auth_without_token");

    public static string ErrorCodeOf(IReadOnlyList<SiigoError> errors)
    {
        if (errors.Any(error => string.Equals(error.Code, SiigoApiErrors.InvalidTotalPayments, StringComparison.OrdinalIgnoreCase)))
        {
            return SiigoErrorCodes.InvoiceTotalsMismatch;
        }

        if (SiigoApiErrors.IsServiceUnavailable(errors))
        {
            return SiigoErrorCodes.InvoiceSiigoService;
        }

        return errors.Any(error => string.Equals(error.Code, SiigoApiErrors.CredentialsRejected, StringComparison.Ordinal))
            ? SiigoErrorCodes.ConnectionUnavailable
            : "siigo.invoice.rejected";
    }

    /// <summary>
    /// El detalle de Siigo para la fila y la pantalla: los códigos de error, sin su mensaje. Siigo repite en el mensaje los valores que
    /// rechaza (documento, dirección, nombre del cliente) y la fila se guarda y se devuelve: el spec (§5.6) pide «sin datos del cliente»
    /// y <c>SiigoNoLeakTests</c> lo amarra. La única excepción es <c>invalid_total_payments</c>, cuyo mensaje es el total esperado
    /// (un valor del negocio, no del cliente) que la pantalla muestra junto al de QEP.
    /// </summary>
    public static string ErrorDetailOf(IReadOnlyList<SiigoError> errors) =>
        string.Join(
            "; ",
            errors.Select(error =>
                string.IsNullOrWhiteSpace(error.Message) || !string.Equals(error.Code, SiigoApiErrors.InvalidTotalPayments, StringComparison.OrdinalIgnoreCase)
                    ? error.Code
                    : $"{error.Code}: {error.Message}"));

    /// <summary>Auditoría atómica, en el mismo guardado que el cambio. Lleva el id del pedido, el tipo y, si hay, el número y el código
    /// de error: nunca la petición, el cliente ni la clave.</summary>
    public static void Audit(
        ISiigoAuditRecorder recorder,
        IExecutionContext executionContext,
        SiigoInvoice invoice,
        string action,
        DateTimeOffset now,
        string? outcome = null,
        string? fromStatus = null) =>
        recorder.Record(
            invoice.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            action,
            SiigoAuditActions.InvoiceResourceType,
            invoice.Id.ToString(),
            JsonSerializer.Serialize(new
            {
                orderId = invoice.OrderId,
                kind = invoice.Kind.ToString(),
                status = invoice.Status.ToString(),
                number = invoice.Number,
                errorCode = invoice.ErrorCode,
                outcome,
                fromStatus,
            }),
            now);

    /// <summary>El id con que se marca el pedido: el de Siigo o, en una resuelta a mano (sin id), el número.</summary>
    public static string ExternalIdOf(SiigoInvoice invoice) => invoice.SiigoInvoiceId ?? invoice.Number!;
}
