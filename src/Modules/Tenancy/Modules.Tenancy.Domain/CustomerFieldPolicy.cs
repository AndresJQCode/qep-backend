namespace Modules.Tenancy.Domain;

/// <summary>
/// Cuándo exige el tenant un dato del cliente: al guardar la ficha, al facturar, o ambos.
/// </summary>
public sealed record FieldRequirement(bool OnCustomerSave, bool OnBilling)
{
    public static readonly FieldRequirement Always = new(true, true);

    /// <summary>Exigido al guardar implica exigido al facturar: una ficha completa ya lo trae. La combinación
    /// (true, false) no significa nada y se rechaza.</summary>
    public bool IsConsistent => !OnCustomerSave || OnBilling;
}

/// <summary>Qué datos del cliente exige el tenant y en qué momento (spec 2026-11-09).</summary>
public sealed record CustomerFieldPolicy(FieldRequirement Document, FieldRequirement City)
{
    // EF no enlaza navegaciones owned a parámetros de constructor: materializa con este y asigna las
    // dos propiedades. Nada del dominio lo llama.
    private CustomerFieldPolicy()
        : this(FieldRequirement.Always, FieldRequirement.Always)
    {
    }

    public const string InvalidCode = "tenancy.settings.customer_field_policy.invalid";

    public static readonly CustomerFieldPolicy Default = new(FieldRequirement.Always, FieldRequirement.Always);

    public bool IsConsistent => Document.IsConsistent && City.IsConsistent;
}
