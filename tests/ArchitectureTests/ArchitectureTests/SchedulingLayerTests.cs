using System.Reflection;
using Modules.Scheduling.Api;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using Modules.Scheduling.Infrastructure;

namespace ArchitectureTests;

public sealed class SchedulingLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(SchedulingDomainException).Assembly,
            typeof(SchedulingPermissions).Assembly,
            typeof(SchedulingInfrastructureExtensions).Assembly,
            typeof(SchedulingEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(SchedulingPermissions).Assembly,
            typeof(SchedulingInfrastructureExtensions).Assembly,
            typeof(SchedulingEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(SchedulingInfrastructureExtensions).Assembly,
            typeof(SchedulingEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. la restricción de exclusión de las sesiones) es tarea de Infrastructure, no de Application.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(SchedulingPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal) ||
             name.StartsWith("StackExchange.Redis", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `scheduling` no se acopla a ningún otro módulo de negocio. La única dependencia legítima hacia afuera es
    /// `Modules.Tenancy.Application` (<c>IExecutionContext</c>, <c>ITenantModules</c>). Clientes, miembros, festivos y el envío de
    /// WhatsApp entran por los puertos <c>IScheduling*</c> declarados acá, cuyos adaptadores viven en `Bootstrapper`.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(SchedulingPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Scheduling", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));
    }

    // Los clientes entran sólo por ISchedulingCustomerLookup, y Mensajería sólo por IAppointmentReminderSender.
    [Fact]
    public void NoSchedulingLayerReferencesCustomers() => AssertNoSchedulingLayerReferencesPrefix("Modules.Customers");

    [Fact]
    public void NoSchedulingLayerReferencesMessaging() => AssertNoSchedulingLayerReferencesPrefix("Modules.Messaging");

    private static void AssertNoSchedulingLayerReferencesPrefix(string prefix)
    {
        Assembly[] layers =
        [
            typeof(SchedulingDomainException).Assembly,
            typeof(SchedulingPermissions).Assembly,
            typeof(SchedulingInfrastructureExtensions).Assembly,
            typeof(SchedulingEndpoints).Assembly,
        ];

        foreach (var layer in layers)
        {
            Assert.DoesNotContain(ReferenceNamesOf(layer), name =>
                name is not null && name.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    private static string?[] ReferenceNamesOf(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    private static void AssertDoesNotReference(
        Assembly source,
        params Assembly[] forbiddenAssemblies)
    {
        var references = source
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in forbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden.GetName().Name, references);
        }
    }
}
