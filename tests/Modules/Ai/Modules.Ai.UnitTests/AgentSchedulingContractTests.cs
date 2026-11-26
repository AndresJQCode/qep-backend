using Modules.Ai.Application;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-11-23 §5.7: lo que el contrato común gana con las herramientas de la Agenda.</summary>
public sealed class AgentSchedulingContractTests
{
    [Fact]
    public void WritesCoverTheThreeSchedulingWrites()
    {
        Assert.True(AgentToolNames.Writes(AgentToolNames.CreateQuotationDraft));
        Assert.True(AgentToolNames.Writes(AgentToolNames.BookAppointment));
        Assert.True(AgentToolNames.Writes(AgentToolNames.CancelAppointment));
        Assert.True(AgentToolNames.Writes(AgentToolNames.RescheduleAppointment));
        Assert.False(AgentToolNames.Writes(AgentToolNames.ListBookableServices));
        Assert.False(AgentToolNames.Writes(AgentToolNames.CheckAvailability));
        Assert.False(AgentToolNames.Writes(AgentToolNames.ListMyAppointments));
    }

    [Fact]
    public void ThePrincipalHasTheFourSchedulingCapabilities()
    {
        Assert.Contains(AiServicePrincipal.SchedulingRead, AiServicePrincipal.Capabilities);
        Assert.Contains(AiServicePrincipal.SchedulingReadOwn, AiServicePrincipal.Capabilities);
        Assert.Contains(AiServicePrincipal.SchedulingBookOwn, AiServicePrincipal.Capabilities);
        Assert.Contains(AiServicePrincipal.SchedulingChangeOwn, AiServicePrincipal.Capabilities);
    }

    [Fact]
    public void TheCallerComesFromTheInvocation()
    {
        var (tenant, job, agent, conversation, customer) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var invocation = new AgentToolInvocation(
            tenant, job, "call-1", customer, IsTest: true, AgentId: agent, ConversationId: conversation, BookingOrdinal: 2);

        var caller = AiSchedulingCaller.From(invocation);

        Assert.Equal(new AiSchedulingCaller(tenant, agent, job, conversation, customer, true), caller);
    }

    [Fact]
    public void ResultsCarryEitherAValueOrACode()
    {
        Assert.Equal("x", AiSchedulingResult<string>.Ok("x").Value);
        Assert.Equal("slot_taken", AiSchedulingResult<string>.Error("slot_taken").ErrorCode);
        var handed = AiSchedulingResult<string>.HandedOff("customer_unknown", "appointment_customer_unavailable");
        Assert.Equal(("customer_unknown", "appointment_customer_unavailable"), (handed.ErrorCode, handed.HandoffCode));
    }
}
