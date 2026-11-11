using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Marcar como pagado tiene permiso propio (<c>quotations.order.approve</c>) y no el de gestión: quien
/// registra el pedido —la asesora, con <c>quotations.order.manage</c>— no es quien verifica la plata.
/// Con el ajuste <c>OnOrderApproval</c> del tenant (spec 2026-10-26) exige el tipo de persona de a quien
/// se factura. Las pruebas del handler llegan con él (spec 2026-11-10, Tarea 5); acá quedan los armados.
/// </summary>
public sealed class MarkOrderPaidHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);

    private static StubTenantDirectory Directory(
        PersonTypeRequirement requirement, string tenantCountry = "CO") =>
        new()
        {
            Regional = new(
                "America/Bogota", "dd/MM/yyyy", "COP", TenantNumberFormats.CommaDecimal,
                tenantCountry, requirement)
        };

    private static StubQuotationCustomerLookup Customer(string? personType, string? country = "CO") =>
        new(new QuotationCustomerRef(
            Guid.CreateVersion7(), TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false,
            PersonType: personType, Country: country,
            IdentificationNumber: "900123456", IdentificationType: "NIT"));

    private static QuotationParties BillingParties(PartyPersonType? personType) =>
        QuotationParties.Empty with
        {
            Billing = new QuotationPartyDetails
            {
                Name = "Otra S.A.S.",
                IdentificationNumber = "900123456",
                IdentificationType = PartyIdentificationType.Nit,
                PersonType = personType,
                Phone = "3001234567",
                Email = "otra@example.com",
                Address = "Calle 1 # 2-3",
                DepartmentId = Guid.CreateVersion7(),
                CityId = Guid.CreateVersion7(),
            },
            BillingWithRetention = false,
            BillingVatSurplus = false,
        };

    private static Order NewRow(QuotationParties? parties = null) =>
        OrderBuilders.Pending(
            TenantId, AdvisorId, Now,
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)],
            parties: parties);

    /// <summary>Al revés de <see cref="StubExecutionContext"/>: concede sólo lo que se le nombra.
    /// Lo que estas pruebas miran es que un permiso de más (gestionar) no alcanza, y eso no se
    /// puede decir negando uno.</summary>
    private sealed class GrantedPermissionsExecutionContext(
        Guid subjectId, Guid tenantId, params string[] grantedPermissions) : IExecutionContext
    {
        public Guid SubjectId { get; } = subjectId;

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => grantedPermissions.Contains(permission);
    }
}
