using System.Text.Json;
using Modules.Messaging.Api;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Revisión final: un <c>scheduledAt</c> sin zona horaria es UTC, nunca la hora del servidor. Sin contenedor: el multipart pasa por
/// <see cref="ScheduledAtInput.Parse"/> y las rutas JSON por los records del contrato con las opciones web de System.Text.Json.</summary>
public sealed class ScheduledAtInputTests
{
    private static readonly DateTimeOffset Utc = new(2026, 11, 10, 15, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-11-10T15:30:00")]
    [InlineData("2026-11-10T15:30:00Z")]
    [InlineData("2026-11-10T10:30:00-05:00")]
    public void TheVoiceNoteFormReadsAnOffsetLessValueAsUtc(string value)
    {
        var parsed = ScheduledAtInput.Parse(value);

        Assert.Equal(Utc, parsed);
        Assert.Equal(TimeSpan.Zero, parsed!.Value.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("mañana")]
    public void AnUnreadableFormValueIsNull(string? value) => Assert.Null(ScheduledAtInput.Parse(value));

    [Theory]
    [InlineData("2026-11-10T15:30:00")]
    [InlineData("2026-11-10T15:30:00Z")]
    [InlineData("2026-11-10T10:30:00-05:00")]
    public void TheJsonRoutesReadAnOffsetLessValueAsUtc(string value)
    {
        var create = JsonSerializer.Deserialize<ScheduleMessageRequest>($$"""{"scheduledAt":"{{value}}"}""", JsonSerializerOptions.Web)!;
        var update = JsonSerializer.Deserialize<UpdateScheduledMessageRequest>($$"""{"scheduledAt":"{{value}}"}""", JsonSerializerOptions.Web)!;

        Assert.Equal((Utc, TimeSpan.Zero), (create.ScheduledAt!.Value, create.ScheduledAt.Value.Offset));
        Assert.Equal((Utc, TimeSpan.Zero), (update.ScheduledAt!.Value, update.ScheduledAt.Value.Offset));
    }

    [Fact]
    public void AnAbsentJsonValueStaysNull()
    {
        var update = JsonSerializer.Deserialize<UpdateScheduledMessageRequest>("""{"scheduledAt":null,"expectedVersion":3}""", JsonSerializerOptions.Web)!;

        Assert.Null(update.ScheduledAt);
        Assert.Null(JsonSerializer.Deserialize<ScheduleMessageRequest>("{}", JsonSerializerOptions.Web)!.ScheduledAt);
    }
}
