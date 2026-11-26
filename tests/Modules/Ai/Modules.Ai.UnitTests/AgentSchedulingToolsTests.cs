using System.Text.Json;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Spec 2026-11-23 §5, §7.5 y §7.7: las seis herramientas de la Agenda y sus tres gemelas simuladas del playground. Las reglas las aplica el
/// puerto (adaptador en Bootstrapper); acá se amarra lo que la herramienta hace con los argumentos, la llave, las horas y los traspasos.
/// </summary>
public sealed class AgentSchedulingToolsTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid JobId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid Service = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid Appointment = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    private readonly FakeAiScheduling _scheduling = new();

    public static TheoryData<string> BadCheckAvailabilityArguments => new()
    {
        """{"serviceId":"no-es-guid","fromDate":"2026-11-24","toDate":"2026-11-25","resourceId":null}""",
        """{"serviceId":"01900000-0000-7000-8000-0000000000b1","fromDate":"24/11/2026","toDate":"2026-11-25","resourceId":null}""",
        """{"serviceId":"01900000-0000-7000-8000-0000000000b1","fromDate":"2026-11-24","toDate":"2026-12-08","resourceId":null}""",
        """{"serviceId":"01900000-0000-7000-8000-0000000000b1","fromDate":"2026-11-25","toDate":"2026-11-24","resourceId":null}""",
        """{"serviceId":"01900000-0000-7000-8000-0000000000b1","fromDate":"2026-11-24","toDate":"2026-11-25","resourceId":"x"}""",
    };

    [Theory]
    [MemberData(nameof(BadCheckAvailabilityArguments))]
    public async Task CheckAvailabilityRejectsBadArguments(string json)
    {
        var result = await new CheckAvailabilityTool(_scheduling).ExecuteAsync(Invocation(), Json(json), TestContext.Current.CancellationToken);

        Assert.Equal(AgentToolErrors.InvalidArguments, result.ErrorCode);
        Assert.Equal(0, _scheduling.Calls);
    }

    [Fact]
    public async Task CheckAvailabilityAcceptsFourteenDaysInclusive()
    {
        var json = $$"""{"serviceId":"{{Service}}","fromDate":"2026-11-24","toDate":"2026-12-07","resourceId":null}""";

        var result = await new CheckAvailabilityTool(_scheduling).ExecuteAsync(Invocation(), Json(json), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task CheckAvailabilityCutsAtThirtyStartsAndSaysSo()
    {
        _scheduling.Availability = new AiAvailability(
            "America/Bogota", Service, Enumerable.Range(0, 31).Select(i => Slot(9 + (i / 60), i % 60)).ToList(), false);
        var json = $$"""{"serviceId":"{{Service}}","fromDate":"2026-11-24","toDate":"2026-11-24","resourceId":null}""";

        var result = await new CheckAvailabilityTool(_scheduling).ExecuteAsync(Invocation(), Json(json), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result.ResultJson);
        Assert.Equal(30, document.RootElement.GetProperty("slots").GetArrayLength());
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task BookRejectsANoteOver500()
    {
        var json = Book(note: new string('a', 501));

        var result = await new BookAppointmentTool(_scheduling).ExecuteAsync(Invocation(), Json(json), TestContext.Current.CancellationToken);

        Assert.Equal(AgentToolErrors.InvalidArguments, result.ErrorCode);
        Assert.Equal(0, _scheduling.Calls);
    }

    [Fact]
    public async Task BookRejectsAStartThatIsNotLocalMinutes()
    {
        var json = Book(startsAt: "2026-11-24T09:00:00-05:00");

        var result = await new BookAppointmentTool(_scheduling).ExecuteAsync(Invocation(), Json(json), TestContext.Current.CancellationToken);

        Assert.Equal(AgentToolErrors.InvalidArguments, result.ErrorCode);
    }

    [Fact]
    public async Task BookUsesTheJobAndOrdinalKey()
    {
        await new BookAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(ordinal: 2), Json(Book()), TestContext.Current.CancellationToken);

        Assert.Equal($"ai:{JobId:N}:book:2", _scheduling.LastBookKey);
        Assert.Equal(new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Unspecified), _scheduling.LastBookStart);
    }

    [Fact]
    public async Task BookFormatsLocalTimesWithoutOffset()
    {
        var result = await new BookAppointmentTool(_scheduling).ExecuteAsync(Invocation(), Json(Book()), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result.ResultJson);
        Assert.Equal("2026-11-24T09:00", document.RootElement.GetProperty("startsAt").GetString());
        Assert.Equal("2026-11-24T09:30", document.RootElement.GetProperty("endsAt").GetString());
        Assert.Equal("America/Bogota", document.RootElement.GetProperty("timeZone").GetString());
        Assert.False(document.RootElement.GetProperty("alreadyBooked").GetBoolean());
    }

    // D-14: el resultado describe la cita guardada aunque esta llamada pida otra hora.
    [Fact]
    public async Task AlreadyBookedDescribesTheStoredAppointment()
    {
        _scheduling.BookResult = AiSchedulingResult<AiAppointmentWrite>.Ok(Stored(hour: 15) with { AlreadyDone = true });

        var result = await new BookAppointmentTool(_scheduling).ExecuteAsync(Invocation(), Json(Book()), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result.ResultJson);
        Assert.True(document.RootElement.GetProperty("alreadyBooked").GetBoolean());
        Assert.Equal("2026-11-24T15:00", document.RootElement.GetProperty("startsAt").GetString());
    }

    [Fact]
    public async Task ChangeBlockedOnCancelReturnsTheHandoffCode()
    {
        _scheduling.CancelResult = AiSchedulingResult<AiAppointmentWrite>.HandedOff(
            AgentToolErrors.AppointmentChangeBlocked, AgentHandoffCodes.AppointmentChangeBlocked);

        var result = await new CancelAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(), Json($$"""{"appointmentId":"{{Appointment}}","reason":null}"""), TestContext.Current.CancellationToken);

        Assert.Equal(AgentToolErrors.AppointmentChangeBlocked, result.ErrorCode);
        Assert.Equal(new AgentToolHandoff("appointment_change_blocked", null), result.Handoff);
    }

    [Fact]
    public async Task CustomerUnknownOnBookHandsOff()
    {
        _scheduling.BookResult = AiSchedulingResult<AiAppointmentWrite>.HandedOff(
            AgentToolErrors.CustomerUnknown, AgentHandoffCodes.AppointmentCustomerUnavailable);

        var result = await new BookAppointmentTool(_scheduling).ExecuteAsync(Invocation(), Json(Book()), TestContext.Current.CancellationToken);

        Assert.Equal(AgentToolErrors.CustomerUnknown, result.ErrorCode);
        Assert.Equal("appointment_customer_unavailable", result.Handoff?.Code);
        Assert.Null(result.Handoff?.MemberId);
    }

    [Fact]
    public async Task ACodeWithoutHandoffGoesBackToTheModel()
    {
        _scheduling.BookResult = AiSchedulingResult<AiAppointmentWrite>.Error(AgentToolErrors.SlotTaken);

        var result = await new BookAppointmentTool(_scheduling).ExecuteAsync(Invocation(), Json(Book()), TestContext.Current.CancellationToken);

        Assert.Equal((true, AgentToolErrors.SlotTaken), (result.IsError, result.ErrorCode));
        Assert.Null(result.Handoff);
    }

    [Fact]
    public async Task CancelWithAnIdThatIsNotAGuidIsInvalid()
    {
        var result = await new CancelAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(), Json("""{"appointmentId":"PRUEBA-AB12CD34","reason":null}"""), TestContext.Current.CancellationToken);

        Assert.Equal(AgentToolErrors.InvalidArguments, result.ErrorCode);
    }

    [Fact]
    public async Task CancelAndRescheduleDescribeWhatExists()
    {
        var cancel = await new CancelAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(), Json($$"""{"appointmentId":"{{Appointment}}","reason":"Ya no puedo."}"""), TestContext.Current.CancellationToken);
        var move = await new RescheduleAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(), Json($$"""{"appointmentId":"{{Appointment}}","startsAt":"2026-11-25T10:00","resourceId":null}"""),
            TestContext.Current.CancellationToken);

        Assert.False(cancel.IsError);
        Assert.Equal("Ya no puedo.", _scheduling.LastCancelReason);
        using var document = JsonDocument.Parse(move.ResultJson);
        Assert.False(document.RootElement.GetProperty("alreadyDone").GetBoolean());
        Assert.Equal(new DateTime(2026, 11, 25, 10, 0, 0), _scheduling.LastRescheduleStart);
    }

    [Fact]
    public void RealWritesAreNotOfferedInTests()
    {
        var on = new AgentTools(false, false, false, Scheduling: true);

        Assert.All(
            new IAgentTool[] { new BookAppointmentTool(_scheduling), new CancelAppointmentTool(_scheduling), new RescheduleAppointmentTool(_scheduling) },
            tool => Assert.Equal((true, false), (tool.IsOfferedTo(on, false), tool.IsOfferedTo(on, true))));
    }

    [Fact]
    public void SimulatedWritesAreOnlyOfferedInTests()
    {
        var on = new AgentTools(false, false, false, Scheduling: true);

        Assert.All(
            new IAgentTool[]
            {
                new SimulatedBookAppointmentTool(_scheduling), new SimulatedCancelAppointmentTool(_scheduling),
                new SimulatedRescheduleAppointmentTool(_scheduling),
            },
            tool => Assert.Equal((false, true), (tool.IsOfferedTo(on, false), tool.IsOfferedTo(on, true))));
    }

    [Fact]
    public void ReadsAreOfferedInBoth()
    {
        var on = new AgentTools(false, false, false, Scheduling: true);

        Assert.All(
            new IAgentTool[] { new ListBookableServicesTool(_scheduling), new CheckAvailabilityTool(_scheduling), new ListMyAppointmentsTool(_scheduling) },
            tool => Assert.Equal((true, true), (tool.IsOfferedTo(on, false), tool.IsOfferedTo(on, true))));
    }

    [Fact]
    public void NothingIsOfferedWithTheSwitchOff()
    {
        var off = new AgentTools(false, false, false, Scheduling: false);

        Assert.All(AllTools(), tool => Assert.All([false, true], isTest => Assert.False(tool.IsOfferedTo(off, isTest))));
    }

    [Fact]
    public void EveryToolHasACapabilityOfThePrincipal() =>
        Assert.All(AllTools(), tool => Assert.Contains(tool.Capability, AiServicePrincipal.Capabilities));

    [Fact]
    public async Task SimulatedBookNeverWrites()
    {
        var result = await new SimulatedBookAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(isTest: true), Json(Book()), TestContext.Current.CancellationToken);

        Assert.Equal(0, _scheduling.BookCalls);
        using var document = JsonDocument.Parse(result.ResultJson);
        Assert.StartsWith("PRUEBA-", document.RootElement.GetProperty("appointmentId").GetString(), StringComparison.Ordinal);
        Assert.False(document.RootElement.GetProperty("alreadyBooked").GetBoolean());
        Assert.Equal("Pending", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SimulatedBookStillValidatesWhatTheRealDoes()
    {
        _scheduling.PreviewResult = AiSchedulingResult<AiStartPreview>.Error(AgentToolErrors.OutsideRules);

        var result = await new SimulatedBookAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(isTest: true), Json(Book()), TestContext.Current.CancellationToken);

        Assert.Equal(AgentToolErrors.OutsideRules, result.ErrorCode);
    }

    [Fact]
    public async Task SimulatedCancelOfAPruebaIdIsFoundAndWritesNothing()
    {
        var result = await new SimulatedCancelAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(isTest: true), Json("""{"appointmentId":"PRUEBA-AB12CD34","reason":null}"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal(0, _scheduling.CancelCalls);
        Assert.Contains("\"status\":\"Cancelled\"", result.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulatedCancelOfAnotherCustomersAppointmentIsNotFound()
    {
        _scheduling.FindResult = AiSchedulingResult<AiAppointment>.Error(AgentToolErrors.AppointmentNotFound);

        var result = await new SimulatedCancelAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(isTest: true), Json($$"""{"appointmentId":"{{Appointment}}","reason":null}"""), TestContext.Current.CancellationToken);

        Assert.Equal(AgentToolErrors.AppointmentNotFound, result.ErrorCode);
        Assert.Null(result.Handoff);
        Assert.Equal(0, _scheduling.CancelCalls);
    }

    [Fact]
    public async Task SimulatedCancelPastTheCutoffHandsOff()
    {
        _scheduling.FindResult = AiSchedulingResult<AiAppointment>.Ok(Stored(canChange: false).Appointment);

        var result = await new SimulatedCancelAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(isTest: true), Json($$"""{"appointmentId":"{{Appointment}}","reason":null}"""), TestContext.Current.CancellationToken);

        Assert.Equal(AgentToolErrors.AppointmentChangeBlocked, result.ErrorCode);
        Assert.Equal("appointment_change_blocked", result.Handoff?.Code);
    }

    [Fact]
    public async Task SimulatedRescheduleOfAServiceWithoutChatBookingHandsOff()
    {
        _scheduling.FindResult = AiSchedulingResult<AiAppointment>.Ok(Stored(canReschedule: false).Appointment);

        var result = await new SimulatedRescheduleAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(isTest: true),
            Json($$"""{"appointmentId":"{{Appointment}}","startsAt":"2026-11-25T10:00","resourceId":null}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal("appointment_change_blocked", result.Handoff?.Code);
        Assert.Equal(0, _scheduling.RescheduleCheckCalls);
    }

    [Fact]
    public async Task SimulatedRescheduleChecksTheMoveOfThatAppointment()
    {
        _scheduling.FindResult = AiSchedulingResult<AiAppointment>.Ok(Stored().Appointment);

        var result = await new SimulatedRescheduleAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(isTest: true),
            Json($$"""{"appointmentId":"{{Appointment}}","startsAt":"2026-11-25T10:00","resourceId":null}"""),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal((Appointment, new DateTime(2026, 11, 25, 10, 0, 0), (Guid?)null), _scheduling.LastRescheduleCheck);
        Assert.Equal((1, 0, 0), (_scheduling.RescheduleCheckCalls, _scheduling.PreviewCalls, _scheduling.RescheduleCalls));
    }

    [Fact]
    public async Task SimulatedRescheduleToTheSameTimeSaysAlreadyDone()
    {
        _scheduling.FindResult = AiSchedulingResult<AiAppointment>.Ok(Stored().Appointment);
        _scheduling.ReschedulePreviewResult = AiSchedulingResult<AiReschedulePreview>.Ok(
            new AiReschedulePreview(
                new AiStartPreview(
                    "Confirmed", new DateTimeOffset(2026, 11, 24, 9, 0, 0, Bogota), new DateTimeOffset(2026, 11, 24, 9, 30, 0, Bogota),
                    "America/Bogota", "Consulta general", "Dra. Pérez", "Sede principal"),
                AlreadyDone: true));

        var result = await new SimulatedRescheduleAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(isTest: true),
            Json($$"""{"appointmentId":"{{Appointment}}","startsAt":"2026-11-24T09:00","resourceId":null}"""),
            TestContext.Current.CancellationToken);

        Assert.Contains("\"alreadyDone\":true", result.ResultJson);
    }

    [Fact]
    public async Task SimulatedRescheduleOfAPruebaIdEchoesTheRequestedStart()
    {
        var result = await new SimulatedRescheduleAppointmentTool(_scheduling).ExecuteAsync(
            Invocation(isTest: true),
            Json("""{"appointmentId":"PRUEBA-AB12CD34","startsAt":"2026-11-25T10:00","resourceId":null}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal("""{"appointmentId":"PRUEBA-AB12CD34","status":"Pending","startsAt":"2026-11-25T10:00","alreadyDone":false}""", result.ResultJson);
    }

    [Fact]
    public async Task ListMyAppointmentsCarriesTheDeadlineFlags()
    {
        var result = await new ListMyAppointmentsTool(_scheduling).ExecuteAsync(Invocation(), Json("{}"), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result.ResultJson);
        var first = document.RootElement.GetProperty("appointments")[0];
        Assert.True(first.GetProperty("canChangeByChat").GetBoolean());
        Assert.True(first.GetProperty("canRescheduleByChat").GetBoolean());
        Assert.Equal("2026-11-23T09:00", first.GetProperty("changeByChatUntil").GetString());
    }

    [Fact]
    public async Task ListMyAppointmentsCarriesTheServiceIdForCheckAvailability()
    {
        var result = await new ListMyAppointmentsTool(_scheduling).ExecuteAsync(Invocation(), Json("{}"), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result.ResultJson);
        var first = document.RootElement.GetProperty("appointments")[0];
        Assert.Equal(Service, first.GetProperty("serviceId").GetGuid());
    }

    // §3: Anthropic acepta a lo sumo 20 herramientas estrictas, 24 parámetros opcionales y 16 uniones por request.
    [Fact]
    public void TheAgentStaysWithinAnthropicStrictLimits()
    {
        var definitions = AllTools()
            .Where(tool => tool.IsOfferedTo(new AgentTools(true, true, true, Scheduling: true), isTest: false))
            .Select(tool => tool.Definition)
            .ToList();

        const int finals = 3;
        Assert.Equal(12, definitions.Count + finals);
        Assert.True(definitions.Count + finals <= 20);

        var unions = 0;
        foreach (var definition in definitions)
        {
            Assert.True(definition.Strict, definition.Name);
            using var schema = JsonDocument.Parse(definition.InputSchemaJson);
            var properties = schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
            var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToHashSet();
            Assert.Equal(properties, required);
            unions += properties.Count(name => schema.RootElement.GetProperty("properties").GetProperty(name).GetProperty("type").ValueKind == JsonValueKind.Array);
        }

        // 7 de las herramientas + 1 de handoff_to_human (nota) = 8 de 16.
        Assert.Equal(7, unions);
    }

    [Fact]
    public void LocalTimesAreParsedExactly()
    {
        Assert.True(AgentSchedulingFormat.TryParseLocal("2026-11-24T09:00", out var value));
        Assert.Equal(DateTimeKind.Unspecified, value.Kind);
        Assert.False(AgentSchedulingFormat.TryParseLocal("2026-11-24 09:00", out _));
        Assert.False(AgentSchedulingFormat.TryParseLocal("2026-11-24T09:00Z", out _));
        Assert.True(AgentSchedulingFormat.TryParseDay("2026-11-24", out _));
        Assert.False(AgentSchedulingFormat.TryParseDay("2026-11-24T09:00", out _));
        Assert.Equal("2026-11-24T09:05", AgentSchedulingFormat.Local(new DateTimeOffset(2026, 11, 24, 9, 5, 59, Bogota)));
    }

    private IEnumerable<IAgentTool> AllTools() =>
    [
        new SearchCatalogTool(null!), new GetOrderStatusTool(null!), new CreateQuotationDraftTool(null!, null!), new SimulatedQuotationDraftTool(),
        new ListBookableServicesTool(_scheduling), new CheckAvailabilityTool(_scheduling), new ListMyAppointmentsTool(_scheduling),
        new BookAppointmentTool(_scheduling), new CancelAppointmentTool(_scheduling), new RescheduleAppointmentTool(_scheduling),
        new SimulatedBookAppointmentTool(_scheduling), new SimulatedCancelAppointmentTool(_scheduling), new SimulatedRescheduleAppointmentTool(_scheduling),
    ];

    private static string Book(string startsAt = "2026-11-24T09:00", string? note = null) =>
        JsonSerializer.Serialize(new { serviceId = Service, startsAt, resourceId = (Guid?)null, note });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static AgentToolInvocation Invocation(bool isTest = false, int ordinal = 1) =>
        new(Tenant, JobId, "call_1", Guid.NewGuid(), isTest, AgentId: Guid.NewGuid(), ConversationId: isTest ? null : Guid.NewGuid(), BookingOrdinal: ordinal);

    private static AiSlot Slot(int hour, int minute) =>
        new(new DateTimeOffset(2026, 11, 24, hour, minute, 0, Bogota), new DateTimeOffset(2026, 11, 24, hour, minute, 0, Bogota).AddMinutes(30), 1, []);

    private static AiAppointmentWrite Stored(int hour = 9, bool canChange = true, bool canReschedule = true) =>
        new(
            new AiAppointment(
                Appointment, "Pending", new DateTimeOffset(2026, 11, 24, hour, 0, 0, Bogota), new DateTimeOffset(2026, 11, 24, hour, 30, 0, Bogota),
                "America/Bogota", Service, "Consulta general", "Dra. Pérez", "Sede principal", canChange, canReschedule,
                new DateTimeOffset(2026, 11, 23, 9, 0, 0, Bogota)),
            false);

    private sealed class FakeAiScheduling : IAiScheduling
    {
        public int Calls { get; private set; }

        public int BookCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public int RescheduleCalls { get; private set; }

        public int PreviewCalls { get; private set; }

        public string? LastBookKey { get; private set; }

        public DateTime LastBookStart { get; private set; }

        public string? LastCancelReason { get; private set; }

        public DateTime LastRescheduleStart { get; private set; }

        public Guid LastPreviewService { get; private set; }

        public int RescheduleCheckCalls { get; private set; }

        public (Guid AppointmentId, DateTime Start, Guid? ResourceId)? LastRescheduleCheck { get; private set; }

        public AiAvailability Availability { get; set; } = new("America/Bogota", Service, [Slot(9, 0)], false);

        public AiSchedulingResult<AiAppointmentWrite> BookResult { get; set; } = AiSchedulingResult<AiAppointmentWrite>.Ok(Stored());

        public AiSchedulingResult<AiAppointmentWrite> CancelResult { get; set; } = AiSchedulingResult<AiAppointmentWrite>.Ok(Stored());

        private static readonly AiStartPreview DefaultPreview = new(
            "Pending", new DateTimeOffset(2026, 11, 24, 9, 0, 0, Bogota), new DateTimeOffset(2026, 11, 24, 9, 30, 0, Bogota),
            "America/Bogota", "Consulta general", "Dra. Pérez", "Sede principal");

        public AiSchedulingResult<AiStartPreview> PreviewResult { get; set; } = AiSchedulingResult<AiStartPreview>.Ok(DefaultPreview);

        public AiSchedulingResult<AiReschedulePreview> ReschedulePreviewResult { get; set; } =
            AiSchedulingResult<AiReschedulePreview>.Ok(new AiReschedulePreview(DefaultPreview, false));

        public AiSchedulingResult<AiAppointment> FindResult { get; set; } = AiSchedulingResult<AiAppointment>.Ok(Stored().Appointment);

        public Task<AgentSchedulingReadiness> GetReadinessAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentSchedulingReadiness(true, 1));

        public Task<AiSchedulingResult<AiBookableServices>> ListBookableServicesAsync(AiSchedulingCaller caller, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AiSchedulingResult<AiBookableServices>.Ok(new AiBookableServices("America/Bogota", [new AiBookableService(Service, "Consulta general", 30)], false)));
        }

        public Task<AiSchedulingResult<AiAvailability>> SearchAvailabilityAsync(
            AiSchedulingCaller caller, Guid serviceId, DateOnly fromDate, DateOnly toDate, Guid? resourceId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AiSchedulingResult<AiAvailability>.Ok(Availability));
        }

        public Task<AiSchedulingResult<AiAppointmentWrite>> BookAsync(
            AiSchedulingCaller caller, Guid serviceId, DateTime localStartsAt, Guid? resourceId, string? note, string clientRequestId,
            CancellationToken cancellationToken)
        {
            Calls++;
            BookCalls++;
            (LastBookKey, LastBookStart) = (clientRequestId, localStartsAt);
            return Task.FromResult(BookResult);
        }

        public Task<AiSchedulingResult<AiStartPreview>> PreviewBookingAsync(
            AiSchedulingCaller caller, Guid serviceId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken)
        {
            Calls++;
            PreviewCalls++;
            LastPreviewService = serviceId;
            return Task.FromResult(PreviewResult);
        }

        public Task<AiSchedulingResult<AiReschedulePreview>> PreviewRescheduleAsync(
            AiSchedulingCaller caller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken)
        {
            Calls++;
            RescheduleCheckCalls++;
            LastRescheduleCheck = (appointmentId, localStartsAt, resourceId);
            return Task.FromResult(ReschedulePreviewResult);
        }

        public Task<AiSchedulingResult<AiAppointments>> ListMyAppointmentsAsync(AiSchedulingCaller caller, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AiSchedulingResult<AiAppointments>.Ok(new AiAppointments("America/Bogota", [Stored().Appointment], false)));
        }

        public Task<AiSchedulingResult<AiAppointment>> FindMyAppointmentAsync(
            AiSchedulingCaller caller, Guid appointmentId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(FindResult);
        }

        public Task<AiSchedulingResult<AiAppointmentWrite>> CancelAsync(
            AiSchedulingCaller caller, Guid appointmentId, string? reason, CancellationToken cancellationToken)
        {
            Calls++;
            CancelCalls++;
            LastCancelReason = reason;
            return Task.FromResult(CancelResult);
        }

        public Task<AiSchedulingResult<AiAppointmentWrite>> RescheduleAsync(
            AiSchedulingCaller caller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken)
        {
            Calls++;
            RescheduleCalls++;
            LastRescheduleStart = localStartsAt;
            return Task.FromResult(AiSchedulingResult<AiAppointmentWrite>.Ok(Stored()));
        }
    }
}
