using System.Text.Json.Nodes;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Ai.Infrastructure.Seed;
using Modules.Tenancy.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Spec 2026-11-23 §3 y §6: las plantillas de agente del sistema son JSON embebidos que se leen una vez al arrancar. Un archivo roto
/// tumba el host (no deja un agente con un valor por defecto en silencio), y cada plantilla real tiene que poder publicarse apenas el
/// tenant elija lo que le toca: proveedor y modelo, conexiones y miembro del traspaso. Copia de <c>SystemTemplatesTests</c> de Automations.
/// </summary>
public sealed class SystemAgentTemplatesTests
{
    // Perezoso a propósito: un inicializador estático que lanza tumbaría también las pruebas con archivos en memoria de esta clase.
    private static readonly Lazy<SystemAgentTemplateCatalog> Real = new(() => new SystemAgentTemplateCatalog());

    private static readonly AgentModelRef DefaultModel = new("anthropic", "claude-opus-5-5");

    private static SystemAgentTemplateCatalog Catalog => Real.Value;

    private static JsonObject Valid(string id = "prueba") => new()
    {
        ["id"] = id,
        ["name"] = "Prueba",
        ["description"] = "Una plantilla de prueba.",
        ["requires"] = new JsonArray("scheduling"),
        ["config"] = new JsonObject
        {
            ["instructions"] = "Atiende a los clientes.",
            ["tone"] = "friendly",
            ["greeting"] = null,
            ["faq"] = new JsonArray(),
            ["useQuickReplies"] = false,
            ["answerOnlyFromSources"] = true,
            ["tools"] = new JsonObject { ["catalog"] = false, ["orderStatus"] = false, ["quotationDraft"] = false, ["scheduling"] = true },
            ["handoff"] = new JsonObject
            {
                ["destination"] = "unassigned",
                ["memberId"] = null,
                ["message"] = null,
                ["maxRepliesWithoutHuman"] = 20,
                ["maxConsecutiveFailures"] = 2,
            },
            ["trigger"] = "NewConversation",
        },
    };

    private static (string ResourceName, string Json) File(JsonObject json, string? fileId = null) =>
        ($"Modules.Ai.Infrastructure.Seed.AgentTemplates.{fileId ?? json["id"]!.GetValue<string>()}.json", json.ToJsonString());

    [Fact]
    public void TheCatalogHasExactlyTheAppointmentAssistant()
    {
        Assert.Equal(["asistente-de-citas"], Catalog.All.Select(template => template.Id));
        var template = Catalog.Find("asistente-de-citas")!;
        Assert.Equal("Asistente de citas", template.Name);
        Assert.Equal([TenantModuleKeys.Scheduling], template.Requires);
    }

    // Las reglas de publicar menos lo que el tenant todavía tiene que elegir (proveedor y modelo, conexiones, miembro del traspaso).
    [Fact]
    public void EverySystemAgentTemplateIsPublishableOnceTheTenantChoosesItsPieces()
    {
        foreach (var template in Catalog.All)
        {
            var config = template.ConfigFor(DefaultModel);

            Assert.True(new AgentConfigFieldsValidator().Validate(config).IsValid, template.Id);
            var issues = AgentConfigValidator.Validate(
                config,
                new AgentValidationContext(
                    ConnectionCount: 1,
                    AgentModelAvailability.Available,
                    HandoffMemberCanReply: null,
                    Publishing: true,
                    Scheduling: config.Tools.Scheduling ? new AgentSchedulingReadiness(true, 1) : null));
            Assert.DoesNotContain(issues, issue => issue.Severity == AgentIssueSeverity.Error);
            Assert.Equal(template.Requires.Contains(TenantModuleKeys.Scheduling), config.Tools.Scheduling);
            Assert.True(template.Name.Length <= Agent.NameMaxLength);
        }
    }

