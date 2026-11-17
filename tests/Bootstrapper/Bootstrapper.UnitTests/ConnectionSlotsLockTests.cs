using System.Reflection;
using Bootstrapper.Startup;
using BuildingBlocks.Application;
using Modules.Integrations.Infrastructure;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Plan 2026-11-12 (correcciones), Tarea 6: la primera clave del candado de dos claves de <see cref="ConnectionSlotsLock"/> no la usa ningún
/// otro candado de dos claves. Si la comparte, la sincronización de un tenant hace fila con algo que no tiene nada que ver (el arranque de
/// un pod, el refresco de un token) cuando los hashes coinciden.
/// </summary>
public sealed class ConnectionSlotsLockTests
{
    [Fact]
    public void ItsNamespaceIsNotUsedByAnyOtherTwoKeyLock()
    {
        var integrations = typeof(IntegrationsInfrastructureExtensions).Assembly;
        int[] others =
        [
            TenantChangeLock.Namespace,
            ConversationBotLock.Namespace,
            StartupLock.Namespace,
            InternalNamespace(integrations, "Modules.Integrations.Infrastructure.Persistence.ShopifyTokenLock"),
            InternalNamespace(integrations, "Modules.Integrations.Infrastructure.Persistence.InstagramTokenLock"),
            CopilotAskNamespace,
        ];

        Assert.DoesNotContain(ConnectionSlotsLock.Namespace, others);
    }

    /// <summary>Revisión de la rama, hallazgo 4: el candado de preguntas del copiloto tiene su propio espacio de dos claves.</summary>
    [Fact]
    public void TheCopilotAskingNamespaceIsNotUsedByAnyOtherTwoKeyLock()
    {
        var integrations = typeof(IntegrationsInfrastructureExtensions).Assembly;
        int[] others =
        [
            TenantChangeLock.Namespace,
            ConversationBotLock.Namespace,
            StartupLock.Namespace,
            ConnectionSlotsLock.Namespace,
            InternalNamespace(integrations, "Modules.Integrations.Infrastructure.Persistence.ShopifyTokenLock"),
            InternalNamespace(integrations, "Modules.Integrations.Infrastructure.Persistence.InstagramTokenLock"),
        ];

        Assert.DoesNotContain(CopilotAskNamespace, others);
    }

    // CopilotAskLock es internal de Ai: se lee por nombre, como los de Integrations.
    private static int CopilotAskNamespace =>
        InternalNamespace(typeof(Modules.Ai.Infrastructure.AiInfrastructureExtensions).Assembly, "Modules.Ai.Infrastructure.Persistence.CopilotAskLock");

    // ShopifyTokenLock e InstagramTokenLock son internal de Integrations: se leen por nombre.
    private static int InternalNamespace(Assembly assembly, string typeName) =>
        (int)assembly.GetType(typeName, throwOnError: true)!
            .GetField("Namespace", BindingFlags.Public | BindingFlags.Static)!
            .GetRawConstantValue()!;
}
