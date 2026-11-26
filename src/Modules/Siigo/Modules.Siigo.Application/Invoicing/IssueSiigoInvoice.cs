using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>La forma de pago que quien factura eligió para un comprobante, en la factura de ese tipo (<c>Order</c> por defecto).</summary>
public sealed record SiigoPaymentChoice(string? Kind, Guid ProofId, string? SiigoPaymentTypeId);

/// <summary>Generar la factura de un pedido (<c>siigo.invoice.issue</c>). No tiene ruta propia: lo corre el worker de las tandas por cada
/// pedido (<c>SiigoBatchItemInvoker</c>). <paramref name="Payments"/> es opcional: sin él se usa la propuesta de la cuenta de
/// cobro.</summary>
public sealed record IssueSiigoInvoiceCommand(Guid TenantId, Guid OrderId, IReadOnlyList<SiigoPaymentChoice>? Payments)
    : ICommand<IReadOnlyList<SiigoInvoiceDto>>;

/// <summary>La forma de cada forma de pago elegida, la misma en la tanda de uno y en el comando que corre el worker: así los dos validan
/// igual y con las mismas claves de error.</summary>
internal static class SiigoPaymentChoiceRules
{
    public static void Configure(InlineValidator<SiigoPaymentChoice> payment)
    {
        payment.RuleFor(value => value.Kind).Must(SiigoInvoiceSupport.IsKind).OverridePropertyName("kind");
        payment.RuleFor(value => value.ProofId).NotEmpty().OverridePropertyName("proofId");
        payment.RuleFor(value => value.SiigoPaymentTypeId)
            .NotEmpty().MaximumLength(SiigoSettings.IdMaxLength).OverridePropertyName("siigoPaymentTypeId");
    }
}

/// <summary>La forma del cuerpo (422 <c>validation.failed</c> con el campo en camelCase). Un elemento nulo de <c>payments</c> falla en su
/// propio campo.</summary>
public sealed class IssueSiigoInvoiceValidator : AbstractValidator<IssueSiigoInvoiceCommand>
{
    public IssueSiigoInvoiceValidator()
    {
        RuleForEach(command => command.Payments).NotNull().ChildRules(SiigoPaymentChoiceRules.Configure);
    }
}

