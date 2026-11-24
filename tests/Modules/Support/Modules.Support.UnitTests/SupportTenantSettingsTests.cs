using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Pre-flight R3: el umbral de «por vencer» del tenant [owner 2026-11-14], 0 a 30 días hábiles, 3 por defecto.
public sealed class SupportTenantSettingsTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheRangeIsZeroToThirtyAndTheDefaultIsThree()
    {
        Assert.Equal(3, SupportTenantSettings.DefaultDueSoonBusinessDays);
        Assert.Equal(0, SupportTenantSettings.MinDueSoonBusinessDays);
        Assert.Equal(30, SupportTenantSettings.MaxDueSoonBusinessDays);
    }

    [Fact]
    public void ANewRowHasTheDefaultAndVersionOne()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Assert.Equal(TenantId, settings.TenantId);
        Assert.Equal(3, settings.DueSoonBusinessDays);
        Assert.Equal(1L, settings.Version);
        Assert.Equal(Now, settings.UpdatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(30)]
    public void ChangingTheThresholdBumpsTheVersion(int businessDays)
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        settings.ChangeDueSoon(businessDays, Now.AddHours(1));

        Assert.Equal(businessDays, settings.DueSoonBusinessDays);
        Assert.Equal(2L, settings.Version);
        Assert.Equal(Now.AddHours(1), settings.UpdatedAt);
    }

    [Fact]
    public void TheSameThresholdIsANoOp()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        settings.ChangeDueSoon(3, Now.AddHours(1));

        Assert.Equal(1L, settings.Version);
        Assert.Equal(Now, settings.UpdatedAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void AThresholdOutOfRangeIsRejected(int businessDays)
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ChangeDueSoon(businessDays, Now.AddHours(1)));
        Assert.Equal(3, settings.DueSoonBusinessDays);
        Assert.Equal(1L, settings.Version);
    }

    [Fact]
    public void TheNextStepIsTrimmedAndAnEmptyOneIsNull()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        Assert.Null(settings.FormalNextStep);

        settings.ChangeFormalNextStep("  Puedes acudir a la Superintendencia.\n\tLínea 2  ", Now.AddHours(1));
        Assert.Equal("Puedes acudir a la Superintendencia.\n\tLínea 2", settings.FormalNextStep);
        Assert.Equal(2L, settings.Version);
        Assert.Equal(Now.AddHours(1), settings.UpdatedAt);

        settings.ChangeFormalNextStep("   ", Now.AddHours(2));
        Assert.Null(settings.FormalNextStep);
        Assert.Equal(3L, settings.Version);
    }

    [Fact]
    public void SettingTheSameNextStepDoesNotBumpTheVersion()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        settings.ChangeFormalNextStep("Texto", Now.AddHours(1));

        settings.ChangeFormalNextStep("  Texto ", Now.AddHours(2));
        settings.ChangeFormalNextStep("Texto", Now.AddHours(3));

        Assert.Equal(2L, settings.Version);
        Assert.Equal(Now.AddHours(1), settings.UpdatedAt);

        var empty = SupportTenantSettings.CreateDefault(TenantId, Now);
        empty.ChangeFormalNextStep(null, Now.AddHours(1));
        empty.ChangeFormalNextStep("", Now.AddHours(1));
        Assert.Equal(1L, empty.Version);
    }

    [Fact]
    public void ANextStepLongerThan2000Throws()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Assert.Equal(2000, SupportTenantSettings.FormalNextStepMaxLength);
        settings.ChangeFormalNextStep($"  {new string('a', 2000)}  ", Now.AddHours(1));
        Assert.Throws<ArgumentException>(() => settings.ChangeFormalNextStep(new string('b', 2001), Now.AddHours(2)));
        Assert.Equal(new string('a', 2000), settings.FormalNextStep);
        Assert.Equal(2L, settings.Version);
    }

    /// <summary>La regla del validador repetida en el dominio: un camino que se salte el validador no puede guardar un carácter de
    /// control (un <c>\0</c> llegaría a Postgres como 22021).</summary>
    [Fact]
    public void ANextStepWithAControlCharacterThrows()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        settings.ChangeFormalNextStep("Paso uno.\r\n\tPaso dos.", Now.AddHours(1));
        Assert.Throws<ArgumentException>(() => settings.ChangeFormalNextStep("Con un \0 nulo", Now.AddHours(2)));
        Assert.Throws<ArgumentException>(() => settings.ChangeFormalNextStep("Con un \u0007 de control", Now.AddHours(2)));
        Assert.Throws<ArgumentException>(() => SupportFormalNextStep.Normalize("Con un \u001b escape"));
        Assert.Equal("Paso uno.\r\n\tPaso dos.", settings.FormalNextStep);
    }

    // Fase 4b (H14, owner 2026-11-16, Q-19 y Q-20): la encuesta nace apagada; el cierre automático va de 1 a 30 días hábiles, 5 por
    // defecto, y nunca se apaga.
    [Fact]
    public void AutoCloseIsBetween1And30AndTheSurveyIsOffByDefault()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Assert.False(settings.SurveyEnabled);
        Assert.Equal(5, settings.AutoCloseBusinessDays);
        Assert.Equal((1, 30, 5), (SupportTenantSettings.MinAutoCloseBusinessDays, SupportTenantSettings.MaxAutoCloseBusinessDays,
            SupportTenantSettings.DefaultAutoCloseBusinessDays));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ChangeSurvey(true, 0, Now.AddHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ChangeSurvey(true, 31, Now.AddHours(1)));

        settings.ChangeSurvey(false, 5, Now.AddHours(1));
        Assert.Equal(1L, settings.Version);
        settings.ChangeSurvey(true, 30, Now.AddHours(2));
        Assert.Equal(
            (true, 30, 2L, Now.AddHours(2)), (settings.SurveyEnabled, settings.AutoCloseBusinessDays, settings.Version, settings.UpdatedAt));
    }

    // Owner 2026-11-16: one PUT of /support/settings is one version, however many groups it changes. The If-Match the screen
    // holds is "the version I loaded", not a count of the fields that moved.
    [Fact]
    public void ChangingEveryGroupAtOnceBumpsTheVersionOnce()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        var changes = settings.Update(
            6, "  Puedes acudir a la SIC.  ", surveyEnabled: true, autoCloseBusinessDays: 9, settings.UnassignedAlert, null, Now.AddHours(1));

        Assert.Equal(new SupportTenantSettingsChanges(true, true, true, true, false, false), changes);
        Assert.Equal(
            (6, "Puedes acudir a la SIC.", true, 9, 2L, Now.AddHours(1)),
            (settings.DueSoonBusinessDays, settings.FormalNextStep, settings.SurveyEnabled, settings.AutoCloseBusinessDays,
                settings.Version, settings.UpdatedAt));
    }

    [Fact]
    public void AnUpdateWithTheSameValuesIsANoOp()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        settings.Update(6, "Texto", surveyEnabled: true, autoCloseBusinessDays: 9, settings.UnassignedAlert, null, Now.AddHours(1));

        var changes = settings.Update(6, " Texto ", surveyEnabled: true, autoCloseBusinessDays: 9, settings.UnassignedAlert, null, Now.AddHours(2));

        Assert.False(changes.Any);
        Assert.Equal((2L, Now.AddHours(1)), (settings.Version, settings.UpdatedAt));
    }

    // Everything is checked before anything changes: a rejected auto-close leaves the threshold as it was.
    [Fact]
    public void ARejectedUpdateChangesNothing()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => settings.Update(6, "Texto", surveyEnabled: true, autoCloseBusinessDays: 31, settings.UnassignedAlert, null, Now.AddHours(1)));

        Assert.Equal(
            (3, (string?)null, false, 5, 1L),
            (settings.DueSoonBusinessDays, settings.FormalNextStep, settings.SurveyEnabled, settings.AutoCloseBusinessDays,
                settings.Version));
    }

    private static readonly Guid Ana = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Bruno = Guid.Parse("0193a000-0000-7000-8000-0000000000a2");

    private static SupportUnassignedAlertSettings Alert(
        bool enabled = true, int hours = 4, Guid? schedule = null, params Guid[] recipients) =>
        new(enabled, hours, schedule, recipients.Length == 0 && enabled ? [Ana] : recipients);

    private static SupportTenantSettings Apply(SupportTenantSettings settings, SupportUnassignedAlertSettings alert, DateTimeOffset at)
    {
        settings.Update(
            settings.DueSoonBusinessDays, settings.FormalNextStep, settings.SurveyEnabled, settings.AutoCloseBusinessDays, alert, settings.TaskNoticeConnectionId, at);
        return settings;
    }

    // Spec 2026-11-22 §7: off by default, four hours, the default schedule, nobody chosen.
    [Fact]
    public void ANewRowHasTheUnassignedAlertOff()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Assert.False(settings.UnassignedAlert.Enabled);
        Assert.Equal(4, settings.UnassignedAlert.ThresholdHours);
        Assert.Null(settings.UnassignedAlert.ScheduleId);
        Assert.Empty(settings.UnassignedAlert.RecipientIds);
        Assert.Null(settings.UnassignedAlertEnabledAt);
        Assert.Equal((1, 72, 20), (
            SupportUnassignedAlertSettings.MinThresholdHours,
            SupportUnassignedAlertSettings.MaxThresholdHours,
            SupportUnassignedAlertSettings.MaxRecipients));
    }

    [Fact]
    public void UpdateBumpsTheVersionOnceWithTheAlert()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        var schedule = Guid.CreateVersion7();

        var changes = settings.Update(
            6, "Texto", surveyEnabled: true, autoCloseBusinessDays: 9, Alert(true, 8, schedule, Ana, Bruno), null, Now.AddHours(1));

        Assert.True(changes.UnassignedAlert);
        Assert.Equal(2L, settings.Version);
        Assert.Equal(
            (true, 8, (Guid?)schedule), (settings.UnassignedAlertEnabled, settings.UnassignedAlertHours, settings.UnassignedAlertScheduleId));
        Assert.Equal([Ana, Bruno], settings.UnassignedAlertRecipientIds);
    }

    [Fact]
    public void ARepeatedAlertChangesNothingAndTheOrderOfRecipientsCounts()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        Apply(settings, Alert(true, 4, null, Ana, Bruno), Now.AddHours(1));

        var same = settings.Update(3, null, false, 5, Alert(true, 4, null, Ana, Bruno), null, Now.AddHours(2));
        var reordered = settings.Update(3, null, false, 5, Alert(true, 4, null, Bruno, Ana), null, Now.AddHours(3));

        Assert.False(same.Any);
        Assert.True(reordered.UnassignedAlert);
        Assert.Equal(3L, settings.Version);
    }

    [Fact]
    public void EnablingSetsEnabledAtAndReEnablingMovesIt()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Apply(settings, Alert(true, 4, null, Ana), Now.AddHours(1));
        Assert.Equal(Now.AddHours(1), settings.UnassignedAlertEnabledAt);

        // Another threshold while it stays on does not move the floor of the clock.
        Apply(settings, Alert(true, 6, null, Ana), Now.AddHours(2));
        Assert.Equal(Now.AddHours(1), settings.UnassignedAlertEnabledAt);

        // Off keeps it; on again moves it, so a ticket that waited while it was off does not alert at once.
        Apply(settings, Alert(false, 6, null, Ana), Now.AddHours(3));
        Assert.Equal(Now.AddHours(1), settings.UnassignedAlertEnabledAt);
        Apply(settings, Alert(true, 6, null, Ana), Now.AddHours(4));
        Assert.Equal(Now.AddHours(4), settings.UnassignedAlertEnabledAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(73)]
    [InlineData(-1)]
    public void ThresholdOutOfRangeIsRejected(int hours)
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => Apply(settings, Alert(true, hours, null, Ana), Now.AddHours(1)));

        Assert.Equal(1L, settings.Version);
        Assert.False(settings.UnassignedAlertEnabled);
    }

    [Fact]
    public void TheRecipientsMustBeFewDistinctAndNotEmptyWhenEnabled()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        var twentyOne = Enumerable.Range(0, 21).Select(_ => Guid.CreateVersion7()).ToArray();

        Assert.Throws<ArgumentException>(() => Apply(settings, Alert(true, 4, null, twentyOne), Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => Apply(settings, Alert(true, 4, null, Ana, Ana), Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => Apply(settings, new SupportUnassignedAlertSettings(true, 4, null, []), Now.AddHours(1)));
        // Off with nobody is the stored default and is fine.
        Apply(settings, new SupportUnassignedAlertSettings(false, 4, null, []), Now.AddHours(1));
        Assert.Equal(1L, settings.Version);
    }

    // Fase 4b, revisión del bloque C (2026-11-16): el frontend publicado manda sólo el umbral. La encuesta y el cierre automático son
    // opcionales en el PUT: ausentes conservan el valor guardado, y el rango 1–30 se valida sólo si llegan.
    [Fact]
    public void TheSettingsValidatorKeepsAnAbsentSurveyAndChecksAutoCloseOnlyWhenPresent()
    {
        static IEnumerable<string> Errors(bool? survey, int? autoClose) =>
            new Modules.Support.Application.UpdateSupportSettingsValidator()
                .Validate(Modules.Support.Application.UpdateSupportSettingsCommand.From(
                    TenantId, new Modules.Support.Application.UpdateSupportSettingsRequest(3, null, survey, autoClose), 1))
                .Errors.Select(error => error.PropertyName);

        Assert.Empty(Errors(true, 1));
        Assert.Empty(Errors(false, 30));
        Assert.Empty(Errors(null, 5));
        Assert.Empty(Errors(true, null));
        Assert.Empty(Errors(null, null));
        Assert.Equal(["AutoCloseBusinessDays"], Errors(true, 0));
        Assert.Equal(["AutoCloseBusinessDays"], Errors(null, 31));
    }

    [Fact]
    public void AnAbsentSurveyOrAutoCloseTravelsAsNullToTheCommand()
    {
        var command = Modules.Support.Application.UpdateSupportSettingsCommand.From(
            TenantId, new Modules.Support.Application.UpdateSupportSettingsRequest(3), 1);

        Assert.True(command.SurveyEnabled is null && command.AutoCloseBusinessDays is null && command.FormalNextStep is null);
    }

    // Task-notices spec 2026-11-22 §5.4: the WhatsApp connection of the task notices is one more group of the same PUT.
    [Fact]
    public void ChangingTheTaskNoticeConnectionBumpsTheVersionOnce()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        var connection = Guid.CreateVersion7();

        var changes = settings.Update(
            6, "Texto", surveyEnabled: true, autoCloseBusinessDays: 9, settings.UnassignedAlert, connection, Now.AddHours(1));

        Assert.Equal(new SupportTenantSettingsChanges(true, true, true, true, false, true), changes);
        Assert.Equal((connection, 2L), (settings.TaskNoticeConnectionId, settings.Version));
    }

    [Fact]
    public void TheSameConnectionChangesNothing()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        var connection = Guid.CreateVersion7();
        settings.Update(3, null, false, 5, settings.UnassignedAlert, connection, Now.AddHours(1));

        var changes = settings.Update(3, null, false, 5, settings.UnassignedAlert, connection, Now.AddHours(2));

        Assert.False(changes.Any);
        Assert.Equal((2L, Now.AddHours(1)), (settings.Version, settings.UpdatedAt));
    }

    [Fact]
    public void ClearingTheConnectionIsAChange()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        settings.Update(3, null, false, 5, settings.UnassignedAlert, Guid.CreateVersion7(), Now.AddHours(1));

        var changes = settings.Update(3, null, false, 5, settings.UnassignedAlert, null, Now.AddHours(2));

        Assert.True(changes.TaskNotice);
        Assert.Null(settings.TaskNoticeConnectionId);
        Assert.Equal(3L, settings.Version);
    }

    [Fact]
    public void TheShortcutsKeepTheConnection()
    {
        var settings = SupportTenantSettings.CreateDefault(TenantId, Now);
        var connection = Guid.CreateVersion7();
        settings.Update(3, null, false, 5, settings.UnassignedAlert, connection, Now.AddHours(1));

        settings.ChangeDueSoon(7, Now.AddHours(2));
        settings.ChangeSurvey(true, 8, Now.AddHours(3));
        settings.ChangeFormalNextStep("Texto", Now.AddHours(4));

        Assert.Equal(connection, settings.TaskNoticeConnectionId);
    }
}
