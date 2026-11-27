using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>HR birthday notices, spec 2026-11-26 §5.3: the email of the daily digest. The names come from people, so the HTML encodes them; each
/// one links to the employee profile; and neither a birth year nor an age appears. The test names carry no accents on purpose:
/// <c>WebUtility.HtmlEncode</c> writes them as numeric entities, and these tests look at the escape of <c>&lt;</c>.</summary>
public sealed class HrBirthdayDigestEmailTemplateTests
{
    private const string BaseUrl = "https://qep.qcode.co/";

    private static readonly Guid AnaId = Guid.CreateVersion7();
    private static readonly Guid LuisId = Guid.CreateVersion7();

    private static EmailMessage Render(
        IReadOnlyList<(Guid Id, string Name)>? today = null,
        int? todayTotal = null,
        IReadOnlyList<(Guid Id, string Name)>? upcoming = null,
        int? upcomingTotal = null,
        DateOnly? upcomingDate = null)
    {
        today ??= [(AnaId, "Ana Diaz")];
        upcoming ??= [(LuisId, "Luis Rojas")];
        return HrBirthdayDigestEmailTemplate.Render(
            "jefe@example.com", today, todayTotal ?? today.Count, upcoming, upcomingTotal ?? upcoming.Count, upcomingDate ?? new DateOnly(2026, 12, 4), BaseUrl);
    }

    [Fact]
    public void SubjectIsTodayWhenThereAreBirthdaysToday()
    {
        var message = Render();

        Assert.Equal("jefe@example.com", message.ToAddress);
        Assert.Equal("Cumpleaños de hoy en tu equipo", message.Subject);
        Assert.Equal("hr.birthdays-digest.v1", HrBirthdayDigestEmailTemplate.TemplateRef);
        Assert.Contains("Ana Diaz", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Luis Rojas", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("viernes 4 de diciembre", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SubjectIsUpcomingWhenOnlyUpcoming()
    {
        var message = Render(today: [], todayTotal: 0);

        Assert.Equal("Próximos cumpleaños en tu equipo", message.Subject);
        Assert.DoesNotContain("Ana Diaz", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Luis Rojas", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void NoUpcomingSectionWhenThereIsNoLead()
    {
        var message = HrBirthdayDigestEmailTemplate.Render("jefe@example.com", [(AnaId, "Ana Diaz")], 1, [], 0, null, BaseUrl);

        Assert.DoesNotContain("Próximos", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("nadie", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesAreHtmlEncoded()
    {
        var message = Render(today: [(AnaId, "<b>Ana</b>")], upcoming: [(LuisId, "Luis & <i>Co</i>")]);

        Assert.Contains("&lt;b&gt;Ana&lt;/b&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Luis &amp; &lt;i&gt;Co&lt;/i&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Ana</b>", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void EachEmployeeLinksToTheProfile()
    {
        var message = Render();

        Assert.Equal($"https://qep.qcode.co/hr/employees/{AnaId:D}", AccountLinks.HrEmployee(BaseUrl, AnaId));
        Assert.Contains($"<a href=\"https://qep.qcode.co/hr/employees/{AnaId:D}\">", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains($"https://qep.qcode.co/hr/employees/{LuisId:D}", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains($"https://qep.qcode.co/hr/employees/{AnaId:D}", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ATotalAboveTheShownNamesAddsYNMas()
    {
        var message = Render(todayTotal: 4, upcomingTotal: 1);

        Assert.Contains("y 3 más", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("y 3 más", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("y 0 más", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLeadMeansNoUpcomingSection()
    {
        var message = HrBirthdayDigestEmailTemplate.Render("jefe@example.com", [(AnaId, "Ana Diaz")], 1, [], 0, null, BaseUrl);

        Assert.Equal("Cumpleaños de hoy en tu equipo", message.Subject);
        Assert.DoesNotContain("Próximos", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Próximos", message.HtmlBody, StringComparison.Ordinal);
    }
}
