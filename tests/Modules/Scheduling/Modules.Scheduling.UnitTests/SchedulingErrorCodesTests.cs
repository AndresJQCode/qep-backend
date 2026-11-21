using System.Reflection;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.UnitTests;

public sealed class SchedulingErrorCodesTests
{
    private static string[] Codes() =>
        typeof(SchedulingErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

    // Plan 2026-11-21, «Contrato HTTP»: sólo existen los códigos que el contrato nombra, todos con el prefijo de la Agenda.
    [Fact]
    public void EveryCodeBelongsToTheSchedulingNamespace() =>
        Assert.All(Codes(), code => Assert.StartsWith("scheduling.", code, StringComparison.Ordinal));

    [Fact]
    public void NoCodeRepeats() => Assert.Equal(Codes().Length, Codes().Distinct(StringComparer.Ordinal).Count());

    [Fact]
    public void TheContractCodesAreTheOnesTheFrontendWillMap() =>
        Assert.Equal(
            [
                "scheduling.appointment.before_start",
                "scheduling.appointment.invalid_transition",
                "scheduling.appointment.outside_rules",
                "scheduling.appointment.session_full",
                "scheduling.appointment.slot_taken",
                "scheduling.closure.overlaps_appointments",
                "scheduling.location.main_cannot_be_archived",
                "scheduling.reminder.connection_invalid",
                "scheduling.reminder.template_missing",
                "scheduling.resource.has_future_appointments",
                "scheduling.service.in_use",
                "scheduling.session.not_published_mode",
            ],
            Codes().Order(StringComparer.Ordinal));
}