    [Fact]
    public void TheAppointmentAssistantKeepsItsContract()
    {
        var config = Catalog.Find("asistente-de-citas")!.ConfigFor(DefaultModel);

        Assert.Equal("friendly", config.Tone);
        Assert.Equal(new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: false, Scheduling: true), config.Tools);
        Assert.True(config.AnswerOnlyFromSources);
        Assert.False(config.UseQuickReplies);
        Assert.Equal(["horarios-de-atencion", "cancelar-o-reprogramar"], config.Faq.Select(faq => faq.Id));
        Assert.Equal(
            new AgentHandoff("unassigned", null, "Te paso con una persona del equipo para que te ayude por este mismo chat.", 20, 2),
            config.Handoff);
        Assert.Equal("NewConversation", config.Trigger);
        Assert.StartsWith("¡Hola! Soy el asistente de citas.", config.Greeting, StringComparison.Ordinal);
        Assert.Contains("check_availability", config.Instructions, StringComparison.Ordinal);
        Assert.Contains("book_appointment", config.Instructions, StringComparison.Ordinal);
        Assert.Null(config.QuotationDraft);
    }

    [Fact]
    public void MissingModulesAreTheRequiredOnesThatAreOff()
    {
        var template = Catalog.Find("asistente-de-citas")!;

        Assert.Empty(AgentTemplateModules.Missing(template, null));
        Assert.Equal(
            [TenantModuleKeys.Scheduling],
            AgentTemplateModules.Missing(template, TenantModuleSet.FromStored([TenantModuleKeys.Customers])));
        Assert.Empty(AgentTemplateModules.Missing(template, TenantModuleSet.FromStored([TenantModuleKeys.Customers, TenantModuleKeys.Scheduling])));
        // Sin customers, scheduling no es efectiva (depende de él): sigue faltando.
        Assert.Equal(
            [TenantModuleKeys.Scheduling],
            AgentTemplateModules.Missing(template, TenantModuleSet.FromStored([TenantModuleKeys.Scheduling])));
    }

    [Fact]
    public void AValidFileLoadsWithItsModulesAndItsConfig()
    {
        var catalog = new SystemAgentTemplateCatalog([File(Valid())]);

        Assert.Single(catalog.All);
        var template = catalog.Find("prueba")!;
        Assert.Equal([TenantModuleKeys.Scheduling], template.Requires);
        var config = template.ConfigFor(new AgentModelRef("anthropic", "claude-opus-5-5"));
        Assert.True(config.Tools.Scheduling);
        Assert.Equal("claude-opus-5-5", config.Model.Id);
        Assert.Null(catalog.Find("otra"));
    }

    [Fact]
    public void ACatalogWithoutTemplateResourcesDoesNotStart()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new SystemAgentTemplateCatalog(typeof(SystemAgentTemplatesTests).Assembly));

        Assert.Contains("No hay plantillas de agente del sistema", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json", "no se puede leer")]
    [InlineData("missing-config", "Falta «config»")]
    [InlineData("id-not-kebab", "kebab-case")]
    [InlineData("id-not-file-name", "kebab-case")]
    [InlineData("duplicate-id", "dos plantillas")]
    [InlineData("name-too-long", "«name»")]
    [InlineData("description-too-long", "«description»")]
    [InlineData("missing-id", "Falta «id»")]
    [InlineData("missing-name", "Falta «name»")]
    [InlineData("missing-requires", "Falta «requires»")]
    [InlineData("unknown-module", "«agenda», que no existe")]
    [InlineData("repeated-module", "repite el módulo")]
    [InlineData("config-with-model", "«model»")]
    [InlineData("config-with-quotation-draft", "«quotationDraft»")]
    [InlineData("config-with-member", "«handoff.memberId»")]
    [InlineData("unknown-config-property", "no se puede leer")]
    public void ABrokenTemplateDoesNotStart(string mutation, string fragment)
    {
        var json = Valid();
        var files = new List<(string, string)>();
        switch (mutation)
        {
            case "not-json":
                files.Add(("Modules.Ai.Infrastructure.Seed.AgentTemplates.prueba.json", "{"));
                break;
            case "missing-config":
                json.Remove("config");
                files.Add(File(json));
                break;
            case "id-not-kebab":
                json["id"] = "Prueba_1";
                files.Add(File(json));
                break;
            case "id-not-file-name":
                files.Add(File(json, fileId: "otra"));
                break;
            case "duplicate-id":
                files.Add(File(json));
                files.Add(File(Valid()));
                break;
            case "name-too-long":
                json["name"] = new string('a', 81);
                files.Add(File(json));
                break;
            case "description-too-long":
                json["description"] = new string('a', 301);
                files.Add(File(json));
                break;
            case "missing-id":
            case "missing-name":
            case "missing-requires":
                json.Remove(mutation["missing-".Length..]);
                files.Add(File(json, fileId: "prueba"));
                break;
            case "unknown-module":
                json["requires"] = new JsonArray("agenda");
                files.Add(File(json));
                break;
            case "repeated-module":
                json["requires"] = new JsonArray("scheduling", "scheduling");
                files.Add(File(json));
                break;
            case "config-with-model":
                json["config"]!["model"] = new JsonObject { ["provider"] = "anthropic", ["id"] = "x" };
                files.Add(File(json));
                break;
            case "config-with-quotation-draft":
                json["config"]!["quotationDraft"] = new JsonObject { ["advisorMemberId"] = Guid.NewGuid(), ["billingAccount"] = null };
                files.Add(File(json));
                break;
            case "config-with-member":
                json["config"]!["handoff"]!["memberId"] = Guid.NewGuid();
                files.Add(File(json));
                break;
            case "unknown-config-property":
                json["config"]!["answerOnlyFromSource"] = true;
                files.Add(File(json));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var error = Assert.Throws<InvalidOperationException>(() => new SystemAgentTemplateCatalog(files));

        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
    }
}
