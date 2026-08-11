namespace Bootstrapper.Authentication;

public static class QepClaimTypes
{
    public const string SubjectId = "sub";

    // Id interno de usuario QEP, resuelto del subject del proveedor externo en cada request.
    public const string QepSubject = "qep_sub";
    public const string TenantId = "tenant_id";
    public const string Permission = "permission";
}
