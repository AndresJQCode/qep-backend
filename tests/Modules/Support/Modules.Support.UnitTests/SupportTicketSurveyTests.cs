using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Fase 4b, Tarea 10 (H15, H18, H21): la encuesta de satisfacción de cada ciclo de resolución, del lado del dominio.</summary>
public sealed class SupportTicketSurveyTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Actor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly TicketRequester Person = new(
        "Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null);

    private static SupportTicket NewTicket() =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 1, SupportTicketTypeDefaults.For(TenantId, Received)[0], "No me llegó la factura",
            "Pedí la factura del pedido 45 y no llegó.", SupportTicketPriority.Medium, ManualIntakeMedium.Phone, SupportReplyChannel.Email,
            null, Person, assigneeMemberId: null, Actor, Received, Clock);

    private static SupportTicket Resolved(out SupportTicketSurvey survey)
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, Received.AddHours(1), Clock);
        survey = Assert.Single(ticket.Surveys);
        return ticket;
    }

    [Fact]
    public void ResolvingRequestsASurveyWithTheResolutionNumber()
    {
        var ticket = Resolved(out var survey);

        Assert.Equal((TenantId, ticket.Id, 1), (survey.TenantId, survey.TicketId, survey.ResolutionNumber));
        Assert.Equal(SupportSurveyStatus.Pending, survey.Status);
        Assert.Equal(Received.AddHours(1), survey.NextAttemptAt);
        Assert.Equal((0, (SupportSurveyChannel?)null, (SupportSurveyRating?)null), (survey.Attempts, survey.Channel, survey.Rating));
        Assert.NotEqual(Guid.Empty, survey.Id);
    }

    [Fact]
    public void ReopeningAndResolvingAgainRequestsTheNext()
    {
        var ticket = Resolved(out _);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, Received.AddHours(2), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, Received.AddHours(3), Clock);

        Assert.Equal([1, 2], ticket.Surveys.Select(survey => survey.ResolutionNumber));
    }

    [Fact]
    public void OtherTransitionsRequestNothing()
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, Received.AddHours(1), Clock);
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, Received.AddHours(2), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, Received.AddHours(3), Clock);

        Assert.Empty(ticket.Surveys);

        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, Received.AddHours(4), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Closed, Actor, Received.AddHours(5), Clock);
        Assert.Single(ticket.Surveys);
    }

    // H21 (owner 2026-11-16, Q-10): mientras no se reabra, también cerrado; nunca Pending ni apagada; la primera respuesta gana.
    [Fact]
    public void AnswerOnlyOnceAndOnlyWhileAnswerable()
    {
        var ticket = Resolved(out var survey);
        var now = Received.AddHours(2);

        var pending = Assert.Throws<SupportDomainException>(
            () => survey.Answer(ticket, SupportSurveyRating.Good, null, SupportSurveyChannel.Portal, null, null, now));
        Assert.Equal(SupportErrorCodes.SurveyClosed, pending.Code);

        survey.MarkSent(SupportSurveyChannel.Portal, null, null, now);
        Assert.True(survey.IsAnswerable(ticket));
        ticket.ChangeStatus(SupportTicketStatus.Closed, Actor, now, Clock);
        Assert.True(survey.IsAnswerable(ticket));
        survey.Answer(ticket, SupportSurveyRating.Bad, "  No me resolvieron.  ", SupportSurveyChannel.Portal, null, null, now);
        Assert.Equal((SupportSurveyRating.Bad, "No me resolvieron.", SupportSurveyChannel.Portal, now),
            (survey.Rating, survey.Comment, survey.AnsweredVia, survey.AnsweredAt));
        var twice = Assert.Throws<SupportDomainException>(
            () => survey.Answer(ticket, SupportSurveyRating.Good, null, SupportSurveyChannel.Portal, null, null, now));
        Assert.Equal(SupportErrorCodes.SurveyAlreadyAnswered, twice.Code);

        // Reabierto: esa resolución ya no se juzga.
        var reopened = Resolved(out var first);
        first.MarkSent(SupportSurveyChannel.Portal, null, null, now);
        reopened.ChangeStatus(SupportTicketStatus.Open, Actor, now, Clock);
        Assert.False(first.IsAnswerable(reopened));

        // La encuesta apagada del tenant no se responde.
        var disabledTicket = Resolved(out var disabled);
        disabled.Skip(SupportTicketSurvey.DisabledReasonCode, null, now);
        Assert.False(disabled.IsAnswerable(disabledTicket));
        var closed = Assert.Throws<SupportDomainException>(
            () => disabled.Answer(disabledTicket, SupportSurveyRating.Good, null, SupportSurveyChannel.Portal, null, null, now));
        Assert.Equal(SupportErrorCodes.SurveyClosed, closed.Code);

        // Sin encuesta por otro motivo (sin plantilla), la página de consulta sí la recibe.
        var skippedTicket = Resolved(out var skipped);
        skipped.Skip("template_not_approved", SupportSurveyChannel.WhatsApp, now);
        Assert.True(skipped.IsAnswerable(skippedTicket));
    }

    [Fact]
    public void TheCommentIsCutTo1000AndOnlyBeforeItsWindow()
    {
        var ticket = Resolved(out var survey);
        var tap = Received.AddHours(2);
        survey.MarkSent(SupportSurveyChannel.WhatsApp, Guid.CreateVersion7(), null, tap);
        Assert.False(survey.AddComment("Antes de calificar.", Guid.CreateVersion7(), tap, tap));

        survey.Answer(ticket, SupportSurveyRating.Regular, null, SupportSurveyChannel.WhatsApp, Guid.CreateVersion7(), tap.AddHours(24), tap);
        var late = Resolved(out var other);
        other.MarkSent(SupportSurveyChannel.WhatsApp, Guid.CreateVersion7(), null, tap);
        other.Answer(late, SupportSurveyRating.Good, null, SupportSurveyChannel.WhatsApp, Guid.CreateVersion7(), tap.AddHours(24), tap);

        var message = Guid.CreateVersion7();
        Assert.True(survey.AddComment(new string('a', 1500), message, tap.AddHours(1), tap.AddHours(1)));
        Assert.Equal(SupportTicketSurvey.CommentMaxLength, survey.Comment!.Length);
        Assert.Equal(message, survey.CommentMessageId);
        Assert.False(survey.AddComment("Otro más.", Guid.CreateVersion7(), tap.AddHours(2), tap.AddHours(2)));
        Assert.False(other.AddComment("Tarde.", Guid.CreateVersion7(), tap.AddHours(24), tap.AddHours(24)));
        Assert.Null(other.Comment);
    }

    // Revisión del bloque D: la ventana se mide con la hora en que el cliente escribió, no con la hora en que el worker lo procesa. Un
    // texto escrito dentro de las 24 horas y procesado tarde sigue siendo el comentario; uno escrito después, nunca.
    [Fact]
    public void TheCommentWindowIsMeasuredByWhenTheCustomerWrote()
    {
        var tap = Received.AddHours(2);
        var processedLate = Resolved(out var onTime);
        onTime.MarkSent(SupportSurveyChannel.WhatsApp, Guid.CreateVersion7(), null, tap);
        onTime.Answer(processedLate, SupportSurveyRating.Good, null, SupportSurveyChannel.WhatsApp, Guid.CreateVersion7(), tap.AddHours(24), tap);
        var writtenLate = Resolved(out var late);
        late.MarkSent(SupportSurveyChannel.WhatsApp, Guid.CreateVersion7(), null, tap);
        late.Answer(writtenLate, SupportSurveyRating.Bad, null, SupportSurveyChannel.WhatsApp, Guid.CreateVersion7(), tap.AddHours(24), tap);

        Assert.True(onTime.AddComment("Escrito a tiempo.", Guid.CreateVersion7(), tap.AddHours(23), tap.AddHours(30)));
        Assert.False(late.AddComment("Escrito tarde.", Guid.CreateVersion7(), tap.AddHours(25), tap.AddHours(25)));
        Assert.Equal(("Escrito a tiempo.", (string?)null), (onTime.Comment, late.Comment));
        Assert.Equal(tap.AddHours(30), onTime.UpdatedAt);
    }

    [Fact]
    public void ThePromptIsRecordedOnce()
    {
        _ = Resolved(out var survey);
        var message = Guid.CreateVersion7();

        survey.RecordPrompt(message, null, Received.AddHours(2));
        survey.RecordPrompt(Guid.CreateVersion7(), "send_failed", Received.AddHours(3));

        Assert.Equal((message, Received.AddHours(2), (string?)null), (survey.PromptMessageId, survey.PromptSentAt, survey.PromptFailureCode));

        _ = Resolved(out var failed);
        failed.RecordPrompt(null, "no_author", Received.AddHours(2));
        failed.RecordPrompt(Guid.CreateVersion7(), null, Received.AddHours(3));
        Assert.Equal(((Guid?)null, (DateTimeOffset?)null, "no_author"), (failed.PromptMessageId, failed.PromptSentAt, failed.PromptFailureCode));
    }

    // H16: tres reintentos (1, 5 y 15 minutos) y el siguiente fallo queda Failed.
    [Fact]
    public void ThirdRetryFailureBecomesFailed()
    {
        _ = Resolved(out var survey);
        var now = Received.AddHours(2);

        survey.Retry("send_failed", now);
        Assert.Equal((SupportSurveyStatus.Pending, 1, now.AddMinutes(1)), (survey.Status, survey.Attempts, survey.NextAttemptAt));
        survey.Retry("connection_unavailable", now);
        Assert.Equal((SupportSurveyStatus.Pending, 2, now.AddMinutes(5)), (survey.Status, survey.Attempts, survey.NextAttemptAt));
        survey.Retry("send_failed", now);
        Assert.Equal((SupportSurveyStatus.Pending, 3, now.AddMinutes(15)), (survey.Status, survey.Attempts, survey.NextAttemptAt));
        survey.Retry("send_failed", now);
        Assert.Equal((SupportSurveyStatus.Failed, 4, "send_failed"), (survey.Status, survey.Attempts, survey.ReasonCode));
    }

    [Theory]
    [InlineData("Buena", SupportSurveyRating.Good)]
    [InlineData("  buena ", SupportSurveyRating.Good)]
    [InlineData("REGULAR", SupportSurveyRating.Regular)]
    [InlineData("Mala", SupportSurveyRating.Bad)]
    [InlineData("málà", SupportSurveyRating.Bad)]
    [InlineData("Bueno", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FromButtonTextIgnoresCaseAccentsAndEdges(string? text, SupportSurveyRating? expected) =>
        Assert.Equal(expected, SupportSurveyRatings.FromButtonText(text));
}
