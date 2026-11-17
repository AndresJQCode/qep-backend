using FluentValidation;
using Modules.Ai.Application;
using Modules.Authorization.Application;
using Modules.Identity.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-29 §8.3 y P-4: el borrador de cotización del agente sobre <see cref="IExternalQuotationWriter"/>. Traduce los rechazos de
/// Quotations a lo que el agente necesita decidir: un cliente que no se puede cotizar traspasa (P-4); cualquier otro rechazo vuelve al modelo
/// con su código. Corre fuera de la transacción de Ai (D-A13): la escritura es la transacción propia de Quotations.
/// </summary>
internal sealed class AiQuotationDrafts(IExternalQuotationWriter writer) : IAiQuotationDrafts
{
    /// <summary>Los códigos de <c>QuotationCustomerEligibility</c>, en su orden, más el de un cliente borrado mientras se guardaba
    /// (<c>CustomerReferenceWrites</c> usa el mismo <c>client_not_found</c>).</summary>
    internal static readonly IReadOnlySet<string> CustomerCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "quotation.quotation.client_not_found",
        "quotation.quotation.client_incomplete",
        "quotation.quotation.client_cuc_missing",
        "quotation.quotation.client_inactive",
    };

    /// <summary>El código del 422 de FluentValidation (<c>ApiExceptionHandler</c>): el cuerpo no pasó las reglas de la creación.</summary>
    internal const string ValidationFailed = "validation.failed";

    public async Task<AiQuotationDraftResult> CreateAsync(AiQuotationDraftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var account = request.BillingAccount;
            var created = await writer.CreateDraftAsync(
                new ExternalQuotationDraft(
                    request.TenantId,
                    request.CustomerId,
                    request.AdvisorMemberId,
                    new QuotationBillingAccountRequest(account.CompanyId, account.BankName, account.AccountNumber, account.Currency),
                    [.. request.Lines.Select(line => new QuotationItemAddition(line.VariantId, line.Quantity))],
                    request.Note,
                    request.ExternalKey),
                cancellationToken);
            return AiQuotationDraftResult.Created(created.QuotationNumber, created.AdvisorMembershipId);
        }
        catch (QuotationsDomainException error) when (CustomerCodes.Contains(error.Code))
        {
            return AiQuotationDraftResult.CustomerNotReady(error.Code);
        }
        catch (QuotationsDomainException error)
        {
            return AiQuotationDraftResult.Rejected(error.Code);
        }
        catch (ValidationException)
        {
            return AiQuotationDraftResult.Rejected(ValidationFailed);
        }
    }
}

/// <summary>P-4: quién puede quedar como asesor de un borrador. Miembro activo con <see cref="QuotationsPermissions.QuotationManage"/>, la
/// misma regla de membresía + roles → permisos que <c>MessagingAssignees</c> usa para responder conversaciones, sin caché.</summary>
internal sealed class AiQuotationAdvisors(IMembershipRepository memberships, IUserDirectory users, ITenantRoleCatalog roles)
    : IAiQuotationAdvisors
{
    public async Task<bool> CanQuoteAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
    {
        var membership = await memberships.FindByIdAsync(new MembershipId(memberId), new TenantId(tenantId), cancellationToken);
        return membership is { State: MembershipState.Active } && await GrantsQuotingAsync(tenantId, membership, cancellationToken);
    }

    public async Task<IReadOnlyList<AiMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var result = new List<AiMemberRef>();
        foreach (var membership in await memberships.ListByTenantAsync(new TenantId(tenantId), cancellationToken))
        {
            if (membership.State != MembershipState.Active || !await GrantsQuotingAsync(tenantId, membership, cancellationToken))
            {
                continue;
            }

            // Como MessagingAssignees: el nombre de la membresía o, sin él, el correo.
            var name = !string.IsNullOrWhiteSpace(membership.DisplayName)
                ? membership.DisplayName
                : await users.GetEmailAsync(membership.UserId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.Add(new AiMemberRef(membership.Id.Value, name));
            }
        }

        return [.. result.OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private async Task<bool> GrantsQuotingAsync(Guid tenantId, Membership membership, CancellationToken cancellationToken) =>
        (await roles.PermissionsForAsync(tenantId, membership.Roles, cancellationToken))
            .Contains(QuotationsPermissions.QuotationManage, StringComparer.Ordinal);
}
