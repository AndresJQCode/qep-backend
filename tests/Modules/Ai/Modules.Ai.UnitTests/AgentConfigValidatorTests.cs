using System.Text.Json;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Spec 2026-10-29 §4.6 y D-A7, plan 2026-11-12 C-16: guardar siempre guarda y devuelve los problemas; publicar falla con los
/// errores. El modelo es <c>{ provider, id }</c>: fuera de la lista de su proveedor es aviso al guardar y error al publicar.</summary>
public sealed class AgentConfigValidatorTests
{
    private static AgentConfig Valid() =>
        AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")) with { Instructions = "Atiende a los clientes de la tienda." };

    private static AgentValidationContext Context(
        int connections = 1, AgentModelAvailability model = AgentModelAvailability.Available, bool? memberCanReply = null,
        bool publishing = false) =>
        new(connections, model, memberCanReply, publishing);

    [Fact]
    public void AValidConfigHasNoIssues() =>
        Assert.Empty(AgentConfigValidator.Validate(Valid(), Context()));

    [Fact]
    public void TheDefaultConfigOnlyMissesInstructionsAndConnections()
    {
        var issues = AgentConfigValidator.Validate(
            AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")), Context(connections: 0));

        Assert.Equal(
            [("instructions_missing", "instructions", AgentIssueSeverity.Error), ("connections_missing", "connections", AgentIssueSeverity.Warning)],
            issues.Select(issue => (issue.Code, issue.Path, issue.Severity)));
    }

    // Spec 2026-11-17 D-B6: un agente pensado sólo para bloques de chatbots no ocupa conexión propia; sin conexiones se publica con aviso.
    [Fact]
    public void AnAgentWithoutConnectionsCanBePublishedWithAWarning()
    {
        var issue = Assert.Single(AgentConfigValidator.Validate(Valid(), Context(connections: 0, publishing: true)));

        Assert.Equal(("connections_missing", "connections", AgentIssueSeverity.Warning), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void BlankInstructionsAreAnError()
    {
        var issue = Assert.Single(AgentConfigValidator.Validate(Valid() with { Instructions = "   " }, Context()));

        Assert.Equal(("instructions_missing", "instructions", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AModelOutsideItsProviderListIsAWarningWhenSaving()
    {
        var issue = Assert.Single(AgentConfigValidator.Validate(Valid(), Context(model: AgentModelAvailability.Unavailable)));

        Assert.Equal(("model_unavailable", "model", AgentIssueSeverity.Warning), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AModelOutsideItsProviderListIsAnErrorWhenPublishing()
    {
        var issue = Assert.Single(AgentConfigValidator.Validate(
            Valid(), Context(model: AgentModelAvailability.Unavailable, publishing: true)));

        Assert.Equal(("model_unavailable", "model", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AnUnknownProviderIsAlwaysAnError()
    {
        var issue = Assert.Single(AgentConfigValidator.Validate(
            Valid() with { Model = new AgentModelRef("gemini", "x") }, Context(model: AgentModelAvailability.UnknownProvider)));

        Assert.Equal(("model_provider_unknown", "model.provider", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AHandoffToAMemberNeedsTheMember()
    {
        var config = Valid() with { Handoff = Valid().Handoff with { Destination = AgentHandoff.ToMember, MemberId = null } };

        var issue = Assert.Single(AgentConfigValidator.Validate(config, Context()));

        Assert.Equal(("handoff_member_missing", "handoff.memberId", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AHandoffToAMemberWhoCannotReplyIsAnError()
    {
        var config = Valid() with { Handoff = Valid().Handoff with { Destination = AgentHandoff.ToMember, MemberId = Guid.NewGuid() } };

        var issue = Assert.Single(AgentConfigValidator.Validate(config, Context(memberCanReply: false)));

        Assert.Equal(("handoff_member_cannot_reply", "handoff.memberId", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void AHandoffToAMemberWhoCanReplyIsValid()
    {
        var config = Valid() with { Handoff = Valid().Handoff with { Destination = AgentHandoff.ToMember, MemberId = Guid.NewGuid() } };

        Assert.Empty(AgentConfigValidator.Validate(config, Context(memberCanReply: true)));
    }

    [Fact]
    public void TheConfigSurvivesItsJsonRoundTrip()
    {
        var config = Valid() with
        {
            Greeting = "Hola, soy el asistente.",
            Faq = [new AgentFaq("envios", "¿Hacen envíos?", "Sí, a todo el país.")],
            Tools = new AgentTools(Catalog: true, OrderStatus: false, QuotationDraft: true),
        };

        var read = AgentConfig.FromJson(config.ToJson());

        Assert.Equal(config.ToJson(), read.ToJson());
        Assert.Contains("\"model\":{\"provider\":\"anthropic\",\"id\":\"claude-opus-5-5\"}", config.ToJson(), StringComparison.Ordinal);
    }

    // P-4: la herramienta de cotización necesita asesora y cuenta de cobro para publicar.
    [Fact]
    public void TheQuotationToolWithoutAdvisorOrAccountIsAnError()
    {
        var config = Valid() with { Tools = new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: true) };

        var issues = AgentConfigValidator.Validate(config, Context());

        Assert.Equal(
            [
                ("quotation_advisor_missing", "quotationDraft.advisorMemberId", AgentIssueSeverity.Error),
                ("quotation_billing_account_missing", "quotationDraft.billingAccount", AgentIssueSeverity.Error),
            ],
            issues.Select(issue => (issue.Code, issue.Path, issue.Severity)));
    }

    // P-4: una asesora que ya no puede cotizar bloquea la herramienta con aviso; no impide publicar.
    [Fact]
    public void AnAdvisorWhoCanNoLongerQuoteIsAWarning()
    {
        var config = Valid() with
        {
            Tools = new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: true),
            QuotationDraft = new AgentQuotationDraft(Guid.NewGuid(), new AgentBillingAccount(Guid.NewGuid(), "Banco", "123", "COP")),
        };

        var issue = Assert.Single(AgentConfigValidator.Validate(
            config, new AgentValidationContext(1, AgentModelAvailability.Available, null, Publishing: true, QuotationAdvisorCanQuote: false)));

        Assert.Equal(
            ("quotation_advisor_cannot_quote", "quotationDraft.advisorMemberId", AgentIssueSeverity.Warning), (issue.Code, issue.Path, issue.Severity));
    }

    // Plan 2026-11-17 A3: la cuenta guardada que ya no está entre las elegibles (empresa desactivada o cuenta borrada) es aviso, como la
    // asesora que ya no cotiza: al correr, Quotations la rechaza con quotation.billing.account_not_found.
    [Fact]
    public void ASavedBillingAccountThatIsNoLongerEligibleIsAWarning()
    {
        var config = Valid() with
        {
            Tools = new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: true),
            QuotationDraft = new AgentQuotationDraft(Guid.NewGuid(), new AgentBillingAccount(Guid.NewGuid(), "Banco", "123", "COP")),
        };

        var issue = Assert.Single(AgentConfigValidator.Validate(
            config,
            new AgentValidationContext(
                1, AgentModelAvailability.Available, null, Publishing: true, QuotationAdvisorCanQuote: true,
                QuotationBillingAccountEligible: false)));

        Assert.Equal(
            ("quotation_billing_account_unavailable", "quotationDraft.billingAccount", AgentIssueSeverity.Warning),
            (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void WithTheQuotationToolOffItsSettingsAreNotRequired() =>
        Assert.Empty(AgentConfigValidator.Validate(Valid() with { QuotationDraft = null }, Context()));

    // Un documento guardado antes de quotationDraft se sigue leyendo, sin la configuración. Sólo falta la clave de primer nivel:
    // tools.quotationDraft (el interruptor) existe desde el agregado inicial (f25065ca), antes que la configuración (fa7879ca).
    [Fact]
    public void AConfigSavedBeforeTheQuotationSettingsStillReads()
    {
        var json = Valid().ToJson().Replace(",\"quotationDraft\":null", string.Empty, StringComparison.Ordinal);

        using (var document = JsonDocument.Parse(json))
        {
            Assert.False(document.RootElement.TryGetProperty("quotationDraft", out _));
        }

        Assert.Null(AgentConfig.FromJson(json).QuotationDraft);
    }

    // Spec 2026-11-23 §9.2: con la herramienta de la Agenda prendida, publicar exige el módulo y un servicio reservable.
    private static AgentConfig WithScheduling(bool on = true) =>
        Valid() with { Tools = new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: false, Scheduling: on) };

    private static AgentValidationContext SchedulingContext(AgentSchedulingReadiness? readiness) =>
        new(1, AgentModelAvailability.Available, null, false, Scheduling: readiness);

    [Fact]
    public void SchedulingWithTheModuleOffIsAnError()
    {
        var issues = AgentConfigValidator.Validate(WithScheduling(), SchedulingContext(new AgentSchedulingReadiness(false, 0)));

        var issue = Assert.Single(issues);
        Assert.Equal(("scheduling_module_disabled", "tools.scheduling", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void SchedulingWithoutBookableServicesIsAnError()
    {
        var issues = AgentConfigValidator.Validate(WithScheduling(), SchedulingContext(new AgentSchedulingReadiness(true, 0)));

        var issue = Assert.Single(issues);
        Assert.Equal(("scheduling_no_bookable_service", "tools.scheduling", AgentIssueSeverity.Error), (issue.Code, issue.Path, issue.Severity));
    }

    [Fact]
    public void SchedulingReadyHasNoIssue()
    {
        var readiness = new AgentSchedulingReadiness(true, 2);

        Assert.True(readiness.Ready);
        Assert.Empty(AgentConfigValidator.Validate(WithScheduling(), SchedulingContext(readiness)));
    }

    [Fact]
    public void SchedulingOffIgnoresTheReadiness() =>
        Assert.DoesNotContain(
            AgentConfigValidator.Validate(WithScheduling(on: false), SchedulingContext(new AgentSchedulingReadiness(false, 0))),
            issue => issue.Code.StartsWith("scheduling_", StringComparison.Ordinal));
}
