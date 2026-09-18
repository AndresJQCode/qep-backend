using FluentValidation;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Un comprobante nuevo del borrador (spec 2026-09-17). <paramref name="FileId"/> es null
/// en el cálculo previo: los archivos se suben recién al guardar.</summary>
public sealed record OrderEditProofAddition(Guid? FileId, decimal Amount);

/// <summary>Los comprobantes del borrador. <see cref="Update"/> sólo lleva los que cambian; los no
/// mencionados se conservan.</summary>
public sealed record OrderEditProofs(
    IReadOnlyList<OrderEditProofAddition> Add,
    IReadOnlyList<OrderPaymentProofUpdateRequest> Update,
    IReadOnlyList<Guid> RemoveIds)
{
    public static readonly OrderEditProofs None = new([], [], []);
}

/// <summary>El estado deseado completo de «Editar pedido» (spec 2026-09-17, decisión 2): mismo
/// cuerpo para el guardado y el cálculo previo, así que las reglas se escriben una vez.</summary>
public interface IOrderEdits
{
    IReadOnlyList<OrderItemAddition> Items { get; }

    OrderEditProofs Proofs { get; }

    string? Notes { get; }
}

/// <summary>
/// Las reglas de la sección «Validación» del spec 2026-09-17. Abstracta a propósito: el escaneo de
/// validadores del composition root sólo registra las dos concretas. <paramref name="requireFileIds"/>
/// separa el guardado del cálculo previo, que todavía no tiene archivos (el <c>newFileId</c> sí se
/// valida en los dos: si llega, no puede ser <see cref="Guid.Empty"/>).
/// </summary>
public abstract class OrderEditsValidator<TEdits> : AbstractValidator<TEdits>
    where TEdits : IOrderEdits
{
    protected OrderEditsValidator(bool requireFileIds)
    {
        RuleFor(edits => edits.Items)
            .Must(items => items.Count > 0)
            .WithMessage("At least one product is required.");
        RuleForEach(edits => edits.Items).ChildRules(item =>
        {
            item.RuleFor(line => line.ProductId).NotEmpty();
            item.RuleFor(line => line.Quantity).GreaterThan(0m);
        });
        RuleFor(edits => edits.Items)
            .Must(items => !HasDuplicates(items.Select(line => line.ProductId)))
            .WithMessage("The same product cannot appear more than once.");

        RuleForEach(edits => edits.Proofs.Add).ChildRules(addition =>
        {
            addition.RuleFor(value => value.Amount).GreaterThan(0m);
            if (requireFileIds)
            {
                addition.RuleFor(value => value.FileId).NotNull().NotEqual(Guid.Empty);
            }
        });
        RuleForEach(edits => edits.Proofs.Update).SetValidator(new OrderPaymentProofUpdateRequestValidator());
        RuleFor(edits => edits.Proofs.Update)
            .Must(updates => !HasDuplicates(updates.Select(update => update.ProofId)))
            .WithMessage("The same payment proof cannot be updated more than once in a request.");
        RuleFor(edits => edits.Proofs)
            .Must(proofs => !proofs.Update.Select(update => update.ProofId).Intersect(proofs.RemoveIds).Any())
            .WithMessage("A payment proof cannot be updated and removed in the same request.");
        // Mismo motivo que AddOrderPaymentProofsValidator (revisión final, I2): un archivo, un adjunto.
        RuleFor(edits => edits.Proofs)
            .Must(proofs => !HasDuplicates(proofs.Add
                .Where(addition => addition.FileId is not null)
                .Select(addition => addition.FileId!.Value)
                .Concat(proofs.Update
                    .Where(update => update.NewFileId is not null)
                    .Select(update => update.NewFileId!.Value))))
            .WithMessage("The same file cannot be attached more than once in a request.");

        RuleFor(edits => edits.Notes)
            .MaximumLength(Order.NotesMaxLength)
            .When(edits => edits.Notes is not null);
    }

    private static bool HasDuplicates<TValue>(IEnumerable<TValue> values)
    {
        var seen = new HashSet<TValue>();
        return values.Any(value => !seen.Add(value));
    }
}
