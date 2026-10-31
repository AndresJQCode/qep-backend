namespace Bootstrapper.UnitTests;

/// <summary>
/// Cobros recurrentes, fase 1 (owner, 2026-10-29): el aviso de cobro sale sin importar el consentimiento de Ley 1581.
/// El adaptador es el único punto que cambiaría si eso se revierte, así que esta prueba fija la decisión.
/// </summary>
public sealed class ServiceContractsConsentGateTests
{
    [Fact]
    public async Task Phase1AlwaysAllows()
    {
        var gate = new ServiceContractsConsentGate();

        var allowed = await gate.MayNotifyAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        Assert.True(allowed);
    }
}
