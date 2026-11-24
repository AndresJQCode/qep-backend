using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Wholesale → Customers (D-7): el cliente del distribuidor en el tenant del mayorista, por el camino y las reglas de Customers
/// (origen <c>wholesale</c>, con quien invitó como actor). Decisión del owner, 2026-11-24: ciudad, dirección y contacto del
/// distribuidor también van al cliente. Con teléfono y ciudad se intenta la ficha completa
/// (<see cref="ICustomerProvisioning.CreateCompleteAsync"/>: CUC, dirección, ciudad), y el nombre del contacto es la etiqueta de la
/// dirección principal de la libreta. Esa fila sólo existe con ciudad colombiana y calle (<c>CustomerMapping.ToFirstAddress</c>
/// es la regla de Customers): para un distribuidor de afuera o sin dirección, el contacto no se guarda y está bien. Si falta el
/// teléfono, o Customers rechaza la ficha completa (ciudad inexistente, política del tenant), cae al camino anterior
/// (<see cref="ICustomerProvisioning.CreateFromRegisterAsync"/>, ficha incompleta) sin romper la aceptación. Si el documento ya
/// es de un cliente —uno previo del mayorista o el de un intento anterior de esta aceptación—, se reutiliza y no se actualiza
/// (copia de <see cref="ServiceContractsCustomerProvisioning"/>).
/// </summary>
internal sealed class WholesaleCustomerProvisioning(ICustomerProvisioning customers, CustomersDbContext customersContext)
    : IWholesaleCustomerProvisioning
{
    private const string Source = "wholesale";

    public async Task<Guid> EnsureCustomerAsync(
        Guid wholesalerTenantId,
        Guid actorUserId,
        string businessName,
        string country,
        string identificationType,
        string identificationNumber,
        string email,
        string? phone,
        Guid? cityId,
        string? cityName,
        string? address,
        string contactName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(phone) && (cityId is not null || !string.IsNullOrWhiteSpace(cityName))
                && await TryCreateCompleteAsync(
                    wholesalerTenantId,
                    actorUserId,
                    new CompleteCustomerData(
                        businessName,
                        identificationType,
                        identificationNumber,
                        phone,
                        email,
                        address ?? string.Empty,
                        country,
                        cityId,
                        cityName,
                        ClassificationId: null,
                        AddressLabel: contactName),
                    cancellationToken) is { } complete)
            {
                return complete;
            }

            var registered = await customers.CreateFromRegisterAsync(
                wholesalerTenantId,
                actorUserId,
                new RegisterCustomerData(businessName, identificationType, identificationNumber, phone, email, country),
                Source,
                cancellationToken);
            return registered.Id;
        }
        catch (CustomersDomainException exception) when (exception.Code == CustomersErrorCodes.IdentificationTaken)
        {
            // El documento ya tiene cliente: se relee y se usa ése. Un cliente que no entró quedaría Added en el contexto de
            // Customers y el próximo SaveChanges del scope lo reintentaría.
            customersContext.ChangeTracker.Clear();
            var type = IdentificationTypeParser.Parse(identificationType);
            var number = identificationNumber.Trim();
            var existing = await customersContext.Customers.AsNoTracking()
                .Where(customer => customer.TenantId == wholesalerTenantId
                    && customer.MergedIntoId == null
                    && customer.IdentificationType == type
                    && customer.IdentificationNumber == number)
                .Select(customer => customer.Id)
                .ToListAsync(cancellationToken);
            return existing.Count == 1
                ? existing[0].Value
                : throw new InvalidOperationException(
                    "The identification was taken but its customer cannot be read back.", exception);
        }
        catch
        {
            customersContext.ChangeTracker.Clear();
            throw;
        }
    }

    // null = Customers no aceptó la ficha completa (validación o ciudad/clasificación que no existe) y el llamador cae al
    // incompleto. «Documento tomado» no es rechazo: CreateCompleteAsync ya devuelve el cliente existente con Created en false.
    // Un intento fallido pudo dejar entidades Added en el contexto: se limpian antes de caer, o el próximo SaveChanges las
    // reintentaría.
    private async Task<Guid?> TryCreateCompleteAsync(
        Guid wholesalerTenantId, Guid actorUserId, CompleteCustomerData data, CancellationToken cancellationToken)
    {
        try
        {
            var created = await customers.CreateCompleteAsync(wholesalerTenantId, data, cancellationToken, actorUserId, Source);
            return created.Id;
        }
        catch (Exception exception) when (
            exception is FluentValidation.ValidationException
            || exception is CustomersDomainException { Code: not CustomersErrorCodes.IdentificationTaken })
        {
            customersContext.ChangeTracker.Clear();
            return null;
        }
    }
}
