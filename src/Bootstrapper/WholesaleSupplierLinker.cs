using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Suppliers.Application;
using Modules.Suppliers.Domain;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Bootstrapper;

/// <summary>
/// Wholesale → Suppliers: el proveedor vinculado en el tenant del distribuidor (D-2, D-8). <c>Name</c> es el nombre del tenant
/// mayorista y el resto sale de la empresa facturadora elegida al invitar; sin plazo de pago, categoría, notas, cuentas ni
/// contactos (el distribuidor los completa). <see cref="ISupplierLinking"/> ya es idempotente.
/// </summary>
internal sealed class WholesaleSupplierLinker(ISupplierLinking linking) : IWholesaleSupplierLinker
{
    private const string LinkedElsewhereCode = "suppliers.supplier.linked_elsewhere";

    public async Task<Guid> EnsureLinkedSupplierAsync(
        Guid distributorTenantId,
        Guid wholesalerTenantId,
        string wholesalerName,
        WholesaleCompanyRef company,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(company);
        var data = new SupplierWriteData(
            Name: wholesalerName,
            BusinessName: company.Name,
            IdentificationType: company.DocumentType,
            IdentificationNumber: company.DocumentNumber,
            Country: company.Country,
            CityId: company.CityId,
            CityName: company.CityName,
            Phone: company.Phone,
            Email: company.Email,
            Address: company.Address,
            PaymentTermDays: 0,
            Category: null,
            Notes: null,
            BankAccounts: [],
            Contacts: []);
        try
        {
            return await linking.EnsureLinkedAsync(distributorTenantId, wholesalerTenantId, actorUserId, data, cancellationToken);
        }
        // Un proveedor con ese documento ya está vinculado a otro mayorista (422 del dominio o 409 del índice): para quien acepta
        // es que esa organización no se puede vincular.
        catch (Exception exception) when (exception is DomainException { Code: LinkedElsewhereCode }
            or ResourceConflictException { Code: LinkedElsewhereCode })
        {
            throw new ResourceConflictException(
                WholesaleErrorCodes.DistributorAlreadyLinked,
                "A supplier with that identification is already linked to another wholesaler.",
                exception);
        }
    }
}
