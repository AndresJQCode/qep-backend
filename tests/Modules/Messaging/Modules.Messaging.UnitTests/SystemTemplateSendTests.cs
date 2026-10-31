using Modules.Messaging.Application;
using Modules.Tenancy.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Plantillas editables: el envío del sistema se arma desde la fila aprobada, con las variables que ella usa y en su orden.</summary>
public sealed class SystemTemplateSendTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid MemberId = Guid.CreateVersion7();

    private static readonly Dictionary<string, string> AllValues = new()
    {
        ["nombre_contacto"] = "Laura", ["empresa"] = "Andina", ["valor_total"] = "10",
    };

    private static ApprovedTemplate Row(string parameterFormat, string body, string? footer = null)
    {
        var draft = new TemplateDraft("qep_test", "es", "UTILITY", null, body, footer, [], new Dictionary<string, string>());
        return new ApprovedTemplate("qep_test", "es", "UTILITY", parameterFormat, TemplateComponentsBuilder.Components(draft).ToJsonString());
    }

    [Fact]
    public void SendsOnlyTheVariablesTheRowUsesInItsOrder()
    {
        // Con "vigencia" disponible: la fila no la usa, y no puede viajar ni quedar en el details.
        var values = new Dictionary<string, string>(AllValues) { ["vigencia"] = "1 de enero" };
        var built = SystemTemplateSend.Build(Row("NAMED", "Hola {{nombre_contacto}}, de {{empresa}}, tu total: {{valor_total}} hoy."), values);
        Assert.True(built!.Send.Named);
        Assert.Equal(["nombre_contacto", "empresa", "valor_total"], built.Send.Parameters.Select(parameter => parameter.Key));
        Assert.Equal("Hola Laura, de Andina, tu total: 10 hoy.", built.Text);
        Assert.DoesNotContain("vigencia", built.DetailsJson);
    }

    [Fact]
    public void ALegacyRowKeepsItsFooterInTheText()
    {
        var built = SystemTemplateSend.Build(Row("NAMED", "Hola {{fullname}}, ya salió.", footer: "Este mensaje fue generado automáticamente."), AllValues);
        Assert.Equal("Hola Laura, ya salió.\n\nEste mensaje fue generado automáticamente.", built!.Text);
    }

    [Fact]
    public void APositionalRowTakesItsValuesInTheLegacyOrder()
    {
        var built = SystemTemplateSend.Build(Row("POSITIONAL", "Hola {{1}}, debes {{2}} hoy."), AllValues, ["nombre_contacto", "valor_total"]);
        Assert.False(built!.Send.Named);
        Assert.Equal([("1", "Laura"), ("2", "10")], built.Send.Parameters.Select(parameter => (parameter.Key, parameter.Value)));
    }

    // Una fila rara no puede tumbar el envío: una posición que no cabe en un entero no lanza, no arma nada.
    [Fact]
    public void AnOversizedPositionalKeyBuildsNothingInsteadOfThrowing() =>
        Assert.Null(SystemTemplateSend.Build(Row("POSITIONAL", "Hola {{99999999999999999999}}."), AllValues, ["nombre_contacto"]));

    // Review Focus 1: si Meta no devuelve parameter_format, el refresco guarda POSITIONAL.
    [Fact]
    public void NamedKeysAreNamedWhateverTheStoredFormatSays() =>
        Assert.True(SystemTemplateSend.Build(Row("POSITIONAL", "Hola {{nombre_contacto}}, ya salió."), AllValues)!.Send.Named);

    [Fact]
    public void AMissingOrInvalidValueBuildsNothing()
    {
        Assert.Null(SystemTemplateSend.Build(Row("NAMED", "Hola {{servicio}}, ya salió."), AllValues));
        Assert.Null(SystemTemplateSend.Build(Row("NAMED", "Hola {{nombre_contacto}}, ya salió."), new Dictionary<string, string> { ["nombre_contacto"] = "a\nb" }));
    }

    [Fact]
    public void ThePositionalKeysOfARowTellWhichVariablesItUses()
    {
        Assert.Equal(["nombre_contacto", "empresa"], SystemTemplateSend.VariablesOf(Row("NAMED", "Hola {{nombre_contacto}} de {{empresa}}.")));
    }

    [Fact]
    public async Task TheAdvisorIsTheSendersDisplayName()
    {
        var values = await AddAsync(displayName: "Ana Gomez", tenantName: "Andina", ["asesor", "empresa"]);
        Assert.Equal(("Ana Gomez", "Andina"), (values["asesor"], values["empresa"]));
    }

    // Review Focus 2.
    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Ana\nGomez")]
    public async Task AnAdvisorWithoutAUsableNameFallsBackToTheCompany(string? displayName) =>
        Assert.Equal("Andina", (await AddAsync(displayName, "Andina", ["asesor"]))["asesor"]);

    [Fact]
    public async Task NamesTheRowDoesNotUseAreNotLookedUp() => Assert.Empty(await AddAsync("Ana", "Andina", ["nombre_contacto"], failIfCalled: true));

    // Review Focus 4.
    [Fact]
    public void AnOldRowKeepsItsFourKeysWithTheValuesOfTheSpanishOnes()
    {
        var row = Row("NAMED", "¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*.");
        var built = SystemTemplateSend.Build(row, new Dictionary<string, string>
        {
            ["nombre_contacto"] = "Laura", ["numero_cotizacion"] = "COT-1", ["valor_total"] = "10", ["vigencia"] = "1 de enero",
        });
        Assert.Equal(
            [("fullname", "Laura"), ("order_number", "COT-1"), ("total", "10"), ("valid_until", "1 de enero")],
            built!.Send.Parameters.Select(parameter => (parameter.Key, parameter.Value)));
        Assert.Equal("¡Hola, Laura! Adjuntamos la cotización *COT-1* por *10*, vigente hasta el *1 de enero*.", built.Text);
    }

    [Theory]
    [InlineData(9, "Buenos días")]
    [InlineData(15, "Buenas tardes")]
    [InlineData(20, "Buenas noches")]
    public async Task TheGreetingFollowsTheTenantsLocalHour(int localHour, string expected) =>
        Assert.Equal(expected, (await AddAsync("Ana", "Andina", ["saludo"], localHour: localHour))["saludo"]);

    [Fact]
    public async Task WithoutACompanyTheKeyIsNotWritten() =>
        Assert.DoesNotContain("empresa", (await AddAsync("Ana", null, ["empresa"])).Keys);

    private static async Task<Dictionary<string, string>> AddAsync(
        string? displayName, string? tenantName, string[] usedKeys, int localHour = 10, bool failIfCalled = false)
    {
        var values = new Dictionary<string, string>();
        var sut = new TemplateSenderValues(
            new Members(displayName, failIfCalled), new Tenants(tenantName, failIfCalled), new LocalHourClock(localHour));
        await sut.AddAsync(TenantId, MemberId, usedKeys, values, CancellationToken.None);
        return values;
    }

    private sealed class Members(string? displayName, bool failIfCalled) : IMessagingMemberNames
    {
        public Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not expected.");

        public Task<string?> FindDisplayNameAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
            failIfCalled ? throw new InvalidOperationException("The advisor was looked up.") : Task.FromResult(displayName);
    }

    private sealed class Tenants(string? name, bool failIfCalled) : IMessagingTenantNames
    {
        public Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            failIfCalled ? throw new InvalidOperationException("The company was looked up.") : Task.FromResult(name);
    }

    /// <summary>Bogotá (UTC-5): la hora local nunca coincide con la del instante en UTC.</summary>
    private sealed class LocalHourClock(int localHour) : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(
                new DateTimeOffset(2026, 10, 31, localHour, 0, 0, TimeSpan.FromHours(-5)),
                TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
    }
}