/// <summary>
/// Spec 2026-11-18 §5.4. Orden: tenant, permiso y módulo (403) → transacción y <c>pg_advisory_xact_lock</c> por pedido → las filas del
/// pedido (<c>ExpireIfStale</c> primero) → según la tabla del paso 1:
/// <list type="bullet">
/// <item><description><c>Pending</c> → se retoma: se reenvían los <b>mismos bytes</b> con la <b>misma clave</b>, sin rearmar ni validar.
/// Con la conexión no operativa es un conflicto y la fila no cambia. Si otra emisión la está mandando (el reclamo de
/// <see cref="SiigoInvoice.SendLease"/>) no se vuelve a mandar: es lo que hace que dos «Generar factura» a la vez creen una sola.</description></item>
/// <item><description><c>Created</c>, <c>Stamped</c>, <c>StampRejected</c>, <c>Unknown</c> → se devuelve tal cual, sin llamar a Siigo. Una
/// del pedido ya creada se vuelve a marcar en el pedido (idempotente): cubre el corte entre crear la factura y marcarlo.</description></item>
/// <item><description><c>Rejected</c>, <c>Failed</c>, <c>Annulled</c> o ninguna → se arma una fila nueva: pedido, validaciones (§6.1), faltantes (422
/// <c>not_ready</c>), cuadre previo (422 <c>totals_mismatch</c>), cliente en Siigo (se busca y, si no está, se crea), filas <c>Pending</c> con
/// la petición entera y commit.</description></item>
/// </list>
/// Después del commit, fuera de la transacción y sin reintentos propios, <c>POST /v1/invoices</c> por borrador (la del pedido primero, la
/// de flete sólo si la del pedido salió) y el resultado se guarda aparte: 2xx → <c>Created</c>/<c>Stamped</c> y el pedido se marca; error de
/// validación de Siigo en un primer intento → <c>Rejected</c> y el pedido sigue libre. La excepción (owner, 2026-11-25) es un
/// <c>invalid_total_payments</c> de hasta un peso: se reenvía una vez con el total de Siigo (<see cref="SiigoPaymentRounding"/>).
/// <para>Spec 2026-11-24 §3: ante la duda la factura queda <b>por confirmar</b> (<c>Pending</c> con <c>unanswered_at</c>), con la misma
/// clave, y decide una persona. Pasa con timeout, red, 5xx, 429, una respuesta ilegible, un error de servicio de Siigo (aun en el primer
/// intento), un rechazo de datos en el reenvío de una que ya tuvo un envío sin respuesta, y al reanudar un envío cuyo reclamo venció sin
/// resultado. Sin <c>Partner-Id</c> o sin token la factura no salió: sigue <c>Pending</c> sin quedar por confirmar. La de flete cuya
/// factura del pedido no existe en Siigo (ni <c>Created</c>, ni <c>Stamped</c>, ni <c>StampRejected</c>) nunca se marca sin respuesta:
/// sólo sale después de ella, así que su reclamo vencido es de un envío que se cortó antes de llegarle.</para>
/// </summary>
public sealed class IssueSiigoInvoiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory memberships,
    ISiigoOrderSource orders,
    ISiigoSettingsRepository settingsRepository,
    ISiigoConnectionDirectory connections,
    ISiigoApi api,
    ISiigoInvoiceRepository invoices,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock,
    IValidator<IssueSiigoInvoiceCommand> validator) : ICommandHandler<IssueSiigoInvoiceCommand, IReadOnlyList<SiigoInvoiceDto>>
{
    public async Task<IReadOnlyList<SiigoInvoiceDto>> HandleAsync(IssueSiigoInvoiceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var memberId = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SiigoAuthorization.Denied();

        var prepared = await PrepareAsync(command, memberId, cancellationToken);
        if (prepared.ToSend.Count > 0)
        {
            await SendAsync(prepared, cancellationToken);
        }

        await EnsureOrderMarkedAsync(prepared, cancellationToken);
        var now = clock.UtcNow;
        var current = SiigoInvoiceSupport.Current(prepared.Rows);
        return
        [
            .. current.Select(row => SiigoInvoiceDto.From(
                row,
                now,
                prepared.OrderMarkFailed && row.Kind == SiigoInvoiceKind.Order,
                waitsForOrder: SiigoInvoiceSupport.IsFreightWaitingForTheOrder(row, current))),
        ];
    }

    private sealed record Prepared(Guid TenantId, Guid OrderId, List<SiigoInvoice> Rows, List<SiigoInvoice> ToSend, SiigoCredentials? Credentials)
    {
        /// <summary>El pedido no se pudo marcar aunque la factura exista (anulado o marcado a mano): se avisa en la fila del pedido.</summary>
        public bool OrderMarkFailed { get; set; }
    }

    // El tramo con el candado: decide, arma, crea el cliente y guarda las filas Pending. Todo o nada hasta el commit.
    private async Task<Prepared> PrepareAsync(IssueSiigoInvoiceCommand command, Guid memberId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockOrderAsync(command.OrderId, cancellationToken);

        var rows = (await invoices.ListByOrderAsync(command.TenantId, command.OrderId, cancellationToken)).ToList();
        var expired = rows.Count(row => row.ExpireIfStale(now));
        var live = rows.Where(row => row.IsLive).ToList();

        Prepared prepared;
        if (live.Count > 0)
        {
            prepared = await ResumeAsync(command, rows, live, now, cancellationToken);
        }
        else
        {
            prepared = await BuildNewAsync(command, memberId, rows, now, cancellationToken);
        }

        if (expired > 0 || prepared.ToSend.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return prepared;
    }

    private async Task<Prepared> ResumeAsync(
        IssueSiigoInvoiceCommand command,
        List<SiigoInvoice> rows,
        List<SiigoInvoice> live,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Sólo las Pending se reenvían, y no las que otra petición ya está mandando. Lo demás se devuelve tal cual.
        var pending = live.Where(row => row.Status == SiigoInvoiceStatus.Pending).OrderBy(row => row.Kind).ToList();
        if (pending.Count == 0)
        {
            return new Prepared(command.TenantId, command.OrderId, rows, [], null);
        }

        // Con la conexión no operativa es 409 y la fila no cambia: se resuelve antes de reclamar nada.
        var credentials = await SiigoInvoiceSupport.CredentialsAsync(connections, command.TenantId, pending[0].ConnectionId, cancellationToken);
        var toSend = pending.Where(row => !row.IsBeingSent(now)).ToList();
        var orderInvoiced = IsInvoiced(rows);
        foreach (var row in toSend)
        {
            // Un reclamo vencido que sigue puesto, o un motivo de envío sin unanswered_at (versión anterior durante el despliegue), es un
            // envío sin respuesta que falta registrar: se anota antes de reclamar de nuevo. Salvo en la de flete mientras la del pedido
            // no exista: sólo sale después de ella, así que su reclamo vencido es de un envío que se cortó antes de llegarle.
            var mayHaveLeft = row.Kind != SiigoInvoiceKind.Freight || orderInvoiced;
            if (mayHaveLeft && row.RecoverInterruptedSend(now))
            {
                SiigoInvoiceSupport.Audit(auditRecorder, executionContext, row, SiigoAuditActions.InvoiceUnanswered, now);
            }

            row.BeginSend(now);
        }

        return new Prepared(command.TenantId, command.OrderId, rows, toSend, credentials);
    }

    private async Task<Prepared> BuildNewAsync(
        IssueSiigoInvoiceCommand command,
        Guid memberId,
        List<SiigoInvoice> rows,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var context = await SiigoInvoiceSupport.LoadAsync(orders, settingsRepository, command.TenantId, command.OrderId, cancellationToken);
        var overrides = (command.Payments ?? [])
            .Select(choice => new SiigoPaymentOverride(
                SiigoInvoiceSupport.ParseKind(choice.Kind), choice.ProofId, choice.SiigoPaymentTypeId!.Trim()))
            .ToList();

        var drafts = SiigoInvoiceBuilder.Build(context.Input, context.Settings, overrides) switch
        {
            SiigoBuildResult.Ready ready => ready.Drafts,
            SiigoBuildResult.NotReady notReady => throw new SiigoInvoiceNotReadyException(notReady.Missing),
            SiigoBuildResult.TotalsMismatch mismatch => throw new SiigoInvoiceTotalsMismatchException(mismatch.QepTotal, mismatch.ComputedTotal),
            _ => throw new InvalidOperationException("Unknown build result."),
        };

        var credentials = await SiigoInvoiceSupport.CredentialsAsync(connections, command.TenantId, context.Settings.ConnectionId, cancellationToken);
        await EnsureCustomerAsync(credentials, context.Input, drafts[0], cancellationToken);

        var toSend = new List<SiigoInvoice>();
        foreach (var draft in drafts)
        {
            var row = SiigoInvoice.PendingFor(
                Guid.CreateVersion7(),
                command.TenantId,
                context.Settings.ConnectionId,
                command.OrderId,
                draft.Kind,
                draft.Request.Json,
                context.Input.OrderVersion,
                memberId,
                now);
            // El reclamo del envío nace con la fila: otro «Generar factura» que espere el candado la ve en camino y no la manda de nuevo.
            row.BeginSend(now);
            invoices.Add(row);
            rows.Add(row);
            toSend.Add(row);
        }

        return new Prepared(command.TenantId, command.OrderId, rows, toSend, credentials);
    }

    // Spec §6.2: el cliente se resuelve antes de la factura, en una llamada aparte, y sólo después de la validación completa y de armar la
    // petición (crear un cliente no se deshace). Existente y activo: la factura va sólo con su identificación y los datos de Siigo mandan.
    private async Task EnsureCustomerAsync(
        SiigoCredentials credentials, SiigoInvoiceInput input, SiigoInvoiceDraft draft, CancellationToken cancellationToken)
    {
        var identification = SiigoCustomerMapper.Identification(input.BilledParty)!;
        if (await api.FindCustomerAsync(credentials, identification, cancellationToken) is not null)
        {
            return;
        }

        await api.CreateCustomerAsync(
            credentials,
            draft.NewCustomer ?? throw new InvalidOperationException("A ready draft always carries its customer."),
            cancellationToken);
    }

    // El tramo sin candado: la llamada a Siigo y su resultado, fila por fila. Cualquier resultado que no sea «creada» corta: la de flete
    // no sale antes de saber qué pasó con la del pedido.
    private async Task SendAsync(Prepared prepared, CancellationToken cancellationToken)
    {
        foreach (var row in prepared.ToSend.OrderBy(value => value.Kind))
        {
            if (row.Kind == SiigoInvoiceKind.Freight && !IsInvoiced(prepared.Rows))
            {
                // La del pedido no salió (rechazada, sin saber o todavía en camino): la de flete espera. Se suelta su reclamo.
                row.MarkTransient(null, clock.UtcNow);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            var outcome = await CreateAsync(prepared, row, cancellationToken);
            var proceed = await ApplyAsync(prepared, row, outcome, cancellationToken);
            if (!proceed)
            {
                break;
            }
        }
    }

    // Si Siigo rechaza los pagos por redondeo, la petición ajustada se guarda antes de salir (si el proceso muere en medio, la reanudación
    // manda esos bytes con esa clave) y sale una sola vez: lo que conteste es el resultado.
    private async Task<SiigoInvoiceOutcome> CreateAsync(Prepared prepared, SiigoInvoice row, CancellationToken cancellationToken)
    {
        var outcome = await PostAsync();
        if (outcome is not SiigoInvoiceOutcome.Rejected rejected
            || row.UnansweredAt is not null
            || SiigoPaymentRounding.Adjust(row.RequestJson, rejected) is not { } adjusted)
        {
            return outcome;
        }

        var now = clock.UtcNow;
        row.ResendAdjusted(adjusted, now);
        foreach (var waiting in prepared.ToSend.Where(value => value != row && value.Status == SiigoInvoiceStatus.Pending))
        {
            waiting.RenewClaim(now);
        }

        SiigoInvoiceSupport.Audit(auditRecorder, executionContext, row, SiigoAuditActions.InvoicePaymentsAdjusted, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await PostAsync();

        Task<SiigoInvoiceOutcome> PostAsync() => api.CreateInvoiceAsync(
            prepared.Credentials!, new SiigoInvoiceRequest(row.RequestJson), row.IdempotencyKey, cancellationToken);
    }

    private static bool IsInvoiced(IEnumerable<SiigoInvoice> rows) =>
        rows.Any(row => row is { Kind: SiigoInvoiceKind.Order, Status: SiigoInvoiceStatus.Created or SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected });

    // Devuelve si se puede seguir con la siguiente factura.
    private async Task<bool> ApplyAsync(
        Prepared prepared, SiigoInvoice row, SiigoInvoiceOutcome outcome, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        switch (outcome)
        {
            case SiigoInvoiceOutcome.Created created:
                row.MarkCreated(created.Id, created.Name, created.Total, created.Cufe, now);
                if (row.Kind == SiigoInvoiceKind.Order
                    && await orders.GetVersionAsync(prepared.TenantId, prepared.OrderId, cancellationToken) is { } version
                    && version != row.OrderVersion)
                {
                    // El pedido cambió mientras Siigo creaba la factura: la pantalla lo avisa para que alguien revise.
                    row.MarkOrderChanged();
                }

                SiigoInvoiceSupport.Audit(auditRecorder, executionContext, row, SiigoAuditActions.InvoiceCreated, now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                if (row.Kind == SiigoInvoiceKind.Order)
                {
                    await TryMarkAsync(prepared, row, cancellationToken);
                }

                return true;

            case SiigoInvoiceOutcome.Rejected rejected:
                var serviceOutage = SiigoApiErrors.IsServiceUnavailable(rejected.Errors);
                var alreadyUnanswered = row.UnansweredAt is not null;
                var isRejected = row.RecordRejection(
                    SiigoInvoiceSupport.ErrorCodeOf(rejected.Errors),
                    SiigoInvoiceSupport.ErrorDetailOf(rejected.Errors),
                    rejected.ExpectedPaymentsTotal,
                    serviceOutage,
                    now);
                if (isRejected)
                {
                    SiigoInvoiceSupport.Audit(auditRecorder, executionContext, row, SiigoAuditActions.InvoiceRejected, now);
                    if (row.Kind == SiigoInvoiceKind.Order)
                    {
                        // La de flete no salió nunca: se descarta, y el pedido queda libre para armarse otra vez.
                        foreach (var sibling in prepared.Rows.Where(value => value is { Kind: SiigoInvoiceKind.Freight, Status: SiigoInvoiceStatus.Pending }))
                        {
                            sibling.MarkFailed("siigo.invoice.order_rejected", "The order invoice was rejected, so the freight invoice was not sent.", now);
                        }
                    }
                }
                else
                {
                    // Queda por confirmar. Se audita la primera vez, y cada rechazo de datos retenido por un envío anterior sin respuesta;
                    // otra caída de servicio sobre una que ya lo estaba no agrega nada.
                    if (!alreadyUnanswered || !serviceOutage)
                    {
                        SiigoInvoiceSupport.Audit(auditRecorder, executionContext, row, SiigoAuditActions.InvoiceUnanswered, now);
                    }

                    ReleaseWaiting(prepared, row, now);
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
                return false;

            case SiigoInvoiceOutcome.Transient transient:
                // El motivo queda en la fila para que el pedido le diga al tenant qué pasó (spec siigo-motivo-pendiente). En los dos casos
                // sigue Pending, se suelta el reclamo y el reenvío manda lo mismo con la misma clave.
                var errorCode = SiigoInvoiceSupport.TransientCodeOf(transient.Reason);
                if (!SiigoInvoiceSupport.LeftQep(transient.Reason))
                {
                    row.MarkTransient(errorCode, now);
                }
                else if (row.MarkUnanswered(errorCode, null, now))
                {
                    SiigoInvoiceSupport.Audit(auditRecorder, executionContext, row, SiigoAuditActions.InvoiceUnanswered, now);
                }

                ReleaseWaiting(prepared, row, now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return false;

            default:
                throw new InvalidOperationException("Unknown invoice outcome.");
        }
    }

    // La del pedido no salió bien y corta el envío: la de flete de esta petición nunca salió. Suelta su reclamo en el mismo guardado; si
    // lo conservara, el siguiente reenvío la tomaría por un envío cortado y la dejaría por confirmar (spec 2026-11-24 §3).
    private static void ReleaseWaiting(Prepared prepared, SiigoInvoice sent, DateTimeOffset now)
    {
        if (sent.Kind != SiigoInvoiceKind.Order)
        {
            return;
        }

        foreach (var waiting in prepared.ToSend.Where(value => value is { Kind: SiigoInvoiceKind.Freight, Status: SiigoInvoiceStatus.Pending }))
        {
            waiting.MarkTransient(null, now);
        }
    }

    // Una factura del pedido ya creada y todavía sin marca en el pedido (el proceso murió entre una cosa y la otra): se vuelve a marcar.
    // La marca es idempotente: con la misma marca no hace nada.
    private async Task EnsureOrderMarkedAsync(Prepared prepared, CancellationToken cancellationToken)
    {
        // Si la del pedido salió en esta petición, ApplyAsync ya la marcó.
        if (prepared.ToSend.Any(value => value.Kind == SiigoInvoiceKind.Order))
        {
            return;
        }

        var row = prepared.Rows.FirstOrDefault(value => value is
        {
            Kind: SiigoInvoiceKind.Order,
            Status: SiigoInvoiceStatus.Created or SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected,
            Number: not null,
        });
        if (row is not null)
        {
            await TryMarkAsync(prepared, row, cancellationToken);
        }
    }

    // La factura ya existe en Siigo: que el pedido no se pueda marcar (se anuló o alguien lo marcó a mano en medio) no deshace nada ni
    // debe dejar la emisión fallando para siempre. La fila queda Created, el resultado lleva el aviso y la siguiente emisión reintenta sólo
    // la marca.
    private async Task TryMarkAsync(Prepared prepared, SiigoInvoice row, CancellationToken cancellationToken)
    {
        try
        {
            await orders.MarkInvoicedAsync(
                prepared.TenantId, prepared.OrderId, row.Number!, SiigoInvoiceSupport.ExternalIdOf(row), row.CreatedBy, cancellationToken);
        }
        catch (DomainException)
        {
            prepared.OrderMarkFailed = true;
        }
    }
}
