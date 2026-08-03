namespace Bootstrapper.Authentication;

public static class QepClaimTypes
{
    public const string SubjectId = "sub";

    // Internal QEP user id resolved from the external provider subject on each request.
    public const string QepSubject = "qep_sub";
    public const string TenantId = "tenant_id";
    public const string Permission = "permission";
}
