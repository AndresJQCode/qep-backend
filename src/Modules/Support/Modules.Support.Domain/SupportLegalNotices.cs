namespace Modules.Support.Domain;

/// <summary>The fixed texts of the portal's legal steps (spec 2026-11-15 §6.4 and §8, plan L7 and L8).</summary>
public static class SupportLegalNotices
{
    /// <summary>The characterization notice at the foot of the requester's data step (spec §8): the same for every tenant and not
    /// editable. It travels in <c>GET /form</c> and is copied into the snapshot of the <c>PrivacyNotice</c> acceptance.</summary>
    public const string Characterization =
        "Los datos solicitados en este formulario serán tratados para caracterizar a los usuarios de la Entidad y procurar una mejora continua en la atención.";

    /// <summary>The authorization question shown when the tenant did not write its own (D-1).</summary>
    public static string DefaultAuthorizationQuestion(string tenantName)
    {
        var name = tenantName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("The default authorization question needs the tenant's name.", nameof(tenantName));
        }

        return $"¿Autorizas a {name} para el tratamiento de los datos personales suministrados en este formulario?";
    }
}
