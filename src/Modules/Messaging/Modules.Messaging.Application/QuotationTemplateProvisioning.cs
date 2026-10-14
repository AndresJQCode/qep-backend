using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.5: la **única** fuente del nombre, idioma, categoría, texto, variables y ejemplos de la plantilla de cotización. Es el
/// mismo texto que la plantilla de Zenvia (README § Plantilla de WhatsApp) para que el cliente final no note el cambio de canal.
/// <para>
/// El nombre va por versiones: <c>qep_cotizacion</c> es la 1 y <c>qep_cotizacion_v2</c>, <c>_v3</c>… las siguientes. Las usa el reintento
/// (enmienda del owner, 2026-10-15): Meta bloquea un tiempo el nombre de una plantilla borrada, así que reintentar crea la versión
/// siguiente en vez de recrear la misma. Un cambio de texto algún día es otra definición que también crea la versión siguiente, aprobada
/// antes de que el envío la use; el sender de Quotations lee el nombre vigente de la fila <c>Quotation</c> de la conexión, no de una
/// constante, y cambia en el mismo commit que las variables.
/// </para>
/// </summary>
public static class QuotationTemplateDefinition
{
    public const string Name = MessageTemplateName.QuotationName;
    public const string Language = "es";
    public const string Category = "UTILITY";
    public const string Body = "¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*.";
    public const string Footer = "Este mensaje fue generado automáticamente.";
    public const string ExampleFileName = "cotizacion-ejemplo.pdf";
    public const string ExampleMimeType = "application/pdf";

    // «qep_cotizacion_v»: dentro del prefijo reservado, así que ninguna libre puede tomar una versión.
    private const string VersionPrefix = MessageTemplateName.QuotationPrefix + "v";

    public static readonly IReadOnlyList<string> Variables = ["fullname", "order_number", "total", "valid_until"];

