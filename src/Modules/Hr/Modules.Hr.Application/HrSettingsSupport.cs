using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <summary>Lo que repiten los casos de uso de la configuración.</summary>
internal static class HrSettingsSupport
{
    /// <summary>Un <c>PUT</c> se aplica sobre lo que la pantalla cargó: otra versión es 412.</summary>
    public static void EnsureVersion(long current, long expected)
    {
        if (current != expected)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The setting changed after it was loaded.");
        }
    }

    /// <summary>La ciudad es opcional, pero si viene tiene que existir en Geografía.</summary>
    public static async Task<Guid?> EnsureCityAsync(
        IHrGeographyLookup geography, Guid? cityId, CancellationToken cancellationToken)
    {
        if (cityId is not { } id)
        {
            return null;
        }

        if (id == Guid.Empty || !(await geography.FindCitiesAsync([id], cancellationToken)).ContainsKey(id))
        {
            HrFieldErrors.Throw("cityId", "La ciudad no existe.");
        }

        return id;
    }

    public static void Diff<T>(List<string> fields, string name, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            fields.Add(name);
        }
    }

    public static void PublishAudit(
        IHrAuditPublisher audit,
        IExecutionContext executionContext,
        Guid tenantId,
        string action,
        string resource,
        Guid id,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset now) =>
        audit.Publish(tenantId, executionContext.SubjectId, action, resource, id.ToString(), "success", changedFields, now);
}
