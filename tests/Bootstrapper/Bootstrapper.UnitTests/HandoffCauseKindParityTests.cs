using Modules.Automations.Application.Engine;
using Modules.Messaging.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-24 §5.3: Automations no referencia Messaging, así que la causa de un traspaso tiene dos enums (<see cref="HandoffCauseKind"/>
/// del motor y <see cref="AutomationHandoffKind"/> del hilo) y el adaptador traduce por nombre. Este proyecto ve los dos módulos: si uno
/// cambia sin el otro, esta prueba se rompe antes que el <c>Enum.Parse</c> del adaptador en producción.
/// </summary>
public sealed class HandoffCauseKindParityTests
{
    [Fact]
    public void EveryCauseKindExistsInMessaging() =>
        Assert.Equal(
            Enum.GetNames<HandoffCauseKind>().Order(StringComparer.Ordinal),
            Enum.GetNames<AutomationHandoffKind>().Order(StringComparer.Ordinal));
}