    public static readonly IReadOnlyDictionary<string, string> Examples = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fullname"] = "Juan Perez",
        ["order_number"] = "COT-000123",
        ["total"] = "2.450.000 COP",
        ["valid_until"] = "30 de septiembre de 2026",
    };

    /// <summary>El nombre de la versión <paramref name="version"/>: la 1 es <see cref="Name"/>; de la 2 en adelante, <c>qep_cotizacion_v{n}</c>.</summary>
    public static string NameFor(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return version == 1 ? Name : VersionPrefix + version.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>El inverso de <see cref="NameFor"/>: 1 para <see cref="Name"/>, n para <c>qep_cotizacion_v{n}</c> con n ≥ 2 escrito sin ceros a
    /// la izquierda; cualquier otro nombre (también <c>_v1</c>, <c>_v0</c> o <c>_v02</c>, que <see cref="NameFor"/> nunca produce) es <c>null</c>.</summary>
    public static int? VersionOf(string? name)
    {
        if (string.Equals(name, Name, StringComparison.Ordinal))
        {
            return 1;
        }

        if (name is null || !name.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var digits = name[VersionPrefix.Length..];
        if (digits.Length == 0 || digits[0] == '0' || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 2 ? version : null;
    }

    public static TemplateDraft Draft(string name) => new(name, Language, Category, null, Body, Footer, [], Examples);

    /// <summary>El payload de creación de la versión <paramref name="name"/>, con el encabezado de documento apuntando al
    /// <paramref name="handle"/> de la Resumable Upload.</summary>
    public static JsonObject Payload(string handle, string name) => TemplateComponentsBuilder.CreatePayload(Draft(name), handle);

    /// <summary>§4.5, adopción: unos <c>components</c> de Meta son esta plantilla si el cuerpo y el pie coinciden, el encabezado es
    /// documento y las variables son exactamente las cuatro. No mira el nombre. Se compara lo normalizado por <see cref="TemplateCatalog"/>,
    /// no el JSON crudo: Meta agrega <c>example</c> y otros campos que no cambian la plantilla.</summary>
    public static bool Matches(string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var described = TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson);
        return string.Equals(described.Body, Body, StringComparison.Ordinal)
            && string.Equals(described.Footer, Footer, StringComparison.Ordinal)
            && described.Header is null
            && described.UnsupportedReason == TemplateUnsupportedReasons.MediaHeader
            && described.Parameters.ToHashSet(StringComparer.Ordinal).SetEquals(Variables)
            && described.Buttons.Count == 0
            && IsDocumentHeader(componentsJson);
    }

    /// <summary>El PDF de ejemplo que Meta exige para aprobar un encabezado de documento. Se genera, no se embebe: una página con una línea.</summary>
    public static byte[] ExamplePdf() => MinimalPdf.Build("Cotizacion de ejemplo - QEP");

    // TemplateCatalog normaliza cualquier encabezado multimedia a «media_header»: el formato DOCUMENT se mira acá.
    private static bool IsDocumentHeader(string componentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(componentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.EnumerateArray().Any(component =>
                    component.ValueKind == JsonValueKind.Object
                    && string.Equals(TemplateCatalog.Text(component, "type"), "HEADER", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(TemplateCatalog.Text(component, "format"), "DOCUMENT", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Un PDF 1.4 de una página con una línea de texto y una tabla <c>xref</c> correcta. Suficiente para que Meta lo acepte como
/// ejemplo de encabezado de documento; no es un motor de PDF.</summary>
internal static class MinimalPdf
{
    public static byte[] Build(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
        var stream = $"BT /F1 18 Tf 72 720 Td ({escaped}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream).ToString(CultureInfo.InvariantCulture)} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}

/// <summary>§4.7 <c>POST …/template-management/quotation</c>: aprovisiona (o adopta, o reintenta) la plantilla de cotización de la conexión.</summary>
public sealed record ProvisionQuotationTemplateCommand(Guid TenantId, Guid ConnectionId) : ICommand<ManagedTemplateDto>;

/// <summary>
/// §4.5 con el reintento versionado (enmienda del owner, 2026-10-15), en orden:
/// <list type="number">
/// <item>Si la conexión ya tiene su <c>Quotation</c> viva (pendiente, aprobada o en apelación), <c>quotation_exists</c>.</item>
/// <item>Sin <c>Quotation</c>, una <c>External</c> <c>qep_cotizacion</c> en <c>es</c> se adopta sin tocar Meta si su cuerpo coincide
/// (auditada con <c>changed_fields = ["kind"]</c>, la marca de «adoptada»); si no coincide, <c>quotation_conflict</c>.</item>
/// <item>El nombre a crear es la versión siguiente a la mayor del espejo (cualquier kind, idioma <c>es</c>); sin ninguna, la 1.</item>
/// <item>Sube el PDF de ejemplo y crea con ese handle. Si Meta dice que el nombre ya existe (uno en cuarentena que el espejo ya no
/// conoce), prueba la versión siguiente con el mismo handle, hasta <see cref="MaxCreateAttempts"/> creaciones.</item>
/// <item>Con la nueva creada, borra en Meta la <c>Quotation</c> vieja del reintento. Crear antes de borrar hace que un fallo en la creación
/// deje la rechazada intacta, sin ningún <c>DELETE</c>, y que el siguiente intento calcule la misma versión. Si el borrado falla por el
/// token, <c>connection_unavailable</c> y no se escribe nada: la nueva ya quedó en Meta y el siguiente refresco la trae como
/// <c>External</c>. Si falla por otra cosa, queda un warning y se sigue: el próximo refresco traerá la vieja como <c>External</c> si Meta
/// todavía la tiene.</item>
/// <item>Quita la fila vieja e inserta la <c>PENDING</c> <c>Quotation</c> con el nombre usado, audita y guarda: una sola transacción, así
/// que nunca quedan dos filas <c>Quotation</c>. Si Meta falla al subir o al crear no se escribe nada.</item>
/// </list>
/// </summary>
public sealed partial class ProvisionQuotationTemplateHandler(
    IMessageTemplateRepository repository,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    CallerMembership callerMembership,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    ILogger<ProvisionQuotationTemplateHandler> logger)
    : ICommandHandler<ProvisionQuotationTemplateCommand, ManagedTemplateDto>
{
    /// <summary>Creaciones por intento, contando la primera: un tope para no recorrer versiones sin fin si Meta responde «ya existe» a todo.</summary>
    public const int MaxCreateAttempts = 5;

    // La adopción sólo cambia el kind: es lo que distingue en la auditoría una adoptada de una creada.
    private static readonly string[] AdoptedFields = ["kind"];

    public async Task<ManagedTemplateDto> HandleAsync(ProvisionQuotationTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var now = clock.UtcNow;

        var current = await repository.FindQuotationAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (current is { IsLive: true })
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateQuotationExists, "This connection already has its quotation template pending or approved.");
        }

        if (current is null
            && await repository.FindByNameAsync(command.TenantId, command.ConnectionId, QuotationTemplateDefinition.Name, QuotationTemplateDefinition.Language, cancellationToken) is { } external)
        {
            return await AdoptAsync(command, external, now, cancellationToken);
        }

        var mirror = await repository.ListAsync(command.TenantId, command.ConnectionId, cancellationToken);
        var nextVersion = (mirror
            .Where(template => string.Equals(template.Language, QuotationTemplateDefinition.Language, StringComparison.Ordinal))
            .Select(template => QuotationTemplateDefinition.VersionOf(template.Name))
            .Max() ?? 0) + 1;

        var handle = await UploadExampleAsync(command, source, cancellationToken);
        var (created, name) = await CreateAsync(command, source, handle, nextVersion, cancellationToken);

        if (current is not null)
        {
            var deleted = await meta.DeleteTemplateAsync(source, current.MetaTemplateId, current.Name, cancellationToken);
            if (deleted.Failure is { } failure)
            {
                if (TemplateGraphErrors.IsTokenRejected(failure))
                {
                    throw await TemplateGraphErrors.FailAsync(connections, command.TenantId, command.ConnectionId, failure, cancellationToken);
                }

                LogPreviousVersionNotDeleted(logger, command.TenantId, command.ConnectionId, current.MetaTemplateId);
            }

            repository.Remove(current);
        }

        var memberId = await callerMembership.FindAsync(command.TenantId, cancellationToken);
        var template = MessageTemplate.Pending(
            command.TenantId, command.ConnectionId, created.Id, name, QuotationTemplateDefinition.Language, created.Category, TemplateComponentsBuilder.Named,
            created.Status, TemplateComponentsBuilder.Components(QuotationTemplateDefinition.Draft(name), handle).ToJsonString(), MessageTemplateKind.Quotation,
            memberId, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateQuotationProvisioned, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(template, await NamesOfAsync(command.TenantId, template, cancellationToken));
    }

    private async Task<ManagedTemplateDto> AdoptAsync(ProvisionQuotationTemplateCommand command, MessageTemplate external, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!QuotationTemplateDefinition.Matches(external.ComponentsJson))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateQuotationConflict,
                $"A template named '{QuotationTemplateDefinition.Name}' already exists in Meta with another body; delete it from the templates screen first.");
        }

        external.AdoptAsQuotation(now);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateQuotationProvisioned, external.Id, now, AdoptedFields);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(external, await NamesOfAsync(command.TenantId, external, cancellationToken));
    }

    private async Task<string> UploadExampleAsync(ProvisionQuotationTemplateCommand command, MessagingTemplateSource source, CancellationToken cancellationToken)
    {
        var upload = await meta.UploadTemplateExampleAsync(
            source, QuotationTemplateDefinition.ExamplePdf(), QuotationTemplateDefinition.ExampleMimeType, QuotationTemplateDefinition.ExampleFileName, cancellationToken);
        if (upload.Succeeded && upload.Value is { } handle)
        {
            return handle;
        }

        if (upload.Failure is { } failure && TemplateGraphErrors.IsTokenRejected(failure))
        {
            throw await TemplateGraphErrors.FailAsync(connections, command.TenantId, command.ConnectionId, failure, cancellationToken);
        }

        throw new MessagingDomainException(MessagingErrorCodes.TemplateUploadFailed, "WhatsApp did not accept the example PDF of the quotation template.");
    }

    // Una sola subida para todas las versiones: el handle no está atado al nombre.
    private async Task<(CreatedTemplate Created, string Name)> CreateAsync(
        ProvisionQuotationTemplateCommand command, MessagingTemplateSource source, string handle, int firstVersion, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = QuotationTemplateDefinition.NameFor(firstVersion + attempt - 1);
            var created = await meta.CreateTemplateAsync(source, QuotationTemplateDefinition.Payload(handle, name), cancellationToken);
            if (created.Succeeded && created.Value is { } value)
            {
                return (value, name);
            }

            var error = await TemplateGraphErrors.FailAsync(
                connections, command.TenantId, command.ConnectionId, created.Failure ?? new MessagingGraphFailure(0, null, "unreachable"), cancellationToken);
            if (error.Code != MessagingErrorCodes.TemplateNameTaken || attempt >= MaxCreateAttempts)
            {
                throw error;
            }
        }
    }

    private Task<IReadOnlyDictionary<Guid, string>> NamesOfAsync(Guid tenantId, MessageTemplate template, CancellationToken cancellationToken) =>
        TemplateManagementSupport.MemberNamesAsync(
            memberNames, tenantId, template.CreatedByMemberId is { } member ? [member] : [], cancellationToken);

    // Sin el token ni la respuesta de Meta: sólo lo que hace falta para encontrar la plantilla huérfana.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Meta did not delete the previous quotation template {MetaTemplateId} of connection {ConnectionId} in tenant {TenantId}; its mirror row is removed anyway and the next refresh brings it back as External if Meta still has it.")]
    private static partial void LogPreviousVersionNotDeleted(ILogger logger, Guid tenantId, Guid connectionId, string metaTemplateId);
}
