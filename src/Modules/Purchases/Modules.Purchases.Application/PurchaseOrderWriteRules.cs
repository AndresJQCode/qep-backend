using FluentValidation;
using FluentValidation.Results;
using Modules.Purchases.Domain;

namespace Modules.Purchases.Application;

/// <summary>
/// Lo que comparten el alta, la edición y la vista previa: el mismo cuerpo, las mismas reglas y el mismo armado de líneas
/// (D-19). Los comandos son planos para que <c>errors</c> diga <c>Lines[0].Quantity</c> y no <c>Body.Lines…</c>.
/// </summary>
public interface IPurchaseOrderWriteCommand
{
    Guid TenantId { get; }

    Guid SupplierId { get; }

    string Currency { get; }

    string? Notes { get; }

    IReadOnlyList<PurchaseOrderLineInput> Lines { get; }
}

/// <summary>
/// La forma del cuerpo (D-8). El dominio no valida signos ni topes, así que todo eso vive acá. Lo que necesita otro módulo
/// (moneda del catálogo global, proveedor, variantes) y <c>lines: []</c> lo decide <see cref="PurchaseOrderLineBuilder"/> con
/// los códigos del contrato, no la validación.
/// </summary>
internal sealed class PurchaseOrderWriteRules : AbstractValidator<IPurchaseOrderWriteCommand>
{
    // Las columnas: cantidades numeric(10,2) y costos numeric(14,2). Un valor que no cabe es un 500 de Postgres (22003), y uno con
    // más de dos decimales se redondea recién al guardar: la vista previa y la respuesta del alta mostrarían un total y la siguiente
    // lectura otro (D-19). Los ceros a la derecha no cuentan: 1.50 cabe igual que 1.5.
    public const int QuantityPrecision = 10;

    public const int CostPrecision = 14;

    public const int Scale = 2;

    public PurchaseOrderWriteRules()
    {
        RuleFor(command => command.Notes).MaximumLength(PurchaseOrder.NotesMaxLength);
        RuleFor(command => command.Lines)
            .Must(lines => lines is null || lines.Count <= PurchaseOrder.MaxLines)
            .WithMessage($"A purchase order cannot have more than {PurchaseOrder.MaxLines} lines.");
        RuleForEach(command => command.Lines).ChildRules(line =>
        {
            line.RuleFor(input => input.VariantId).NotEqual(Guid.Empty);
            line.RuleFor(input => input.Quantity)
                .GreaterThan(0m)
                .PrecisionScale(QuantityPrecision, Scale, ignoreTrailingZeros: true);
            // D-11: opcional acá; con proveedor externo lo exige el armado de líneas, que sabe cuál es.
            line.RuleFor(input => input.UnitCost)
                .GreaterThan(0m)
                .PrecisionScale(CostPrecision, Scale, ignoreTrailingZeros: true)
                .When(input => input.UnitCost is not null);
        });
        // La segunda aparición es la que sobra: la primera queda como la buena, igual que en la pantalla.
        RuleFor(command => command.Lines).Custom((lines, context) =>
        {
            if (lines is null)
            {
                return;
            }

            var seen = new HashSet<Guid>();
            for (var index = 0; index < lines.Count; index++)
            {
                var variantId = lines[index].VariantId;
                if (variantId != Guid.Empty && !seen.Add(variantId))
                {
                    context.AddFailure(new ValidationFailure(
                        $"Lines[{index}].VariantId", "The variant is already in another line of the order."));
                }
            }
        });
    }
}

public sealed class CreatePurchaseOrderValidator : AbstractValidator<CreatePurchaseOrderCommand>
{
    public CreatePurchaseOrderValidator() => Include(new PurchaseOrderWriteRules());
}

public sealed class UpdatePurchaseOrderValidator : AbstractValidator<UpdatePurchaseOrderCommand>
{
    public UpdatePurchaseOrderValidator() => Include(new PurchaseOrderWriteRules());
}

public sealed class PreviewPurchaseOrderValidator : AbstractValidator<PreviewPurchaseOrderCommand>
{
    public PreviewPurchaseOrderValidator() => Include(new PurchaseOrderWriteRules());
}
