using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Sumar comprobantes a un pedido que ya existe, y corregir el monto de los que ya tenía
/// cargados (a pedido, 2026-09), con el estado de pago que corresponda a lo cargado. Ruta
/// hermana de <see cref="ApproveOrderCommand"/> y no un campo del PATCH que no existe: el pedido
/// no se edita en general, esto es un único gesto puntual —"cargué o corregí lo que hacía
/// falta"— para destrabar "Aprobar pedido" cuando el pago no había quedado completo o correcto.
/// </summary>
public sealed record AddOrderPaymentProofsCommand(
    Guid TenantId,
    Guid QuotationId,
    string PaymentStatus,
    string? Notes,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs,
    IReadOnlyCollection<OrderPaymentProofUpdateRequest> UpdatedProofs) : ICommand<OrderDto>;

public sealed class AddOrderPaymentProofsValidator
    : AbstractValidator<AddOrderPaymentProofsCommand>
{
    private static readonly string[] ValidPaymentStatuses =
        Enum.GetNames<OrderPaymentStatus>();

    public AddOrderPaymentProofsValidator()
    {
        RuleFor(command => command.PaymentStatus)
            .Must(status => ValidPaymentStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage(
                $"PaymentStatus must be one of: {string.Join(", ", ValidPaymentStatuses)}.");
        RuleFor(command => command.Notes)
            .MaximumLength(Order.NotesMaxLength)
            .When(command => command.Notes is not null);
        RuleForEach(command => command.PaymentProofs).SetValidator(new OrderPaymentProofRequestValidator());
        RuleForEach(command => command.UpdatedProofs).SetValidator(new OrderPaymentProofUpdateRequestValidator());
    }
}

internal sealed class OrderPaymentProofUpdateRequestValidator
    : AbstractValidator<OrderPaymentProofUpdateRequest>
{
    public OrderPaymentProofUpdateRequestValidator()
    {
        RuleFor(request => request.ProofId).NotEmpty();
        RuleFor(request => request.Amount).GreaterThan(0);
        RuleFor(request => request.NewFileId)
            .NotEqual(Guid.Empty)
            .When(request => request.NewFileId is not null);
    }
}

public sealed class AddOrderPaymentProofsHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationFileLookup fileLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IOrderPaymentProofEventPublisher paymentProofEvents,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddOrderPaymentProofsCommand> validator)
    : ICommandHandler<AddOrderPaymentProofsCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        AddOrderPaymentProofsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var order = await repository.FindByQuotationIdAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw OrderNotFound.For(command.QuotationId);

        foreach (var proof in command.PaymentProofs)
        {
            await OrderPaymentProofResolver.ResolveAsync(
                fileLookup, command.TenantId, proof.FileId, cancellationToken);
        }

        foreach (var update in command.UpdatedProofs)
        {
            if (update.NewFileId is { } newFileId)
            {
                await OrderPaymentProofResolver.ResolveAsync(
                    fileLookup, command.TenantId, newFileId, cancellationToken);
            }
        }

        var uploadedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        var paymentStatus = Enum.Parse<OrderPaymentStatus>(command.PaymentStatus, ignoreCase: true);

        // Sólo se publica lo que de verdad cambia de archivo: un comprobante nuevo siempre, y uno
        // que se reemplaza (a pedido, 2026-09-15) — corregir sólo el monto no toca el archivo.
        // Antes del dominio y con rollback, igual que al convertir (P7): si otra persona acaba de
        // aprobar el pedido, AddPaymentProofs lo rechaza con order.order.not_pending después de
        // copiar, y esas copias se borran.
        var copies = new PaymentProofCopies(paymentProofPublisher);
        // La clave vieja de cada comprobante que se reemplaza: capturada ANTES de mutar el
        // agregado, porque después de AddPaymentProofs ya no queda forma de leerla. Sólo se borra
        // si el guardado termina saliendo bien — best-effort, fuera del try de arriba: un archivo
        // público huérfano no es motivo para fallar un request que sí guardó.
        var oldPublicKeysToReplace = command.UpdatedProofs
            .Where(update => update.NewFileId is not null)
            .Select(update => order.PaymentProofs
                .FirstOrDefault(proof => proof.Id.Value == update.ProofId)
                ?.PublicStorageKey)
            .Where(publicKey => publicKey is not null)
            .Cast<string>()
            .ToArray();

        try
        {
            var proofs = await copies.PublishAsync(
                command.TenantId, command.PaymentProofs, cancellationToken);

            var updatedProofs = new List<OrderPaymentProofAmountUpdate>(command.UpdatedProofs.Count);
            foreach (var update in command.UpdatedProofs)
            {
                string? newPublicStorageKey = null;
                if (update.NewFileId is { } newFileId)
                {
                    newPublicStorageKey = await copies.PublishReplacementAsync(
                        command.TenantId, newFileId, cancellationToken);
                }

                updatedProofs.Add(new OrderPaymentProofAmountUpdate(
                    new OrderPaymentProofId(update.ProofId), update.Amount, update.NewFileId,
                    newPublicStorageKey));
            }

            order.AddPaymentProofs(
                proofs,
                paymentStatus,
                command.Notes,
                uploadedBy,
                now,
                updatedProofs);

            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "quotation.order.payment_proofs_added",
                order.Id.ToString(),
                "success",
                now);
            // D9 y D19 (spec 2026-09-16): los comprobantes nuevos y los archivos de reemplazo; corregir
            // sólo un monto no mueve nada.
            var attached = PaymentProofCopies.AttachedFrom(proofs)
                .Concat(PaymentProofCopies.AttachedFromReplacements(updatedProofs))
                .ToArray();
            if (attached.Length > 0)
            {
                paymentProofEvents.PublishAttached(command.TenantId, order.Id, attached, now);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await copies.RollbackAsync();
            throw;
        }

        foreach (var oldPublicKey in oldPublicKeysToReplace)
        {
            try
            {
                await paymentProofPublisher.DeleteAsync(oldPublicKey, CancellationToken.None);
            }
            catch
            {
                // Best-effort, mismo criterio que PaymentProofCopies.RollbackAsync: el archivo
                // viejo queda huérfano en el bucket público, pero el pedido ya guardó el cambio.
            }
        }

        return order.ToDto();
    }
}
