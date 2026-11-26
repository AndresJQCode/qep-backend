using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary>Lo que repiten los casos de uso de Payroll.</summary>
internal static class PayrollSupport
{
    /// <summary>Una escritura se aplica sobre lo que la pantalla cargó: otra versión es 412.</summary>
    public static void EnsureVersion(long current, long expected)
    {
        if (current != expected)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The payroll record changed after it was loaded.");
        }
    }

    public static void Diff<T>(List<string> fields, string name, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            fields.Add(name);
        }
    }

    public static void PublishAudit(
        IPayrollAuditPublisher audit,
        IExecutionContext executionContext,
        Guid tenantId,
        string action,
        string resource,
        string resourceId,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset now) =>
        audit.Publish(tenantId, executionContext.SubjectId, action, resource, resourceId, "success", changedFields, now);
}
