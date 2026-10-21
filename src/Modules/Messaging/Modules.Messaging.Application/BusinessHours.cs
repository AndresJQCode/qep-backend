using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

// Spec 2026-10-21 §5: el catálogo de horarios de atención. Leer pide messaging.conversation.read; todo lo demás, messaging.business_hours.manage.

/// <summary>Una franja en minutos locales desde las 00:00: <c>[From, To)</c>.</summary>
public sealed record TimeRangeDto(int From, int To);

/// <summary>Siempre 7 (lunes a domingo): un día ausente obligaría a la pantalla a conocer el calendario del backend.</summary>
public sealed record WeekdayHoursDto(int Day, IReadOnlyList<TimeRangeDto> Ranges);

public sealed record ExceptionHoursDto(DateOnly Date, IReadOnlyList<TimeRangeDto> Ranges);

public sealed record BusinessHoursConnectionDto(Guid ConnectionId, string Name, string Channel);

/// <summary>UpcomingHolidays viaja calculada para que el frontend no tenga la Ley 51 (D-H9). OpenNow y NextChangeAt se evalúan con el reloj y el huso del tenant.</summary>
public sealed record BusinessHoursDto(
    Guid Id,
    string Name,
    bool IsDefault,
    IReadOnlyList<WeekdayHoursDto> Weekly,
    IReadOnlyList<ExceptionHoursDto> Exceptions,
    string? HolidayCountry,
    IReadOnlyList<DateOnly> UpcomingHolidays,
    IReadOnlyList<BusinessHoursConnectionDto> Connections,
    bool OpenNow,
    DateTimeOffset? NextChangeAt,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary>Lleva las conexiones del tenant en cualquier estado con su horario actual: GET /messaging/connections sólo trae las activas y el editor tiene que mostrar también una pausada para no desasignarla en silencio (D-H10).</summary>
public sealed record BusinessHoursConnectionOptionDto(Guid ConnectionId, string Name, string Channel, string Status, Guid? ScheduleId, string? ScheduleName);

public sealed record BusinessHoursListDto(IReadOnlyList<BusinessHoursDto> Items, string TimeZone, IReadOnlyList<BusinessHoursConnectionOptionDto> Connections);

/// <summary>Opcional campo a campo para que un cuerpo incompleto salga 422 con el mapa <c>errors</c>, no 400 del binder.</summary>
public sealed record SaveBusinessHoursRequest(
    string? Name,
    IReadOnlyList<WeekdayHoursDto>? Weekly,
    IReadOnlyList<ExceptionHoursDto>? Exceptions,
    string? HolidayCountry,
    IReadOnlyList<Guid>? ConnectionIds);

public sealed record ListBusinessHoursQuery(Guid TenantId) : IQuery<BusinessHoursListDto>;

public sealed record CreateBusinessHoursCommand(Guid TenantId, SaveBusinessHoursRequest Request) : ICommand<BusinessHoursDto>;

public sealed record UpdateBusinessHoursCommand(Guid TenantId, Guid ScheduleId, long ExpectedVersion, SaveBusinessHoursRequest Request) : ICommand<BusinessHoursDto>;

public sealed record DeleteBusinessHoursCommand(Guid TenantId, Guid ScheduleId, long ExpectedVersion) : ICommand<bool>;

public sealed record DuplicateBusinessHoursCommand(Guid TenantId, Guid ScheduleId) : ICommand<BusinessHoursDto>;

public sealed record SetDefaultBusinessHoursCommand(Guid TenantId, Guid ScheduleId, long ExpectedVersion) : ICommand<BusinessHoursDto>;

/// <summary>Los textos de <c>errors</c>, tuteando.</summary>
public static class BusinessHoursMessages
{
    public const string Name = "Escribe un nombre de hasta 60 caracteres.";

    public const string Week = "La semana trae los siete días, de lunes a domingo.";

    public const string Ranges = "Cada día trae su lista de franjas, vacía si ese día no se atiende.";

    public const string TooManyRanges = "Un día admite hasta 3 franjas.";

    public const string RangeOrder = "Las franjas van ordenadas y no se pueden solapar.";

    public const string Midnight = "Una franja no puede pasar de medianoche: divídela en dos días.";

    public const string RangeBounds = "Las franjas van entre las 00:00 y las 24:00.";

    public const string Exceptions = "Las excepciones traen una sola entrada por fecha y hasta 100.";

    public const string HolidayCountry = "Sólo están disponibles los festivos de Colombia.";

    public const string ConnectionIds = "Cada conexión se elige una sola vez.";

    public const string UnknownConnection = "Una de las conexiones no es de este negocio.";
}

/// <summary>Texto libre y listas: el validador da el campo (<c>errors.Weekly[0].Ranges[0].To</c>) que el dominio (<c>ranges_invalid</c>) no puede dar.
/// Los nombres de propiedad van tal cual (<c>Name</c>, <c>Weekly</c>…) para que el mapa <c>errors</c> case con el formulario.</summary>
public sealed class SaveBusinessHoursRequestValidator : AbstractValidator<SaveBusinessHoursRequest>
{
    public SaveBusinessHoursRequestValidator()
    {
        RuleFor(request => request.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= BusinessHoursSchedule.NameMaxLength)
            .WithMessage(BusinessHoursMessages.Name);

        RuleFor(request => request.Weekly).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage(BusinessHoursMessages.Week)
            .Must(weekly => HasTheSevenDays(weekly!)).WithMessage(BusinessHoursMessages.Week);
        RuleForEach(request => request.Weekly!).NotNull().WithMessage(BusinessHoursMessages.Week)
            .SetValidator(new WeekdayHoursValidator());

        RuleFor(request => request.Exceptions).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage(BusinessHoursMessages.Exceptions)
            .Must(exceptions => HasUniqueDatesWithinTheCap(exceptions!)).WithMessage(BusinessHoursMessages.Exceptions);
        RuleForEach(request => request.Exceptions!).NotNull().WithMessage(BusinessHoursMessages.Exceptions)
            .SetValidator(new ExceptionHoursValidator());

        RuleFor(request => request.HolidayCountry)
            .Must(country => country is null || country == nameof(HolidayCountry.CO))
            .WithMessage(BusinessHoursMessages.HolidayCountry);

        RuleFor(request => request.ConnectionIds).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage(BusinessHoursMessages.ConnectionIds)
            .Must(ids => ids!.Distinct().Count() == ids!.Count).WithMessage(BusinessHoursMessages.ConnectionIds);
    }

    private static bool HasUniqueDatesWithinTheCap(IReadOnlyList<ExceptionHoursDto> exceptions)
    {
        var dates = exceptions.OfType<ExceptionHoursDto>().Select(exception => exception.Date).ToArray();
        return exceptions.Count <= BusinessHoursSchedule.MaxExceptions && dates.Distinct().Count() == dates.Length;
    }

    private static bool HasTheSevenDays(IReadOnlyList<WeekdayHoursDto> weekly)
    {
        var days = weekly.OfType<WeekdayHoursDto>().Select(day => day.Day).ToArray();
        return weekly.Count == 7 && days.Length == 7 && days.Distinct().Count() == 7 && days.All(day => day is >= 1 and <= 7);
    }
}

internal static class BusinessHoursRules
{
    /// <summary>Las franjas de un día o de una excepción: existen, hasta 3, ordenadas y sin solapes.</summary>
    internal static IRuleBuilderOptions<T, IReadOnlyList<TimeRangeDto>> ValidRanges<T>(this IRuleBuilderInitial<T, IReadOnlyList<TimeRangeDto>> builder) =>
        builder.Cascade(CascadeMode.Stop)
            .NotNull().WithMessage(BusinessHoursMessages.Ranges)
            .Must(ranges => ranges!.Count <= BusinessHoursSchedule.MaxRangesPerDay).WithMessage(BusinessHoursMessages.TooManyRanges)
            .Must(AreOrdered).WithMessage(BusinessHoursMessages.RangeOrder);

    internal static bool AreOrdered(IReadOnlyList<TimeRangeDto> ranges)
    {
        var present = ranges.OfType<TimeRangeDto>().ToArray();
        for (var i = 1; i < present.Length; i++)
        {
            if (present[i - 1].To > present[i].From)
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class WeekdayHoursValidator : AbstractValidator<WeekdayHoursDto>
{
    public WeekdayHoursValidator()
    {
        RuleFor(day => day.Ranges).ValidRanges();
        RuleForEach(day => day.Ranges).NotNull().WithMessage(BusinessHoursMessages.Ranges).SetValidator(new TimeRangeValidator());
    }
}

internal sealed class ExceptionHoursValidator : AbstractValidator<ExceptionHoursDto>
{
    public ExceptionHoursValidator()
    {
        RuleFor(exception => exception.Ranges).ValidRanges();
        RuleForEach(exception => exception.Ranges).NotNull().WithMessage(BusinessHoursMessages.Ranges).SetValidator(new TimeRangeValidator());
    }
}

internal sealed class TimeRangeValidator : AbstractValidator<TimeRangeDto>
{
    public TimeRangeValidator()
    {
        RuleFor(range => range.From).InclusiveBetween(0, BusinessHoursSchedule.MinutesPerDay - 1).WithMessage(BusinessHoursMessages.RangeBounds);
        RuleFor(range => range.To).InclusiveBetween(1, BusinessHoursSchedule.MinutesPerDay).WithMessage(BusinessHoursMessages.RangeBounds);
        // Sólo cuando los dos están en rango: si no, el mensaje de medianoche sobra y confunde.
        RuleFor(range => range.To).GreaterThan(range => range.From)
            .When(range => range.From is >= 0 and < BusinessHoursSchedule.MinutesPerDay && range.To is >= 1 and <= BusinessHoursSchedule.MinutesPerDay)
            .WithMessage(BusinessHoursMessages.Midnight);
    }
}

internal static class BusinessHoursSupport
{
    public static ResourceNotFoundException NotFound(Guid scheduleId) =>
        new(MessagingErrorCodes.BusinessHoursNotFound, $"Business hours schedule '{scheduleId}' was not found.");

    /// <summary>Equivalente de <c>ConversationConcurrency.EnsureVersion</c> para el horario: la versión del If-Match tiene que ser la vigente.</summary>
    public static void EnsureVersion(BusinessHoursSchedule schedule, long expectedVersion)
    {
        if (schedule.Version != expectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The business hours schedule changed since it was loaded.");
        }
    }

    public static IReadOnlyList<WeekdayHours> ToWeekly(SaveBusinessHoursRequest request) =>
        request.Weekly!.Select(day => new WeekdayHours(day.Day, ToRanges(day.Ranges))).ToArray();

    public static IReadOnlyList<DateException> ToExceptions(SaveBusinessHoursRequest request) =>
        request.Exceptions!.Select(exception => new DateException(exception.Date, ToRanges(exception.Ranges))).ToArray();

    public static HolidayCountry? ToHolidayCountry(SaveBusinessHoursRequest request) =>
        request.HolidayCountry is null ? null : Enum.Parse<HolidayCountry>(request.HolidayCountry);

    public static BusinessHoursDto ToDto(BusinessHoursSchedule schedule, IReadOnlyList<MessagingConnectionRef> connections, TenantCalendar calendar) => new(
        schedule.Id,
        schedule.Name,
        schedule.IsDefault,
        schedule.Weekly.Select(day => new WeekdayHoursDto(day.Day, ToRangeDtos(day.Ranges))).ToArray(),
        schedule.Exceptions.Select(exception => new ExceptionHoursDto(exception.Date, ToRangeDtos(exception.Ranges))).ToArray(),
        schedule.HolidayCountry?.ToString(),
        schedule.UpcomingHolidays(calendar.Today),
        connections.Select(connection => new BusinessHoursConnectionDto(connection.Id, connection.Name, connection.Channel.ToString())).ToArray(),
        schedule.IsOpenAt(calendar.UtcNow, calendar.TimeZone),
        schedule.NextChangeAt(calendar.UtcNow, calendar.TimeZone),
        schedule.UpdatedAt,
        schedule.Version);

    /// <summary>Las conexiones vivas (las huérfanas no se muestran) asignadas a un horario, en el orden del directorio.</summary>
    public static IReadOnlyList<MessagingConnectionRef> LiveConnectionsOf(
        Guid scheduleId, IReadOnlyList<ConnectionBusinessHours> assignments, IReadOnlyList<MessagingConnectionRef> live) =>
        live.Where(connection => assignments.Any(row => row.ConnectionId == connection.Id && row.ScheduleId == scheduleId)).ToArray();

    private static TimeRange[] ToRanges(IReadOnlyList<TimeRangeDto> ranges) => ranges.Select(range => new TimeRange(range.From, range.To)).ToArray();

    private static TimeRangeDto[] ToRangeDtos(IReadOnlyList<TimeRange> ranges) => ranges.Select(range => new TimeRangeDto(range.From, range.To)).ToArray();

    /// <summary>D-H10: <c>ConnectionIds</c> reemplaza por completo las asignaciones del horario. Una conexión que no es del tenant es 422 en
    /// <c>ConnectionIds</c>; una pausada o en atención se acepta. Las filas del horario cuya conexión ya no existe (borrada en Integrations) se
    /// limpian en la misma transacción. Devuelve las conexiones asignadas y si cambió alguna asignación viva (lo que se audita).</summary>
    public static async Task<(IReadOnlyList<MessagingConnectionRef> Assigned, bool Changed)> ApplyConnectionsAsync(
        IBusinessHoursRepository repository,
        IMessagingConnectionDirectory directory,
        Guid tenantId,
        BusinessHoursSchedule schedule,
        IReadOnlyList<Guid> connectionIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var live = await directory.ListAllAsync(tenantId, cancellationToken);
        var liveIds = live.Select(connection => connection.Id).ToHashSet();
        if (connectionIds.Any(id => !liveIds.Contains(id)))
        {
            throw new ValidationException([new ValidationFailure(nameof(SaveBusinessHoursRequest.ConnectionIds), BusinessHoursMessages.UnknownConnection)]);
        }

        var wanted = connectionIds.ToHashSet();
        var assignments = await repository.ListAssignmentsAsync(tenantId, cancellationToken);
        var changed = false;
        foreach (var row in assignments.Where(row => row.ScheduleId == schedule.Id))
        {
            if (!liveIds.Contains(row.ConnectionId))
            {
                repository.RemoveAssignment(row);
            }
            else if (!wanted.Contains(row.ConnectionId))
            {
                repository.RemoveAssignment(row);
                changed = true;
            }
        }

        foreach (var connectionId in connectionIds)
        {
            var existing = assignments.FirstOrDefault(row => row.ConnectionId == connectionId);
            if (existing is null)
            {
                repository.AddAssignment(ConnectionBusinessHours.Create(connectionId, tenantId, schedule.Id, now));
                changed = true;
            }
            else if (existing.ScheduleId != schedule.Id)
            {
                existing.MoveTo(schedule.Id, now);
                changed = true;
            }
        }

        return (live.Where(connection => wanted.Contains(connection.Id)).ToArray(), changed);
    }
}

/// <summary><c>GET /business-hours</c>: completa, sin paginar. Las asignaciones huérfanas no se muestran; las opciones traen todas las conexiones vivas.</summary>
public sealed class ListBusinessHoursHandler(
    IBusinessHoursRepository repository,
    IMessagingConnectionDirectory connections,
    ITenantClock tenantClock,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<ListBusinessHoursQuery, BusinessHoursListDto>
{
    public async Task<BusinessHoursListDto> HandleAsync(ListBusinessHoursQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);
        var schedules = await repository.ListAsync(query.TenantId, cancellationToken);
        var assignments = await repository.ListAssignmentsAsync(query.TenantId, cancellationToken);
        var live = await connections.ListAllAsync(query.TenantId, cancellationToken);
        var names = schedules.ToDictionary(schedule => schedule.Id, schedule => schedule.Name);

        return new BusinessHoursListDto(
            schedules.Select(schedule => BusinessHoursSupport.ToDto(schedule, BusinessHoursSupport.LiveConnectionsOf(schedule.Id, assignments, live), calendar)).ToArray(),
            calendar.TimeZone.Id,
            live.Select(connection =>
            {
                var row = assignments.FirstOrDefault(assignment => assignment.ConnectionId == connection.Id);
                var scheduleName = row is not null && names.TryGetValue(row.ScheduleId, out var name) ? name : null;
                return new BusinessHoursConnectionOptionDto(
                    connection.Id, connection.Name, connection.Channel.ToString(), connection.Status, scheduleName is null ? null : row!.ScheduleId, scheduleName);
            }).ToArray());
    }
}

public sealed class CreateBusinessHoursHandler(
    IBusinessHoursRepository repository,
    IMessagingConnectionDirectory connections,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantClock tenantClock,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<SaveBusinessHoursRequest> validator)
    : ICommandHandler<CreateBusinessHoursCommand, BusinessHoursDto>
{
    public async Task<BusinessHoursDto> HandleAsync(CreateBusinessHoursCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.BusinessHoursManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var request = command.Request;

        var schedule = BusinessHoursSchedule.Create(
            Guid.CreateVersion7(),
            command.TenantId,
            request.Name,
            BusinessHoursSupport.ToWeekly(request),
            BusinessHoursSupport.ToExceptions(request),
            BusinessHoursSupport.ToHolidayCountry(request),
            calendar.Today,
            calendar.UtcNow);
        repository.Add(schedule);
        var (assigned, changed) = await BusinessHoursSupport.ApplyConnectionsAsync(
            repository, connections, command.TenantId, schedule, request.ConnectionIds!, calendar.UtcNow, cancellationToken);

        audit.RecordBusinessHours(command.TenantId, executionContext.SubjectId, MessagingAuditActions.BusinessHoursCreated, schedule.Id, calendar.UtcNow);
        if (changed)
        {
            audit.RecordBusinessHours(command.TenantId, executionContext.SubjectId, MessagingAuditActions.BusinessHoursConnectionsAssigned, schedule.Id, calendar.UtcNow);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return BusinessHoursSupport.ToDto(schedule, assigned, calendar);
    }
}

/// <summary><c>PUT /business-hours/{id}</c> con <c>If-Match</c>: reemplaza el horario y sus conexiones. Ajeno o inexistente es 404; versión vieja, 412.</summary>
public sealed class UpdateBusinessHoursHandler(
    IBusinessHoursRepository repository,
    IMessagingConnectionDirectory connections,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantClock tenantClock,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IValidator<SaveBusinessHoursRequest> validator)
    : ICommandHandler<UpdateBusinessHoursCommand, BusinessHoursDto>
{
    public async Task<BusinessHoursDto> HandleAsync(UpdateBusinessHoursCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.BusinessHoursManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);
        var schedule = await repository.FindAsync(command.TenantId, command.ScheduleId, cancellationToken)
            ?? throw BusinessHoursSupport.NotFound(command.ScheduleId);
        BusinessHoursSupport.EnsureVersion(schedule, command.ExpectedVersion);
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var request = command.Request;

        schedule.Update(
            request.Name,
            BusinessHoursSupport.ToWeekly(request),
            BusinessHoursSupport.ToExceptions(request),
            BusinessHoursSupport.ToHolidayCountry(request),
            calendar.Today,
            calendar.UtcNow);
        var (assigned, changed) = await BusinessHoursSupport.ApplyConnectionsAsync(
            repository, connections, command.TenantId, schedule, request.ConnectionIds!, calendar.UtcNow, cancellationToken);

        audit.RecordBusinessHours(command.TenantId, executionContext.SubjectId, MessagingAuditActions.BusinessHoursUpdated, schedule.Id, calendar.UtcNow);
        if (changed)
        {
            audit.RecordBusinessHours(command.TenantId, executionContext.SubjectId, MessagingAuditActions.BusinessHoursConnectionsAssigned, schedule.Id, calendar.UtcNow);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return BusinessHoursSupport.ToDto(schedule, assigned, calendar);
    }
}

/// <summary><c>DELETE /business-hours/{id}</c>: el predeterminado y el que tiene conexiones vivas se rechazan (422); las asignaciones de conexiones
/// ya borradas en Integrations no cuentan y se limpian en la misma transacción.</summary>
public sealed class DeleteBusinessHoursHandler(
    IBusinessHoursRepository repository,
    IMessagingConnectionDirectory connections,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantClock tenantClock,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : ICommandHandler<DeleteBusinessHoursCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteBusinessHoursCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.BusinessHoursManage, cancellationToken);
        var schedule = await repository.FindAsync(command.TenantId, command.ScheduleId, cancellationToken)
            ?? throw BusinessHoursSupport.NotFound(command.ScheduleId);
        BusinessHoursSupport.EnsureVersion(schedule, command.ExpectedVersion);
        if (schedule.IsDefault)
        {
            throw new MessagingDomainException(MessagingErrorCodes.BusinessHoursIsDefault, "The default business hours schedule cannot be deleted: choose another default first.");
        }

        var live = (await connections.ListAllAsync(command.TenantId, cancellationToken)).Select(connection => connection.Id).ToHashSet();
        var own = (await repository.ListAssignmentsAsync(command.TenantId, cancellationToken)).Where(row => row.ScheduleId == schedule.Id).ToArray();
        if (own.Any(row => live.Contains(row.ConnectionId)))
        {
            throw new MessagingDomainException(MessagingErrorCodes.BusinessHoursInUse, "The business hours schedule is assigned to connections: unassign them first.");
        }

        foreach (var orphan in own)
        {
            repository.RemoveAssignment(orphan);
        }

        repository.Remove(schedule);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        audit.RecordBusinessHours(command.TenantId, executionContext.SubjectId, MessagingAuditActions.BusinessHoursDeleted, schedule.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary><c>POST /business-hours/{id}/duplicate</c>: sin <c>If-Match</c> (no cambia el origen). Elige el primer nombre «(copia n)» libre; si otra
/// petición lo toma entre la comprobación y el guardado, el índice único responde 409.</summary>
public sealed class DuplicateBusinessHoursHandler(
    IBusinessHoursRepository repository,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantClock tenantClock,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : ICommandHandler<DuplicateBusinessHoursCommand, BusinessHoursDto>
{
    public async Task<BusinessHoursDto> HandleAsync(DuplicateBusinessHoursCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.BusinessHoursManage, cancellationToken);
        var source = await repository.FindAsync(command.TenantId, command.ScheduleId, cancellationToken)
            ?? throw BusinessHoursSupport.NotFound(command.ScheduleId);
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);

        var attempt = 1;
        var name = BusinessHoursSchedule.DuplicateName(source.Name, attempt);
        while (await repository.NameExistsAsync(command.TenantId, name, cancellationToken))
        {
            name = BusinessHoursSchedule.DuplicateName(source.Name, ++attempt);
        }

        var copy = source.Duplicate(Guid.CreateVersion7(), name, calendar.UtcNow);
        repository.Add(copy);
        audit.RecordBusinessHours(command.TenantId, executionContext.SubjectId, MessagingAuditActions.BusinessHoursDuplicated, copy.Id, calendar.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return BusinessHoursSupport.ToDto(copy, [], calendar);
    }
}

/// <summary><c>POST /business-hours/{id}/default</c> con <c>If-Match</c>: quita el predeterminado anterior y lo pone en este. Si ya lo es, 200 sin
/// cambios. Dos peticiones a la vez las arbitra el índice único parcial (409 <c>default_conflict</c>).</summary>
public sealed class SetDefaultBusinessHoursHandler(
    IBusinessHoursRepository repository,
    IMessagingConnectionDirectory connections,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantClock tenantClock,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : ICommandHandler<SetDefaultBusinessHoursCommand, BusinessHoursDto>
{
    public async Task<BusinessHoursDto> HandleAsync(SetDefaultBusinessHoursCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.BusinessHoursManage, cancellationToken);
        // El anterior se carga primero: EF escribe los UPDATE en el orden en que rastrea las entidades, y el índice único parcial exige que
        // el viejo deje de ser predeterminado antes de que el nuevo lo sea.
        var previous = await repository.FindDefaultAsync(command.TenantId, cancellationToken);
        var schedule = await repository.FindAsync(command.TenantId, command.ScheduleId, cancellationToken)
            ?? throw BusinessHoursSupport.NotFound(command.ScheduleId);
        BusinessHoursSupport.EnsureVersion(schedule, command.ExpectedVersion);
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);

        if (!schedule.IsDefault)
        {
            previous?.ClearDefault(calendar.UtcNow);
            schedule.MakeDefault(calendar.UtcNow);
            audit.RecordBusinessHours(command.TenantId, executionContext.SubjectId, MessagingAuditActions.BusinessHoursDefaultSet, schedule.Id, calendar.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var live = await connections.ListAllAsync(command.TenantId, cancellationToken);
        var assignments = await repository.ListAssignmentsAsync(command.TenantId, cancellationToken);
        return BusinessHoursSupport.ToDto(schedule, BusinessHoursSupport.LiveConnectionsOf(schedule.Id, assignments, live), calendar);
    }
}
