using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-11-17 importar/exportar §3: el archivo de un agente no lleva lo que es del tenant (el miembro del traspaso, el asesor y
/// la cuenta de los borradores de cotización); el resto viaja igual.</summary>
public sealed class AgentExportTests
{
    [Fact]
    public void PortableDropsTheMemberAndTheQuotationDraftButKeepsTheRest()
    {
        var model = new AgentModelRef("anthropic", "claude-sonnet-5-5");
        var config = AgentConfig.Default(model) with
        {
            Tone = "friendly",
            Handoff = new AgentHandoff(AgentHandoff.ToMember, Guid.NewGuid(), "Te paso con un asesor.", 10, 3),
            Tools = new AgentTools(Catalog: true, OrderStatus: false, QuotationDraft: true),
            QuotationDraft = new AgentQuotationDraft(Guid.NewGuid(), new AgentBillingAccount(Guid.NewGuid(), "Banco", "123", "COP")),
        };

        var portable = AgentExport.Portable(config);

        Assert.Null(portable.Handoff.MemberId);
        Assert.Equal(AgentHandoff.ToMember, portable.Handoff.Destination);
        Assert.Equal("Te paso con un asesor.", portable.Handoff.Message);
        Assert.Null(portable.QuotationDraft);
        Assert.Equal("friendly", portable.Tone);
        Assert.True(portable.Tools.QuotationDraft);
        Assert.Equal(model, portable.Model);
    }

    // Spec 2026-11-23 D-1 y §9.1: un documento guardado antes de la herramienta de la Agenda se sigue leyendo, con el interruptor apagado.
    [Fact]
    public void AConfigWithoutTheSchedulingKeyReadsFalse()
    {
        var json = AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5")).ToJson()
            .Replace(",\"scheduling\":false", string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain("scheduling", json, StringComparison.Ordinal);
        Assert.False(AgentConfig.FromJson(json).Tools.Scheduling);
    }

    [Fact]
    public void SchedulingTravelsInTheExport()
    {
        var config = AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5")) with
        {
            Tools = new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: false, Scheduling: true),
        };

        var portable = AgentExport.Portable(config);

        Assert.True(portable.Tools.Scheduling);
        Assert.True(AgentConfig.FromJson(portable.ToJson()).Tools.Scheduling);
    }
}
