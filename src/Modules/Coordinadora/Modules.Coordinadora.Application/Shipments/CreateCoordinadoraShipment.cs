using BuildingBlocks.Application;
using FluentValidation;
using Modules.Coordinadora.Domain;
using Modules.Tenancy.Application;

namespace Modules.Coordinadora.Application.Shipments;

/// <summary><c>POST …/orders/{orderId}/coordinadora-shipment</c> (<c>coordinadora.shipment.manage</c>). El cuerpo es opcional: sin él se usa
/// el paquete por defecto y el total de las líneas como valor declarado.</summary>
public sealed record CreateCoordinadoraShipmentCommand(Guid TenantId, Guid OrderId, CoordinadoraShipmentOverridesInput? Overrides)
    : ICommand<CoordinadoraShipmentDto>;

public sealed class CreateCoordinadoraShipmentValidator : AbstractValidator<CreateCoordinadoraShipmentCommand>
{
    public CreateCoordinadoraShipmentValidator()
    {
        When(command => command.Overrides is not null, () =>
            RuleFor(command => command.Overrides!).SetValidator(new CoordinadoraShipmentOverridesValidator()));
    }
}

/// <summary>
/// Spec 2026-11-21 §5.4. Orden: tenant, permiso y módulo (403) → transacción y <c>pg_advisory_xact_lock</c> por pedido → las filas del
/// pedido (<c>ExpireIfStale</c> primero) → según la fila viva:
/// <list type="bullet">
/// <item><description><c>Created</c>, <c>Pending</c> (otra petición la está mandando) o <c>Unknown</c> → se devuelve tal cual, sin llamar.
/// <b>Nunca se reenvía</b>: Coordinadora no tiene clave de idempotencia y reenviar puede generar dos guías.</description></item>
/// <item><description>Ninguna viva → se arma una fila nueva: pedido, conexión y credenciales (409 si no está operativa), faltantes (422
/// <c>not_ready</c>), fila <c>Pending</c> con la petición entera y commit.</description></item>
/// </list>
/// Después del commit, fuera de la transacción y sin reintentos, <c>Guias_generarGuia</c>: número de guía → <c>Created</c>; rechazo →
/// <c>Rejected</c> (el pedido queda libre); timeout, 5xx o red → <c>Unknown</c>, que resuelve una persona.
/// </summary>
public sealed class CreateCoordinadoraShipmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory memberships,
    ICoordinadoraOrderSource orders,
    ICoordinadoraConnectionDirectory connections,
    ICoordinadoraSettingsRepository settingsRepository,
    ICoordinadoraGeographyLookup geography,
    ICoordinadoraApi api,
    ICoordinadoraShipmentRepository shipments,
    ICoordinadoraUnitOfWork unitOfWork,
    ICoordinadoraAuditRecorder auditRecorder,
    IClock clock,
    IValidator<CreateCoordinadoraShipmentCommand> validator) : ICommandHandler<CreateCoordinadoraShipmentCommand, CoordinadoraShipmentDto>
{
    public async Task<CoordinadoraShipmentDto> HandleAsync(CreateCoordinadoraShipmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await CoordinadoraShipmentSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var memberId = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw CoordinadoraAuthorization.Denied();

        var (row, credentials) = await PrepareAsync(command, memberId, cancellationToken);
        if (credentials is not null)
        {
            await SendAsync(row, credentials, cancellationToken);
        }

        return CoordinadoraShipmentDto.From(row);
    }

    // El tramo con el candado: decide, arma y guarda la fila Pending. Todo o nada hasta el commit. Devuelve la fila que cuenta y, si hay
    // que mandarla, las credenciales.
    private async Task<(CoordinadoraShipment Row, CoordinadoraCredentials? Credentials)> PrepareAsync(
        CreateCoordinadoraShipmentCommand command, Guid memberId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockOrderAsync(command.OrderId, cancellationToken);

        var rows = await shipments.ListByOrderAsync(command.TenantId, command.OrderId, cancellationToken);
        var expired = CoordinadoraShipmentSupport.ExpireStale(rows, now);
        var live = rows.Where(row => row.IsLive).OrderByDescending(row => row.CreatedAt).FirstOrDefault();
        if (live is not null)
        {
            if (expired > 0)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return (live, null);
        }

        var context = await CoordinadoraShipmentSupport.LoadAsync(
            orders, connections, settingsRepository, geography, command.TenantId, command.OrderId, cancellationToken);
        if (context.Connection is null)
        {
            throw new CoordinadoraShipmentNotReadyException([CoordinadoraMissing.Of(CoordinadoraMissing.ConnectionMissing)]);
        }

        // Con la conexión no operativa es 409 antes de armar nada.
        var credentials = await CoordinadoraShipmentSupport.CredentialsAsync(connections, command.TenantId, context.Connection.Id, cancellationToken);
        var ready = CoordinadoraShipmentSupport.BuildOrThrow(
            context, credentials.Nit, CoordinadoraShipmentOverridesValidator.ToOverrides(command.Overrides));

        var row = CoordinadoraShipment.PendingFor(
            Guid.CreateVersion7(),
            command.TenantId,
            context.Connection.Id,
            command.OrderId,
            ready.GuideJson,
            ready.DeclaredValue,
            context.Input.Version,
            memberId,
            now);
        // El reclamo del envío nace con la fila: otro «Generar guía» que espere el candado la ve en camino y la devuelve tal cual.
        row.BeginSend(now);
        shipments.Add(row);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (row, credentials);
    }

    // El tramo sin candado: la llamada a Coordinadora y su resultado. Un resultado que no se sabe queda Unknown: sin clave de idempotencia
    // no hay reenvío seguro.
    private async Task SendAsync(CoordinadoraShipment row, CoordinadoraCredentials credentials, CancellationToken cancellationToken)
    {
        var outcome = await api.CreateGuideAsync(credentials, new CoordinadoraGuideRequest(row.RequestJson), cancellationToken);
        var now = clock.UtcNow;
        switch (outcome)
        {
            case CoordinadoraGuideOutcome.Created created:
                row.MarkCreated(created.GuideNumber, created.LabelPdf, now);
                CoordinadoraShipmentSupport.Audit(auditRecorder, executionContext, row, CoordinadoraAuditActions.ShipmentCreated, now);
                break;
            case CoordinadoraGuideOutcome.Rejected rejected:
                row.MarkRejected(rejected.Code, rejected.Message, now);
                CoordinadoraShipmentSupport.Audit(auditRecorder, executionContext, row, CoordinadoraAuditActions.ShipmentRejected, now);
                break;
            case CoordinadoraGuideOutcome.Transient transient:
                row.MarkUnknown(transient.Reason, now);
                break;
            default:
                throw new InvalidOperationException("Unknown guide outcome.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
