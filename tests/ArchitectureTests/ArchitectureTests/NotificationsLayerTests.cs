using System.Reflection;
using Modules.Notifications.Api;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure;

namespace ArchitectureTests;

// Notifications tenía tres proyectos (Domain, Application, Infrastructure); la campana en la app (spec 2026-11-22) le suma
// Modules.Notifications.Api, así que valen también las reglas de Api de los otros módulos.
public sealed class NotificationsLayerTests
{
    [Fact]
    public void ApiDoesNotReferenceInfrastructure()
    {
        AssertDoesNotReference(
            typeof(NotificationEndpoints).Assembly,
            typeof(NotificationsInfrastructureExtensions).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(IEmailChannel).Assembly,
            typeof(NotificationEndpoints).Assembly);
    }

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
