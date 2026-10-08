using BuildingBlocks.Application;
using BuildingBlocks.Domain.Pricing;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>Sin efectos: los totales que pinta la caja salen de aquí, nunca del frontend.</summary>
public sealed record PreviewPosSaleCommand(Guid TenantId, IReadOnlyList<PosPreviewLineRequest> Lines)
    : ICommand<PosPreviewResponse>;

public sealed class PreviewPosSaleValidator : AbstractValidator<PreviewPosSaleCommand>
{
    public PreviewPosSaleValidator()
    {
        RuleFor(command => command.Lines).NotEmpty().Must(lines => lines.Count <= PosLimits.MaxLines)
            .WithMessage("A sale cannot have more than 200 lines.");
        RuleForEach(command => command.Lines).NotNull().ChildRules(line =>
        {
            line.RuleFor(value => value.ProductId).NotEmpty();
            line.RuleFor(value => value.Quantity)
                .GreaterThan(0m).LessThanOrEqualTo(PosLimits.MaxQuantity)
                .Must(PosLimits.HasValidScale).WithMessage("The quantity accepts at most 2 decimals.");
            line.RuleFor(value => value.DiscountPercentage)
                .InclusiveBetween(0m, 100m)
                .Must(PosLimits.HasValidScale).WithMessage("The discount accepts at most 2 decimals.");
        });
    }
}

public sealed class PreviewPosSaleHandler(
    IPosProductLookup products,
    ICashSessionRepository sessions,
    IMembershipDirectory membershipDirectory,
    ITenantDefaultCurrency tenantDefaultCurrency,
    IExecutionContext executionContext,
    IValidator<PreviewPosSaleCommand> validator)
    : ICommandHandler<PreviewPosSaleCommand, PosPreviewResponse>
{
    public async Task<PosPreviewResponse> HandleAsync(PreviewPosSaleCommand command, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.SaleCreate);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var canDiscount = executionContext.HasPermission(PosPermissions.SaleDiscount);
        if (!canDiscount && command.Lines.Any(line => line.DiscountPercentage > 0))
        {
            throw new RequestForbiddenException(
                "pos.sale.discount_not_allowed", "Line discounts need the pos.sale.discount permission.");
        }

        // Distinct: el mismo producto puede venir en dos líneas (Review Focus 2).
        var found = await products.FindManyAsync(
            command.TenantId, command.Lines.Select(line => line.ProductId).Distinct().ToArray(), cancellationToken);

        var currency = await PosSellingCurrency.ResolveAsync(
            sessions, membershipDirectory, tenantDefaultCurrency, executionContext, command.TenantId, cancellationToken);
        var lines = command.Lines.Select(line => ToLine(line, found, currency)).ToArray();
        var sellable = lines.Where(line => line.Sellable).ToArray();
        var subtotal = VatIncludedLine.Round(sellable.Sum(line => line.Subtotal));
        var tax = VatIncludedLine.Round(sellable.Sum(line => line.TaxAmount));
        var discount = VatIncludedLine.Round(sellable.Sum(line => line.DiscountAmount));
        var total = subtotal + tax;

        // El mismo tope que PosSale.Create: si no se guarda, la caja no debe dejar cobrarlo.
        PosSale.EnsureAmountsFit(sellable.Select(line => line.UnitPrice!.Value), subtotal, discount, tax, total);

        return new PosPreviewResponse(
            lines, subtotal, tax, discount, total,
            ZeroTotalNotAllowed: total == 0 && !canDiscount);
    }

    private static PosPreviewLineResponse ToLine(
        PosPreviewLineRequest line, IReadOnlyDictionary<Guid, PosProductRef> found, string currency)
    {
        if (!found.TryGetValue(line.ProductId, out var product))
        {
            return new PosPreviewLineResponse(
                line.ProductId, null, null, line.Quantity, null, null, line.DiscountPercentage,
                0m, 0m, 0m, 0m, false, PosProductMapping.NotFound);
        }

        var price = product.PriceIn(currency);
        var reason = PosProductMapping.UnsellableReason(product, currency);
        if (reason is not null)
        {
            return new PosPreviewLineResponse(
                line.ProductId, product.Code, product.Name, line.Quantity, price, product.TaxPercentage,
                line.DiscountPercentage, 0m, 0m, 0m, 0m, false, reason);
        }

        var amounts = VatIncludedLine.Compute(
            line.Quantity, price!.Value, line.DiscountPercentage, product.TaxPercentage);
        return new PosPreviewLineResponse(
            line.ProductId, product.Code, product.Name, line.Quantity, price, product.TaxPercentage,
            line.DiscountPercentage, amounts.DiscountAmount, amounts.TaxAmount, amounts.Subtotal, amounts.LineTotal,
            true, null);
    }
}
