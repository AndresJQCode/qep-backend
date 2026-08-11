using System.Reflection;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure;

namespace ArchitectureTests;

// Notifications tiene tres proyectos (Domain, Application, Infrastructure) y ninguno Api,
// así que la regla de Api que afirman los otros módulos no aplica acá.
public sealed class NotificationsLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(Notification).Assembly,
            typeof(IEmailChannel).Assembly,
            typeof(NotificationsInfrastructureExtensions).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructure()
    {
        AssertDoesNotReference(
            typeof(IEmailChannel).Assembly,
            typeof(NotificationsInfrastructureExtensions).Assembly);
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
