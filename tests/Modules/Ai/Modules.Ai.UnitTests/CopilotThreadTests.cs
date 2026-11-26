using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-11-17 (copiloto) §5.2: un hilo privado con sus mensajes. Un solo <c>Pending</c> por hilo, la pregunta hasta 2000
/// caracteres, la respuesta hasta 8000 y el título hasta 120; sólo se reintenta la última respuesta fallida.</summary>
public sealed class CopilotThreadTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Member = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreatingAThreadAsksTheFirstQuestionAndLeavesTheAnswerPending()
    {
        var jobId = Guid.CreateVersion7();

        var thread = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, "  ¿Cuánto   vendimos\neste mes?  ", jobId, Now);

        Assert.Equal("¿Cuánto vendimos este mes?", thread.Title);
        Assert.Equal((Tenant, Member, Now, Now, 1L), (thread.TenantId, thread.UserId, thread.CreatedAt, thread.LastActivityAt, thread.Version));
        Assert.Collection(
            thread.Messages,
            question => Assert.Equal(
                (1, CopilotMessageRole.User, "¿Cuánto   vendimos\neste mes?", CopilotMessageStatus.Done, (Guid?)null),
                (question.Seq, question.Role, question.Text, question.Status, question.JobId)),
            answer => Assert.Equal(
                (2, CopilotMessageRole.Assistant, string.Empty, CopilotMessageStatus.Pending, (Guid?)jobId),
                (answer.Seq, answer.Role, answer.Text, answer.Status, answer.JobId)));
        Assert.True(thread.HasPending);
    }

    [Fact]
    public void TheTitleIsTheFirstQuestionCutTo120()
    {
        var thread = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, new string('a', 300), Guid.CreateVersion7(), Now);

        Assert.Equal(CopilotThread.TitleMaxLength, thread.Title.Length);
    }

    [Fact]
    public void OnlyOnePendingAnswerPerThread()
    {
        var thread = NewThread();

        Assert.Throws<InvalidOperationException>(() => thread.AppendUserMessage("¿Y el mes pasado?", Guid.CreateVersion7(), Now.AddMinutes(1)));
        Assert.Single(thread.Messages, message => message.Status == CopilotMessageStatus.Pending);
    }

    [Fact]
    public void AQuestionOver2000IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, new string('a', CopilotThread.QuestionMaxLength + 1), Guid.CreateVersion7(), Now));

        var thread = Answered();
        Assert.Throws<ArgumentException>(() =>
            thread.AppendUserMessage(new string('a', CopilotThread.QuestionMaxLength + 1), Guid.CreateVersion7(), Now));
        Assert.Throws<ArgumentException>(() => thread.AppendUserMessage("   ", Guid.CreateVersion7(), Now));
    }

    [Fact]
    public void ASecondQuestionAfterTheAnswerAppendsInOrderAndTouchesTheActivity()
    {
        var thread = Answered();
        var jobId = Guid.CreateVersion7();
        var later = Now.AddMinutes(5);

        thread.AppendUserMessage("¿Y el mes pasado?", jobId, later);

        Assert.Equal([1, 2, 3, 4], thread.Messages.Select(message => message.Seq));
        Assert.Equal((CopilotMessageStatus.Pending, (Guid?)jobId), (thread.Messages[^1].Status, thread.Messages[^1].JobId));
        Assert.Equal(later, thread.LastActivityAt);
        Assert.Equal(1L, thread.Version);
    }

    [Fact]
    public void CompletingTheAnswerKeepsItsCitationsAndCutsItTo8000()
    {
        var jobId = Guid.CreateVersion7();
        var thread = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, "¿Pedidos abiertos?", jobId, Now);
        var citation = new CopilotCitation("order", Guid.CreateVersion7().ToString(), "Pedido 1024");

        thread.CompleteAssistant(jobId, new string('b', 9000), [citation], Now.AddSeconds(30));

        var answer = thread.Messages[^1];
        Assert.Equal((CopilotMessageStatus.Done, CopilotThread.AnswerMaxLength, (string?)null), (answer.Status, answer.Text.Length, answer.Progress));
        Assert.Equal([citation], answer.Citations);
        Assert.False(thread.HasPending);
        Assert.Equal(Now.AddSeconds(30), thread.LastActivityAt);
    }

    [Fact]
    public void ProgressIsOnlyForThePendingAnswerOfThatJob()
    {
        var jobId = Guid.CreateVersion7();
        var thread = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, "¿Pedidos?", jobId, Now);

        thread.SetProgress(jobId, "get_orders_summary");

        Assert.Equal("get_orders_summary", thread.Messages[^1].Progress);
        Assert.Throws<InvalidOperationException>(() => thread.SetProgress(Guid.CreateVersion7(), "search_orders"));
    }

    [Fact]
    public void RetryOnlyTheLastFailedAnswer()
    {
        var firstJob = Guid.CreateVersion7();
        var thread = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, "¿Pedidos?", firstJob, Now);
        thread.FailAssistant(firstJob, "job_timed_out", Now.AddMinutes(2));
        var failed = thread.Messages[^1];
        Assert.Equal((CopilotMessageStatus.Failed, "job_timed_out"), (failed.Status, failed.ErrorCode));

        Assert.False(thread.CanRetry(thread.Messages[0].Id));
        Assert.False(thread.CanRetry(Guid.CreateVersion7()));
        Assert.True(thread.CanRetry(failed.Id));

        var retryJob = Guid.CreateVersion7();
        thread.RetryLast(failed.Id, retryJob, Now.AddMinutes(3));

        Assert.Equal((CopilotMessageStatus.Pending, (string?)null, (Guid?)retryJob), (failed.Status, failed.ErrorCode, failed.JobId));
        Assert.Equal(Now.AddMinutes(3), thread.LastActivityAt);
        Assert.False(thread.CanRetry(failed.Id));
        Assert.Throws<InvalidOperationException>(() => thread.RetryLast(failed.Id, Guid.CreateVersion7(), Now));

        // Una respuesta fallida que ya no es la última no se reintenta.
        thread.FailAssistant(retryJob, "job_timed_out", Now.AddMinutes(4));
        thread.AppendUserMessage("Otra pregunta", Guid.CreateVersion7(), Now.AddMinutes(5));
        Assert.False(thread.CanRetry(failed.Id));
    }

    [Fact]
    public void RenamingBumpsTheVersionAndChecksTheTitle()
    {
        var thread = NewThread();

        thread.Rename("  Ventas de noviembre  ");

        Assert.Equal(("Ventas de noviembre", 2L), (thread.Title, thread.Version));
        Assert.Throws<ArgumentException>(() => thread.Rename(" "));
        Assert.Throws<ArgumentException>(() => thread.Rename(new string('t', CopilotThread.TitleMaxLength + 1)));
    }

    private static CopilotThread NewThread() =>
        CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, "¿Cuánto vendimos?", Guid.CreateVersion7(), Now);

    private static CopilotThread Answered()
    {
        var jobId = Guid.CreateVersion7();
        var thread = CopilotThread.Create(Guid.CreateVersion7(), Tenant, Member, "¿Cuánto vendimos?", jobId, Now);
        thread.CompleteAssistant(jobId, "Doce pedidos.", [], Now.AddSeconds(10));
        return thread;
    }
}
