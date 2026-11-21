using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>La forma de pago que quien factura eligió para un comprobante, en la factura de ese tipo (<c>Order</c> por defecto).</summary>
public sealed record SiigoPaymentChoice(string? Kind, Guid ProofId, string? SiigoPaymentTypeId);

/// <summary><c>POST …/orders/{orderId}/siigo-invoice</c> (<c>siigo.invoice.issue</c>). <paramref name="Payments"/> es opcional: sin él se
/// usa la propuesta de la cuenta de cobro.</summary>
public sealed record IssueSiigoInvoiceCommand(Guid TenantId, Guid OrderId, IReadOnlyList<SiigoPaymentChoice>? Payments)
    : ICommand<IReadOnlyList<SiigoInvoiceDto>>;

/// <summary>La forma del cuerpo (422 <c>validation.failed</c> con el campo en camelCase).</summary>
public sealed class IssueSiigoInvoiceValidator : AbstractValidator<IssueSiigoInvoiceCommand>
{
    public IssueSiigoInvoiceValidator()
    {
        RuleForEach(command => command.Payments).ChildRules(payment =>
        {
            payment.RuleFor(value => value.Kind).Must(SiigoInvoiceSupport.IsKind).OverridePropertyName("kind");
            payment.RuleFor(value => value.ProofId).NotEmpty().OverridePropertyName("proofId");
            payment.RuleFor(value => value.SiigoPaymentTypeId)
                .NotEmpty().MaximumLength(SiigoSettings.IdMaxLength).OverridePropertyName("siigoPaymentTypeId");
        });
    }
}

/// <summary>
/// Spec 2026-11-18 §5.4. Orden: tenant, permiso y módulo (403) → transacción y <c>pg_advisory_xact_lock</c> por pedido → las filas del
/// pedido (<c>ExpireIfStale</c> primero) → según la tabla del paso 1:
/// <list type="bullet">
/// <item><description><c>Pending</c> → se retoma: se reenvían los <b>mismos bytes</b> con la <b>misma clave</b>, sin rearmar ni validar.
/// Con la conexión no operativa es 409 y la fila no cambia. Si otra petición la está mandando (el reclamo de
/// <see cref="SiigoInvoice.SendLease"/>) no se vuelve a mandar: es lo que hace que dos «Generar factura» a la vez creen una sola.</description></item>
/// <item><description><c>Created</c>, <c>Stamped</c>, <c>StampRejected</c>, <c>Unknown</c> → se devuelve tal cual, sin llamar a Siigo. Una
/// del pedido ya creada se vuelve a marcar en el pedido (idempotente): cubre el corte entre crear la factura y marcarlo.</description></item>
/// <item><description><c>Rejected</c>, <c>Failed</c> o ninguna → se arma una fila nueva: pedido, validaciones (§6.1), faltantes (422
/// <c>not_ready</c>), cuadre previo (422 <c>totals_mismatch</c>), cliente en Siigo (se busca y, si no está, se crea), filas <c>Pending</c> con
/// la petición entera y commit.</description></item>
/// </list>
/// Después del commit, fuera de la transacción y sin reintentos propios, <c>POST /v1/invoices</c> por borrador (la del pedido primero, la
/// de flete sólo si la del pedido salió) y el resultado se guarda aparte: 2xx → <c>Created</c>/<c>Stamped</c> y el pedido se marca; error de
/// validación de Siigo → <c>Rejected</c> y el pedido sigue libre; timeout, 5xx, 429 o red → sigue <c>Pending</c>.
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
        return
        [
            .. SiigoInvoiceSupport.Current(prepared.Rows)
                .Select(row => SiigoInvoiceDto.From(row, prepared.OrderMarkFailed && row.Kind == SiigoInvoiceKind.Order)),
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
        foreach (var row in toSend)
        {
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

    // El tramo sin candado: la llamada a Siigo y su resultado, fila por fila. Un fallo no sabido (Pending) corta: la de flete no sale
    // antes de saber qué pasó con la del pedido.
    private async Task SendAsync(Prepared prepared, CancellationToken cancellationToken)
    {
        foreach (var row in prepared.ToSend.OrderBy(value => value.Kind))
        {
            if (row.Kind == SiigoInvoiceKind.Freight && !IsInvoiced(prepared.Rows))
            {
                // La del pedido no salió (rechazada, sin saber o todavía en camino): la de flete espera. Se suelta su reclamo.
                row.MarkTransient(clock.UtcNow);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            var outcome = await api.CreateInvoiceAsync(
                prepared.Credentials!, new SiigoInvoiceRequest(row.RequestJson), row.IdempotencyKey, cancellationToken);
            var proceed = await ApplyAsync(prepared, row, outcome, cancellationToken);
            if (!proceed)
            {
                break;
            }
        }
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
                row.MarkRejected(
                    SiigoInvoiceSupport.ErrorCodeOf(rejected.Errors),
                    SiigoInvoiceSupport.ErrorDetailOf(rejected.Errors),
                    rejected.ExpectedPaymentsTotal,
                    now);
                SiigoInvoiceSupport.Audit(auditRecorder, executionContext, row, SiigoAuditActions.InvoiceRejected, now);
                if (row.Kind == SiigoInvoiceKind.Order)
                {
                    // La de flete no salió nunca: se descarta, y el pedido queda libre para armarse otra vez.
                    foreach (var sibling in prepared.Rows.Where(value => value is { Kind: SiigoInvoiceKind.Freight, Status: SiigoInvoiceStatus.Pending }))
                    {
                        sibling.MarkFailed("siigo.invoice.order_rejected", "The order invoice was rejected, so the freight invoice was not sent.", now);
                    }
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
                return false;

            default:
                // Timeout, 5xx, 429 o red: no se sabe si Siigo la creó. Sigue Pending, se suelta el reclamo y el reintento manda lo mismo.
                row.MarkTransient(now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return false;
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
    // debe dejar el POST fallando para siempre. La fila queda Created, la respuesta lleva el aviso y el siguiente POST reintenta sólo la marca.
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
