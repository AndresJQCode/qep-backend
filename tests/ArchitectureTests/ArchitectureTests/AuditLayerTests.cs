using System.Reflection;
using Modules.Audit.Application;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure;

namespace ArchitectureTests;

public sealed class AuditLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(AuditEntry).Assembly,
            typeof(IAuditRecorder).Assembly,
            typeof(AuditInfrastructureExtensions).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructure()
    {
        AssertDoesNotReference(
            typeof(IAuditRecorder).Assembly,
            typeof(AuditInfrastructureExtensions).Assembly);
    }

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
