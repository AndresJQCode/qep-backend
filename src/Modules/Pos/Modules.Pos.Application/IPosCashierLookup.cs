namespace Modules.Pos.Application;

/// <summary>Nombre visible de una membresía: DisplayName ?? Email, como QuotationAdvisorLookup.</summary>
public interface IPosCashierLookup
{
    Task<string?> FindNameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken);
}
