using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-11-17 (copiloto) D-C8, D-C9 y D-C13, plan fase 2 C3.4: el barrido diario borra los hilos sin actividad en la retención
/// del tenant (con su auditoría, actor sistema) y los <c>tool_calls</c> de trabajos del copiloto con más de 30 días.</summary>
public sealed class CopilotRetentionSweeperTests
{
    private static readonly Guid Tenant = CopilotSettingsHandlersTests.Tenant;
    private static readonly DateTimeOffset Now = CopilotSettingsHandlersTests.Now;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnInactiveThreadIsDeletedAfterRetentionAndAudited()
    {
        var bed = new CopilotBed();
        var expired = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Guid.NewGuid(), "¿Cuánto vendimos?", Guid.CreateVersion7(), Now.AddDays(-91));
        bed.Repository.Threads.Add(expired);
        bed.Repository.Expired.Add(new CopilotExpiredThread(Tenant, expired.Id));

        var result = await Sweeper(bed).SweepAsync(Ct);

        Assert.Equal(1, result.Threads);
        Assert.Equal([expired.Id], bed.Repository.Deleted);
        Assert.Equal(Now, bed.Repository.ExpiredAsOf);
        var entry = Assert.Single(bed.Audit.Entries);
        // D-C13: el actor de la retención es el sistema, no un miembro.
        Assert.Equal(
            (Tenant, (Guid?)null, AiAuditActions.CopilotThreadDeleted, AiAuditActions.ResourceCopilotThread, expired.Id),
            (entry.Tenant, entry.Actor, entry.Action, entry.ResourceType, entry.ResourceId));
        // La entrada se guarda en la misma transacción del borrado: el repositorio la ve pendiente cuando borra.
        Assert.Equal([1], bed.Repository.AuditPendingAtDelete);
    }

    [Fact]
    public async Task CopilotToolCallsExpireAfter30Days()
    {
        var bed = new CopilotBed();
        bed.Repository.ExpiredToolCalls = 7;

        var result = await Sweeper(bed).SweepAsync(Ct);

        Assert.Equal(7, result.ToolCalls);
        Assert.Equal(Now.AddDays(-30), bed.Repository.ToolCallsBefore);
        Assert.Empty(bed.Repository.Deleted);
        Assert.Empty(bed.Audit.Entries);
    }

    // Revisión de la rama, hallazgo 8: cada pod barre. Un hilo que otro pod ya borró (o que tiene tomado) no se borra acá, y entonces no se
    // audita: la entrada va sólo con un borrado que ocurrió, y en su misma transacción.
    [Fact]
    public async Task AThreadAnotherPodAlreadyDeletedIsNeitherCountedNorAudited()
    {
        var bed = new CopilotBed();
        bed.Repository.Expired.Add(new CopilotExpiredThread(Tenant, Guid.CreateVersion7()));

        var result = await Sweeper(bed).SweepAsync(Ct);

        Assert.Equal(0, result.Threads);
        Assert.Empty(bed.Audit.Entries);
        Assert.Equal([true], bed.Repository.SkipLockedAtDelete);
    }

    // Revisión de la rama, hallazgo 4: el registro de preguntas del tope por miembro se guarda dos días, el doble del tope más largo.
    [Fact]
    public async Task TheQuestionLogKeepsTwoDays()
    {
        var bed = new CopilotBed();
        bed.Repository.QuestionTimes.AddRange([Now.AddDays(-3), Now.AddHours(-30)]);

        await Sweeper(bed).SweepAsync(Ct);

        Assert.Equal(Now.AddDays(-2), bed.Repository.QuestionsBefore);
        Assert.Equal([Now.AddHours(-30)], bed.Repository.QuestionTimes);
    }

    [Fact]
    public async Task ASweepWalksEveryBatch()
    {
        var bed = new CopilotBed();
        for (var index = 0; index < CopilotRetentionSweeper.BatchSize + 5; index++)
        {
            var thread = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Guid.NewGuid(), "Hola", Guid.CreateVersion7(), Now.AddDays(-200));
            bed.Repository.Threads.Add(thread);
            bed.Repository.Expired.Add(new CopilotExpiredThread(Tenant, thread.Id));
        }

        var result = await Sweeper(bed).SweepAsync(Ct);

        Assert.Equal(CopilotRetentionSweeper.BatchSize + 5, result.Threads);
        Assert.Empty(bed.Repository.Threads);
    }

    // D-C13: la auditoría es de metadatos. El puerto no tiene por dónde recibir el texto de una pregunta o una respuesta.
    [Fact]
    public void TheAuditNeverCarriesQuestionOrAnswerText()
    {
        var record = typeof(IAiAuditRecorder).GetMethod(nameof(IAiAuditRecorder.RecordCopilot))!;

        Assert.Equal(
            ["action", "resourceType"],
            record.GetParameters().Where(parameter => parameter.ParameterType == typeof(string)).Select(parameter => parameter.Name));
    }

    private static CopilotRetentionSweeper Sweeper(CopilotBed bed) =>
        new(bed.Repository, bed.Audit, new CopilotRetention(TimeSpan.FromDays(30)), bed.Clock);
}
