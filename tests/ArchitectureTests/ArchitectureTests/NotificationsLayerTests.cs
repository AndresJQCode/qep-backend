using System.Reflection;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure;

namespace ArchitectureTests;

// Notifications has three projects (Domain, Application, Infrastructure) and no Api,
// so the Api rule the other modules assert does not apply here.
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
