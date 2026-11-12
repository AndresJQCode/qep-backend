using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// El cuerpo del <c>PUT /settings</c> para la política de datos del cliente. Las banderas son
/// anulables para que una ausente sea un <c>validation.failed</c> con su campo y no un
/// <c>false</c> silencioso: si la política viene, vienen las cuatro (spec 2026-11-09 §6.7).
/// </summary>
public sealed record CustomerFieldPolicyInput(FieldRequirementInput? Document, FieldRequirementInput? City)
{
    /// <summary>Sólo se llama tras validar: el validador garantiza que ninguna pieza es nula.</summary>
    public CustomerFieldPolicy ToDomain() => new(Document!.ToDomain(), City!.ToDomain());
}

public sealed record FieldRequirementInput(bool? OnCustomerSave, bool? OnBilling)
{
    public FieldRequirement ToDomain() => new(OnCustomerSave!.Value, OnBilling!.Value);
}
