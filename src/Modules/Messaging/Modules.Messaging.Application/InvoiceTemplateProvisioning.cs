using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-21, <c>POST …/template-management/invoice</c>: aprovisiona (o adopta, o reintenta) la plantilla de factura.</summary>
/// <param name="Body">El cuerpo editado, o <c>null</c> para el texto base.</param>
public sealed record ProvisionInvoiceTemplateCommand(Guid TenantId, Guid ConnectionId, string? Body = null) : ICommand<ManagedTemplateDto>;

/// <summary>El cuerpo, si viene, usa sólo las variables de la factura.</summary>
public sealed class ProvisionInvoiceTemplateCommandValidator : AbstractValidator<ProvisionInvoiceTemplateCommand>
{
    public ProvisionInvoiceTemplateCommandValidator() =>
        RuleFor(command => command.Body).Custom((body, context) =>
        {
            if (body is not null && TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.Invoice)) is { } error)
            {
                context.AddFailure(new ValidationFailure("body", error));
            }
        });
}

/// <summary>
/// Spec 2026-11-21: el mismo procedimiento que <see cref="ProvisionQuotationTemplateHandler"/>, paso por paso, con la fila <c>Invoice</c>:
/// viva ⇒ <c>invoice_exists</c>; externa <c>qep_factura</c>/<c>es</c> viva y compatible ⇒ adopción (auditada con <c>["kind"]</c>), incompatible ⇒
/// <c>invoice_conflict</c>; versión siguiente a la mayor <c>qep_factura*</c> del espejo; una subida y hasta <see cref="MaxCreateAttempts"/>
/// creaciones ante <c>name_taken</c>; crear antes de borrar la rechazada; una sola transacción. La carrera entre dos primeros
/// aprovisionamientos la cierra <c>UX_message_templates_invoice</c>. Las razones de cada regla están en el handler de la cotización y no se
/// repiten acá.
/// </summary>
public sealed partial class ProvisionInvoiceTemplateHandler(
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
    ILogger<ProvisionInvoiceTemplateHandler> logger,
    IValidator<ProvisionInvoiceTemplateCommand> validator)
    : ICommandHandler<ProvisionInvoiceTemplateCommand, ManagedTemplateDto>
{
    public const int MaxCreateAttempts = ProvisionQuotationTemplateHandler.MaxCreateAttempts;

    private static readonly string[] AdoptedFields = ["kind"];

    public async Task<ManagedTemplateDto> HandleAsync(ProvisionInvoiceTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var now = clock.UtcNow;
        var body = (command.Body ?? InvoiceTemplateDefinition.BaseBody).Trim();

        var current = await repository.FindInvoiceAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (current is { IsLive: true })
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateInvoiceExists, "This connection already has its invoice template pending or approved.");
        }

        if (current is null
            && await repository.FindByNameAsync(command.TenantId, command.ConnectionId, InvoiceTemplateDefinition.Name, InvoiceTemplateDefinition.Language, cancellationToken) is { IsLive: true } external)
        {
            return await AdoptAsync(command, external, body, now, cancellationToken);
        }

        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var mirror = await repository.ListAsync(command.TenantId, command.ConnectionId, cancellationToken);
        var nextVersion = (mirror
            .Where(template => string.Equals(template.Language, InvoiceTemplateDefinition.Language, StringComparison.Ordinal))
            .Select(template => InvoiceTemplateDefinition.VersionOf(template.Name))
            .Max() ?? 0) + 1;

        var handle = await UploadExampleAsync(command, source, cancellationToken);
        var (created, name) = await CreateAsync(command, source, handle, body, nextVersion, cancellationToken);

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
            command.TenantId, command.ConnectionId, created.Id, name, InvoiceTemplateDefinition.Language, created.Category, TemplateComponentsBuilder.Named,
            created.Status, TemplateComponentsBuilder.Components(InvoiceTemplateDefinition.Draft(name, body), TemplateMediaHeader.Document(handle)).ToJsonString(), MessageTemplateKind.Invoice,
            memberId, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateInvoiceProvisioned, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(template, await NamesOfAsync(command.TenantId, template, cancellationToken));
    }

    private async Task<ManagedTemplateDto> AdoptAsync(
        ProvisionInvoiceTemplateCommand command, MessageTemplate external, string body, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!InvoiceTemplateDefinition.IsAdoptable(external.ComponentsJson)
            || (command.Body is not null && !string.Equals(InvoiceTemplateDefinition.StoredBody(external.ComponentsJson), body, StringComparison.Ordinal)))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateInvoiceConflict,
                $"A template named '{InvoiceTemplateDefinition.Name}' already exists in Meta with another body or an incompatible shape; delete it from the templates screen first.");
        }

        external.AdoptAsInvoice(now);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateInvoiceProvisioned, external.Id, now, AdoptedFields);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(external, await NamesOfAsync(command.TenantId, external, cancellationToken));
    }

    private async Task<string> UploadExampleAsync(ProvisionInvoiceTemplateCommand command, MessagingTemplateSource source, CancellationToken cancellationToken)
    {
        var upload = await meta.UploadTemplateExampleAsync(
            source, InvoiceTemplateDefinition.ExamplePdf(), InvoiceTemplateDefinition.ExampleMimeType, InvoiceTemplateDefinition.ExampleFileName, cancellationToken);
        if (upload.Succeeded && upload.Value is { } handle)
        {
            return handle;
        }

        // Como en la cotización (revisión final F1): la subida es un endpoint de la app, no de la WABA; nunca se reporta la conexión.
        var failure = upload.Failure ?? new MessagingGraphFailure(0, null, "no_handle");
        LogUploadFailed(
            logger, command.TenantId, command.ConnectionId, failure.HttpStatus, failure.Code?.ToString(CultureInfo.InvariantCulture) ?? "none", failure.Reason);
        throw new MessagingDomainException(MessagingErrorCodes.TemplateUploadFailed, "WhatsApp did not accept the example PDF of the invoice template.");
    }

    private async Task<(CreatedTemplate Created, string Name)> CreateAsync(
        ProvisionInvoiceTemplateCommand command, MessagingTemplateSource source, string handle, string body, int firstVersion, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = InvoiceTemplateDefinition.NameFor(firstVersion + attempt - 1);
            var created = await meta.CreateTemplateAsync(source, InvoiceTemplateDefinition.Payload(handle, name, body), cancellationToken);
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Meta did not delete the previous invoice template {MetaTemplateId} of connection {ConnectionId} in tenant {TenantId}; its mirror row is removed anyway and the next refresh brings it back as External if Meta still has it.")]
    private static partial void LogPreviousVersionNotDeleted(ILogger logger, Guid tenantId, Guid connectionId, string metaTemplateId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Meta did not accept the example PDF of the invoice template for connection {ConnectionId} in tenant {TenantId}: status {HttpStatus}, code {GraphCode}, reason {Reason}; the connection is not reported.")]
    private static partial void LogUploadFailed(ILogger logger, Guid tenantId, Guid connectionId, int httpStatus, string graphCode, string reason);
}
