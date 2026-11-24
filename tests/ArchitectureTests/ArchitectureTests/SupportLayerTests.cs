using System.Reflection;
using BuildingBlocks.Application;
using Modules.Support.Api;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure;

namespace ArchitectureTests;

public sealed class SupportLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(SupportDomainException).Assembly,
            typeof(SupportPermissions).Assembly,
            typeof(SupportInfrastructureExtensions).Assembly,
            typeof(SupportEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(SupportPermissions).Assembly,
            typeof(SupportInfrastructureExtensions).Assembly,
            typeof(SupportEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(SupportInfrastructureExtensions).Assembly,
            typeof(SupportEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. el nombre de tipo repetido, IX_ticket_types_tenant_name) es tarea de
    // Infrastructure, no de Application.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(SupportPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    // Spec 2026-11-15 §6.3 (plan L2): the legal HTML is sanitized behind ISupportHtmlSanitizer; only Infrastructure knows the library.
    [Fact]
    public void ApplicationDoesNotReferenceTheHtmlSanitizer()
    {
        foreach (var assembly in new[] { typeof(SupportDomainException).Assembly, typeof(SupportPermissions).Assembly })
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null &&
                (name.StartsWith("HtmlSanitizer", StringComparison.Ordinal) ||
                 name.StartsWith("Ganss.Xss", StringComparison.Ordinal) ||
                 name.StartsWith("AngleSharp", StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// `support` no se acopla a ningún otro módulo de negocio (spec 2026-11-14). La única dependencia legítima hacia
    /// afuera es `Modules.Tenancy.*` (<c>IExecutionContext</c>, <c>ITenantModules</c>). Clientes, miembros y el calendario
    /// de la bandeja entran por los puertos <c>ISupport*</c> declarados acá, cuyos adaptadores viven en `Bootstrapper`:
    /// Soporte nunca referencia Mensajería. Sin esta aserción esa decisión es un comentario.
    /// <para>Una excepción deliberada (spec 2026-11-22, D-14): el horario de atención entra por <c>IBusinessHoursProvider</c>, un contrato
    /// compartido de <c>BuildingBlocks.Application</c> que implementa Mensajería, y no por un puerto <c>ISupport*</c> con adaptador. Es una
    /// capacidad que varios módulos van a usar, y un contrato compartido evita un adaptador por consumidor. La aserción no cambia: BuildingBlocks
    /// no es un módulo de negocio.</para>
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(SupportPermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Support", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));
    }

    // Spec 2026-11-15 §9.3 (plan L30): the company that answers on the portal is read through ISupportOrganizationDirectory, whose
    // adapter lives in Bootstrapper. No layer of Support references Companies, not even for the FK, which is SQL in the migration.
    [Fact]
    public void SupportDoesNotReferenceCompanies()
    {
        foreach (var assembly in new[]
        {
            typeof(SupportDomainException).Assembly,
            typeof(SupportPermissions).Assembly,
            typeof(SupportInfrastructureExtensions).Assembly,
            typeof(SupportEndpoints).Assembly,
        })
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null && name.StartsWith("Modules.Companies", StringComparison.Ordinal));
        }
    }

    // Spec 2026-11-22 §5.6 (plan Task 6): una sola puerta para todo ticket que toca un miembro. Un miembro que sólo usa tareas no puede
    // llegar a un ticket de cliente por ningún camino, así que cada handler de ticket tiene que pasar por SupportTicketScope en vez de
    // autorizar y cargar por su cuenta. Un handler nuevo con TicketId que se salte la puerta pone esto en rojo.
    private static readonly string[] ScopeGates =
        [nameof(SupportTicketScope.ViewAsync), nameof(SupportTicketScope.ActAsync), nameof(SupportTicketScope.ManageAsync), nameof(SupportTicketScope.FindManagedAsync)];

    [Fact]
    public void EveryTicketHandlerGoesThroughTheScope()
    {
        var application = typeof(SupportPermissions).Assembly;
        var handlers = TicketHandlers(application);
        Assert.NotEmpty(handlers);

        var skipping = handlers
            .Where(handler => !MethodsOfWithNested(application, handler)
                .SelectMany(IlScanner.CalleesOf)
                .Any(callee => callee.DeclaringType == typeof(SupportTicketScope) && ScopeGates.Contains(callee.Name)))
            .Select(handler => handler.Name)
            .Order()
            .ToList();

        Assert.True(skipping.Count == 0, $"Ticket handlers that skip SupportTicketScope: {string.Join(", ", skipping)}");
    }

    [Fact]
    public void NoTicketHandlerAuthorizesOnItsOwn()
    {
        var application = typeof(SupportPermissions).Assembly;
        var authorization = application.GetType("Modules.Support.Application.SupportAuthorization")
            ?? throw new InvalidOperationException("SupportAuthorization was not found.");
        string[] forbidden = ["EnsureAsync", "EnsureCanViewAsync"];

        var offenders = TicketHandlers(application)
            .Where(handler => MethodsOfWithNested(application, handler)
                .SelectMany(IlScanner.CalleesOf)
                .Any(callee => callee.DeclaringType == authorization && forbidden.Contains(callee.Name)))
            .Select(handler => handler.Name)
            .Order()
            .ToList();

        Assert.True(offenders.Count == 0, $"Ticket handlers that authorize on their own: {string.Join(", ", offenders)}");
    }

    // Quién más puede cargar un ticket por id sin la puerta: los caminos del sistema y del portal, que no tienen miembro, y las dos
    // relecturas de SendTicketReplyHandler después de la puerta. Los listados por conversación no cargan un ticket por id y los internos
    // no tienen conversación ni cliente (la exclusión explícita va en la Tarea 14).
    private static readonly (string Type, string Why)[] SystemPathsThatLoadATicket =
    [
        ("SupportAutoCloser", "system worker: closes resolved tickets, no member involved"),
        ("SupportPublicSurveys", "portal survey lookup by signed token (PublicSurveys.cs:55), no member"),
        ("SupportSurveyAnswering", "portal survey answer by signed token (PublicSurveys.cs:133), no member"),
        ("SupportSurveyAnswers", "survey answers service, no member"),
        ("SupportSurveySender", "system worker: sends the survey, no member"),
        ("SendTicketReplyHandler", "re-reads after the gate already admitted the member (SendTicketReply.cs:248 and :551)"),
    ];

    [Fact]
    public void OnlyTheScopeAndTheListedSystemPathsLoadATicket()
    {
        var application = typeof(SupportPermissions).Assembly;
        var allowed = SystemPathsThatLoadATicket.Select(entry => entry.Type).ToHashSet(StringComparer.Ordinal);

        var callers = IlScanner.CallersOf(
            application,
            callee => callee.DeclaringType == typeof(ISupportTicketRepository) && callee.Name == nameof(ISupportTicketRepository.FindAsync));

        var offenders = callers
            .Where(method => !IlScanner.IsWithin(method, typeof(SupportTicketScope)))
            .Select(method => OutermostType(method).Name)
            .Where(name => !allowed.Contains(name))
            .Distinct()
            .Order()
            .ToList();

        Assert.True(offenders.Count == 0, $"Types that load a ticket without the scope and are not listed: {string.Join(", ", offenders)}");
    }

    // Los handlers (comandos y consultas) de Application cuyo pedido lleva un TicketId.
    private static List<Type> TicketHandlers(Assembly application) => application
        .GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false })
        .Where(type => type.GetInterfaces().Any(contract =>
            contract.IsGenericType
            && (contract.GetGenericTypeDefinition() == typeof(ICommandHandler<,>) || contract.GetGenericTypeDefinition() == typeof(IQueryHandler<,>))
            && contract.GetGenericArguments()[0].GetProperty("TicketId") is not null))
        .ToList();

    // El handler y sus tipos anidados: la máquina de estados del async y las clausuras viven ahí y es donde están las llamadas.
    private static IEnumerable<MethodBase> MethodsOfWithNested(Assembly application, Type handler) =>
        IlScanner.MethodsOf(application.GetTypes().Where(type => OutermostType(type) == handler));

    private static Type OutermostType(MethodBase method) => OutermostType(method.DeclaringType!);

    private static Type OutermostType(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    private static string?[] ReferenceNamesOf(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    private static void AssertDoesNotReference(
        Assembly source,
        params Assembly[] forbiddenAssemblies)
    {
        var references = source
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in forbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden.GetName().Name, references);
        }
    }
}
