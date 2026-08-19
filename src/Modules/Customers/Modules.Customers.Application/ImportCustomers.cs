using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>
/// La carga de un Excel de clientes.
///
/// Lleva el nombre y el tamano, **no el contenido**: `CLI-01` deja el procesamiento del archivo
/// explicitamente fuera de alcance y `SDD-OD-10` —el modelo de importacion— sigue abierta. Pasar
/// el stream por un comando que no lo lee solo daria la impresion de que alguien lo va a leer.
/// </summary>
public sealed record ImportCustomersCommand(
    Guid TenantId,
    string FileName,
    long SizeInBytes) : ICommand<ImportCustomersResponse>;

public sealed class ImportCustomersHandler(
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ImportCustomersCommand, ImportCustomersResponse>
{
    /// <summary>
    /// Las extensiones que el modal de importacion ya ofrece
    /// (<c>ACCEPTED_EXTENSIONS</c> en <c>import-customers-modal.tsx</c>).
    /// </summary>
    private static readonly string[] AcceptedExtensions = [".xlsx", ".xls"];

    /// <summary>
    /// 10 MB, el mismo tope que el frontend ya aplica antes de subir. Duplicarlo aca no es
    /// redundancia: la validacion del navegador la elige el llamador, y un cliente que no sea ese
    /// formulario puede mandar lo que quiera.
    /// </summary>
    private const long MaxSizeInBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Lo unico que <c>Status</c> puede valer hoy. Cuando `SDD-OD-10` se cierre y el procesamiento
    /// exista, este campo es el que va a distinguir "en cola" de "procesado" de "fallo" — el
    /// consumidor ya lo lee.
    /// </summary>
    private const string AcceptedStatus = "accepted";

    public async Task<ImportCustomersResponse> HandleAsync(
        ImportCustomersCommand command,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerImport);

        var fileName = EnsureAcceptableFile(command.FileName, command.SizeInBytes);
        var now = clock.UtcNow;

        // Se audita aunque todavia no se importe nada, y a proposito: la carga de un archivo de
        // clientes es PII entrando al sistema, y quien la subio y cuando es exactamente el rastro
        // que la politica de retencion —el item que el gate CLI-00 tiene abierto— va a necesitar.
        // Auditarlo despues seria auditar solo las cargas posteriores al dia que se implemente.
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.import_accepted",
            fileName,
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // 202 y no 201: **no se creo ningun cliente**. El endpoint acusa recibo del archivo, que
        // es todo lo que este slice promete. Ver ImportCustomersResponse.
        return new ImportCustomersResponse(fileName, now, AcceptedStatus);
    }

    private static string EnsureAcceptableFile(string fileName, long sizeInBytes)
    {
        var trimmed = fileName?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new CustomersDomainException(
                "customers.import.file_required",
                "An Excel file is required.");
        }

        if (!AcceptedExtensions.Any(extension =>
                trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CustomersDomainException(
                "customers.import.file_type_invalid",
                "Only .xlsx and .xls files are accepted.");
        }

        if (sizeInBytes <= 0)
        {
            throw new CustomersDomainException(
                "customers.import.file_empty",
                "The uploaded file is empty.");
        }

        return sizeInBytes > MaxSizeInBytes
            ? throw new CustomersDomainException(
                "customers.import.file_too_large",
                "The uploaded file cannot exceed 10 MB.")
            : trimmed;
    }
}
