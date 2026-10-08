using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <param name="Id">Lo genera el cliente al abrir el cobro: es la clave de idempotencia.</param>
public sealed record CreatePosSaleCommand(
    Guid TenantId,
    Guid Id,
    Guid CashSessionId,
    IReadOnlyList<PosSaleLineRequest> Lines,
    IReadOnlyList<PosPaymentRequest> Payments) : ICommand<PosSaleCreation>;

/// <param name="Created">false en una repetición reconocida: el endpoint responde 200 en vez de 201.</param>
public sealed record PosSaleCreation(PosSaleResponse Sale, bool Created);

public sealed class CreatePosSaleValidator : AbstractValidator<CreatePosSaleCommand>
{
    private static readonly string[] Methods =
        [nameof(PosPaymentMethod.Cash), nameof(PosPaymentMethod.Card), nameof(PosPaymentMethod.Transfer)];

    public CreatePosSaleValidator()
    {
        RuleFor(command => command.Id).NotEmpty();
        RuleFor(command => command.CashSessionId).NotEmpty();
        RuleFor(command => command.Lines).NotEmpty()
            .Must(lines => lines is null || lines.Count <= PosLimits.MaxLines).WithMessage("A sale cannot have more than 200 lines.");
        RuleForEach(command => command.Lines).NotNull().ChildRules(line =>
        {
            line.RuleFor(value => value.ProductId).NotEmpty();
            line.RuleFor(value => value.Quantity)
                .GreaterThan(0m).LessThanOrEqualTo(PosLimits.MaxQuantity)
                .Must(PosLimits.HasValidScale).WithMessage("The quantity accepts at most 2 decimals.");
            line.RuleFor(value => value.DiscountPercentage)
                .InclusiveBetween(0m, 100m)
                .Must(PosLimits.HasValidScale).WithMessage("The discount accepts at most 2 decimals.");
            line.RuleFor(value => value.ExpectedUnitPrice)
                .GreaterThanOrEqualTo(0m)
                .Must(PosLimits.HasValidScale).WithMessage("The price accepts at most 2 decimals.");
            line.RuleFor(value => value.ExpectedTaxPercentage).InclusiveBetween(0, 100);
        });
        RuleFor(command => command.Payments).NotEmpty()
            .Must(payments => payments is null || payments.Count <= PosLimits.MaxPayments).WithMessage("A sale cannot have more than 5 payments.");
        RuleForEach(command => command.Payments).NotNull().ChildRules(payment =>
        {
            payment.RuleFor(value => value.Method)
                .Must(method => Methods.Contains(method, StringComparer.Ordinal))
                .WithMessage("The method must be Cash, Card or Transfer.");
            payment.When(value => value.Method == nameof(PosPaymentMethod.Cash), () =>
            {
                // En Cash el Amount lo calcula el servidor (spec, decisión 41).
                payment.RuleFor(value => value.Amount).Null().WithMessage("A cash payment carries only tendered.");
                payment.RuleFor(value => value.Tendered).NotNull()
                    .InclusiveBetween(0m, PosLimits.MaxCashAmount)
                    .Must(tendered => tendered is null || PosLimits.HasValidScale(tendered.Value))
                    .WithMessage("The tendered cash accepts at most 2 decimals.");
                // Decisión P11.
                payment.RuleFor(value => value.Reference).Null().WithMessage("A cash payment has no reference.");
            }).Otherwise(() =>
            {
                payment.RuleFor(value => value.Amount).NotNull().GreaterThan(0m)
                    .Must(amount => amount is null || PosLimits.HasValidScale(amount.Value))
                    .WithMessage("The amount accepts at most 2 decimals.");
                payment.RuleFor(value => value.Tendered).Null().WithMessage("Only cash carries tendered.");
                payment.RuleFor(value => value.Reference).MaximumLength(PosLimits.ReferenceMaxLength);
            });
        });
    }
}

