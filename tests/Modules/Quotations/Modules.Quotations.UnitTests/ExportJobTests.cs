using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El estado de una exportación encolada. La toma real la hace un UPDATE en Postgres
/// (ExportJobQueue); <see cref="ExportJob.Claim"/> es el mismo cambio escrito en C#, y estas
/// pruebas fijan lo que ese UPDATE tiene que dejar.
/// </summary>
public sealed class ExportJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void EnqueueStartsPendingAndDueRightAway()
    {
        var job = NewJob();

        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(Now, job.NextAttemptAt);
        Assert.Equal(Now, job.RequestedAt);
        Assert.Null(job.LockedUntil);
        Assert.True(job.IsClaimable(Now));
    }

    [Fact]
    public void ClaimTakesTheJobWithATenMinuteLeaseAndConsumesAnAttempt()
    {
        var job = NewJob();

        job.Claim(Now);

        Assert.Equal(ExportJobStatus.Processing, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(Now.AddMinutes(10), job.LockedUntil);
        Assert.False(job.IsClaimable(Now.AddMinutes(9)));
    }

    // Un Pending con la espera del backoff por delante no se toma: el reintento es a su hora.
    [Fact]
    public void AJobWaitingItsBackoffIsNotClaimable()
    {
        var job = NewJob();
        job.Claim(Now);
        job.RecordTransientFailure("IOException: timeout", Now);

        Assert.False(job.IsClaimable(Now.AddSeconds(59)));
        Assert.Throws<InvalidOperationException>(() => job.Claim(Now.AddSeconds(59)));
        Assert.True(job.IsClaimable(Now.AddMinutes(1)));
    }

    // Worker muerto a mitad (D11): el lease vence y otro lo retoma, consumiendo otro intento.
    [Fact]
    public void AnExpiredLeaseCanBeClaimedAgain()
    {
        var job = NewJob();
        job.Claim(Now);

        job.Claim(Now.AddMinutes(11));

        Assert.Equal(2, job.Attempts);
        Assert.Equal(Now.AddMinutes(21), job.LockedUntil);
    }

    [Fact]
    public void CompleteRecordsTheFileAndReleasesTheLease()
    {
        var job = NewJob();
        job.Claim(Now);

        job.Complete("cotizaciones-2026-09-12-1530.xlsx", 42, Now.AddMinutes(1));

        Assert.Equal(ExportJobStatus.Completed, job.Status);
        Assert.Equal("cotizaciones-2026-09-12-1530.xlsx", job.FileName);
        Assert.Equal(42, job.RowCount);
        Assert.Equal(Now.AddMinutes(1), job.CompletedAt);
        Assert.Null(job.LockedUntil);
    }

    [Fact]
    public void CompleteRequiresAClaimedJob()
    {
        var job = NewJob();

        Assert.Throws<InvalidOperationException>(() => job.Complete("x.xlsx", 1, Now));
    }

    // D11: 1, 5 y 15 minutos entre intentos; el cuarto fallido termina el job.
    [Fact]
    public void TransientFailuresBackOffOneFiveAndFifteenMinutesAndTheFourthFailsTheJob()
    {
        var job = NewJob();

        job.Claim(Now);
        Assert.False(job.RecordTransientFailure("IOException: r2 down", Now));
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(Now.AddMinutes(1), job.NextAttemptAt);
        Assert.Null(job.LockedUntil);
        Assert.Equal("IOException: r2 down", job.LastError);

        var second = Now.AddMinutes(1);
        job.Claim(second);
        Assert.False(job.RecordTransientFailure("IOException: r2 down", second));
        Assert.Equal(second.AddMinutes(5), job.NextAttemptAt);

        var third = second.AddMinutes(5);
        job.Claim(third);
        Assert.False(job.RecordTransientFailure("IOException: r2 down", third));
        Assert.Equal(third.AddMinutes(15), job.NextAttemptAt);

        var fourth = third.AddMinutes(15);
        job.Claim(fourth);
        Assert.True(job.RecordTransientFailure("IOException: r2 down", fourth));
        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.Equal(fourth, job.CompletedAt);
        Assert.Equal(4, job.Attempts);
    }

    // Una espera por reintento: con una espera de más, la última nunca se usaría —el defecto de la
    // primera versión, tres esperas para tres intentos—.
    [Fact]
    public void EveryRetryDelayIsUsedBeforeTheLastAttempt()
    {
        Assert.Equal(4, ExportJob.MaxAttempts);
        Assert.Equal(ExportJob.MaxAttempts - 1, ExportJob.RetryDelays.Count);
    }

    [Fact]
    public void FailIsDefinitiveEvenOnTheFirstAttempt()
    {
        var job = NewJob();
        job.Claim(Now);

        job.Fail("ExportJobDefinitiveException: no rows", Now);

        Assert.Equal(ExportJobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.False(job.IsClaimable(Now.AddDays(1)));
    }

    // Soporte lee last_error; un stack trace entero no le sirve a nadie y la columna no crece
    // sin techo.
    [Fact]
    public void TheLastErrorIsTruncated()
    {
        var job = NewJob();
        job.Claim(Now);

        job.Fail(new string('x', ExportJob.LastErrorMaxLength + 50), Now);

        Assert.Equal(ExportJob.LastErrorMaxLength, job.LastError!.Length);
    }

    // Si el worker murió en el último intento, el lease vence y la toma suma un quinto: el job
    // ya no tiene intentos y el runner lo cierra sin procesarlo.
    [Fact]
    public void AClaimAfterTheLastAttemptIsDetected()
    {
        var job = NewJob();
        job.Claim(Now);
        job.Claim(Now.AddMinutes(11));
        job.Claim(Now.AddMinutes(22));
        job.Claim(Now.AddMinutes(33));
        Assert.False(job.HasExceededAttempts);

        job.Claim(Now.AddMinutes(44));

        Assert.True(job.HasExceededAttempts);
    }

    private static ExportJob NewJob() =>
        ExportJob.Enqueue(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            ExportJobKind.Quotations,
            "{}",
            Now);
}