public sealed class CreatePosSaleHandler(
    ICashSessionRepository sessions,
    IPosSaleRepository sales,
    IPosSaleNumberGenerator numbers,
    IPosUnitOfWork unitOfWork,
    IPosSaleIdLock saleIdLock,
    IPosAuditPublisher auditPublisher,
    IPosProductLookup products,
    IPosCashierLookup cashiers,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    ITenantClock tenantClock,
    IValidator<CreatePosSaleCommand> validator)
    : ICommandHandler<CreatePosSaleCommand, PosSaleCreation>
{
    public async Task<PosSaleCreation> HandleAsync(CreatePosSaleCommand command, CancellationToken cancellationToken)
    {
        // 1. Lo que se responde aquí (authorization.denied, validation.failed) sale antes de buscar
        // la repetición: el cliente no lo toma como definitivo al reintentar un cobro incierto.
        // Ningún 422 con código pos.* puede salir antes del paso 2, porque el frontend sí trata esos
        // como definitivos: el id vacío lo para el validador (validation.failed) antes de que
        // new PosSaleId lance pos.sale.id_required, y la huella no lanza.
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.SaleCreate);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var cashier = await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var fingerprint = PosSaleFingerprint.Compute(command);
        var saleId = new PosSaleId(command.Id);
        PosSale sale;

        // La transacción y el candado van antes de todo lo demás: dos requests con el mismo id se
        // serializan, y un reintento que llega con el primer envío en vuelo espera su commit o su
        // rollback antes de buscar (spec, decisión 55). Lo que lanzan los pasos 3 a 6 revierte la
        // transacción al salir, sin haber escrito nada.
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await saleIdLock.AcquireAsync(command.TenantId, saleId, cancellationToken);

            // 2. Repetición: antes de mirar la caja, para que reintentar sea idempotente aunque la
            // caja ya no esté abierta.
            if (await FindReplayAsync(command.TenantId, saleId, cashier, fingerprint, cancellationToken) is { } replay)
            {
                return new PosSaleCreation(await RespondAsync(replay, cancellationToken), Created: false);
            }

            // 3. Caja.
            var session = await sessions.FindOpenByCashierAsync(command.TenantId, cashier, cancellationToken)
                ?? throw new PosDomainException("pos.session.not_open", "The cashier has no open cash session.");
            if (session.Id.Value != command.CashSessionId)
            {
                throw new PosDomainException(
                    "pos.sale.session_mismatch", "The cash session sent is not the cashier's open one.");
            }

            // 4. Productos: el precio y la tasa que se cobran son siempre los del catálogo.
            var lines = await ResolveLinesAsync(command, session, cancellationToken);

            // 5. Descuento.
            var canDiscount = executionContext.HasPermission(PosPermissions.SaleDiscount);
            if (!canDiscount && command.Lines.Any(line => line.DiscountPercentage > 0))
            {
                throw DiscountNotAllowed();
            }

            // 6. Venta (valida líneas y pagos). Un total en cero sin descuento sólo sale de
            // productos con precio 0, y regalar también es un descuento.
            var now = clock.UtcNow;
            sale = PosSale.Create(
                saleId, fingerprint, session, lines, command.Payments.Select(ToInput).ToArray(), now);
            if (sale.Total == 0 && !canDiscount)
            {
                throw DiscountNotAllowed();
            }

            // 7. Número adentro de la misma transacción y después de todas las validaciones: un
            // 422 o un 412 no gastan número.
            sale.AssignNumber(await numbers.NextAsync(command.TenantId, cancellationToken));
            sales.Add(sale);
            session.RegisterSale(sale, now);
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "pos.sale.created", "pos_sale",
                sale.Id.ToString(), "success", AuditFields(sale), now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // 8. Choque al guardar: un duplicado en paralelo choca casi siempre en la Version de la caja
        // (412) y a veces en PK_sales. Los dos se tratan igual: limpiar, releer una vez.
        catch (Exception exception) when (exception is RequestConcurrencyException
            || exception is PosDomainException { Code: "pos.sale.id_taken" })
        {
            await unitOfWork.ResetAsync(cancellationToken);
            if (await FindReplayAsync(command.TenantId, saleId, cashier, fingerprint, cancellationToken) is { } winner)
            {
                return new PosSaleCreation(await RespondAsync(winner, cancellationToken), Created: false);
            }

            if (exception is PosDomainException)
            {
                // La PK es de una venta de otro tenant: terminal, el cliente genera otro id.
                throw new PosDomainException("pos.sale.id_conflict", "The sale id is already in use.");
            }

            throw;
        }

        return new PosSaleCreation(await RespondAsync(sale, cancellationToken), Created: true);
    }

    private async Task<PosSale?> FindReplayAsync(
        Guid tenantId, PosSaleId saleId, MemberId cashier, string fingerprint, CancellationToken cancellationToken)
    {
        var existing = await sales.FindAsync(tenantId, saleId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        // Otro cajero u otro carrito con el mismo id: la venta vieja nunca se presenta como la nueva.
        return existing.CashierId == cashier && string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal)
            ? existing
            : throw new PosDomainException("pos.sale.id_conflict", "The sale id belongs to another sale.");
    }

    private async Task<PosSaleLineInput[]> ResolveLinesAsync(
        CreatePosSaleCommand command, CashSession session, CancellationToken cancellationToken)
    {
        var found = await products.FindManyAsync(
            command.TenantId, command.Lines.Select(line => line.ProductId).Distinct().ToArray(), cancellationToken);

        return command.Lines.Select(line =>
        {
            if (!found.TryGetValue(line.ProductId, out var product))
            {
                throw new PosDomainException("pos.sale.product_not_found", $"Product '{line.ProductId}' was not found.");
            }

            if (!product.IsActive)
            {
                throw new PosDomainException("pos.sale.product_inactive", $"Product '{product.Code}' is inactive.");
            }

            // La lista de la caja, nunca la otra (PosProductMapping.PriceIn).
            if (product.PriceIn(session.Currency) is not { } price)
            {
                throw new PosDomainException(
                    "pos.sale.product_price_unavailable", $"Product '{product.Code}' has no {session.Currency} price.");
            }

            // Basta con que cambie uno: un cambio sólo de tasa no mueve el total (IVA incluido) pero
            // sí el desglose del ticket.
            if (price != line.ExpectedUnitPrice || product.TaxPercentage != line.ExpectedTaxPercentage)
            {
                throw new PosDomainException(
                    "pos.sale.price_changed", $"The price or tax rate of '{product.Code}' changed.");
            }

            return new PosSaleLineInput(
                product.Id, product.Code, product.Name, line.Quantity, price, line.DiscountPercentage, product.TaxPercentage);
        }).ToArray();
    }

    private Task<PosSaleResponse> RespondAsync(PosSale sale, CancellationToken cancellationToken) =>
        PosSaleResponses.BuildAsync(sale, sessions, cashiers, tenantClock, cancellationToken);

    private static PosPaymentInput ToInput(PosPaymentRequest payment) =>
        new(Enum.Parse<PosPaymentMethod>(payment.Method), payment.Amount, payment.Tendered, payment.Reference);

    private static RequestForbiddenException DiscountNotAllowed() =>
        new("pos.sale.discount_not_allowed", "Discounts and zero-total sales need the pos.sale.discount permission.");

    // changedFields es el único campo libre del contrato de auditoría (spec, «Descuentos en la
    // auditoría»): discount:{position}:{productCode}:{discountPercentage} y total:0.
    private static string[] AuditFields(PosSale sale)
    {
        var fields = sale.Lines
            .Where(line => line.DiscountPercentage > 0)
            .Select(line => string.Create(
                CultureInfo.InvariantCulture,
                $"discount:{line.Position}:{line.ProductCode}:{line.DiscountPercentage:0.##}"))
            .ToList();
        if (sale.Total == 0)
        {
            fields.Add("total:0");
        }

        return [.. fields];
    }
}
