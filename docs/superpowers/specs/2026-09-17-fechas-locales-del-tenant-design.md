# Fechas en el huso del tenant (`ITenantClock`) — diseño

**Fecha:** 2026-09-17 (diseño acordado con el owner el mismo día)
**Rama:** por crear desde `develop` (backend). No cambia la forma de la API: `qep-frontend` no se toca.
**Datos existentes:** no hay backfill. El proyecto está en desarrollo y el owner lo descartó
explícitamente: lo ya guardado con fechas cortadas en UTC queda como está.

## Contexto

Todos los instantes se guardan en UTC (`IClock.UtcNow`, columnas `timestamptz`), y eso está bien.
El problema aparece cuando un instante se **corta en días**: el código toma el día, el mes o el año
del instante UTC, pero para el negocio el día es el del tenant. Los tenants operan en
`America/Bogota` (UTC-5), así que **todos los días desde las 19:00 hora local** el backend ya vive en
el día siguiente. El 31 de diciembre, además, vive en el año siguiente.

`Tenant.TimeZone` existe, es obligatorio y viene validado como ID IANA (`Tenant.cs:185`). También
existe `ITenantDirectory.GetTimeZoneAsync` (`ITenantDirectory.cs:17`), implementado desde el commit
inicial. Nadie en la lógica de negocio usa ninguno de los dos.

Esta auditoría se hizo sobre `src/` el 2026-09-17. Instante de ejemplo: **2026-12-31 23:00 en
Bogotá = 2027-01-01 04:00Z**.

| # | Dónde | Qué pasa hoy | Severidad |
| --- | --- | --- | --- |
| 1 | `QuotationExpirationProcessor.cs:33-37` | `today` sale de UTC: una cotización `Sent` que vence **hoy** pasa a `Expired` desde las 19:00 locales, con historial y auditoría | Alta |
| 2a | `CreateQuotation.cs:63-65` | Año del consecutivo tomado de `UtcNow.Year`: sale `QUO-2027-0001` el 31-dic y arranca el contador de 2027 antes de tiempo | Alta |
| 2b | `ConvertQuotationToOrder.cs:92-94` | Lo mismo con `PED-` | Alta |
| 2c | `CreateQuotation.cs:108-109` (`DefaultValidUntil`) | La vigencia por defecto vence un día más tarde si se crea después de las 19:00 | Alta |
| 3 | `QuotationRepository.cs:172,180-181`, `OrderRepository.cs:203,211-212` | Los filtros `DateOnly` from/to cortan el día en UTC. "Hasta el 31" deja fuera lo creado después de las 19:00 del 31. Afecta el listado, el conteo y el export | Media |
| 4 | `ReportingLookups.cs:148-152` (`ReportDateRange`) | Lo mismo en los rangos de los 4 reportes y en su comparación con el período anterior. El comentario deja la alineación al huso como "decisión de producto" pendiente | Media |
| 5 | `QuotationsReportSource.cs:93`, `OrdersReportSource.cs:107`, `CustomerReportSource.cs:89`, `PriceChangeReportSource.cs:105` | La agrupación mensual usa `UtcDateTime.Year/Month`: lo de las 20:00 del último día cae en el mes siguiente | Media |
| 6 | `QuotationsReportSummary.cs:142` | "Vencidas", "por vencer" y `DaysLeft` se calculan con el hoy en UTC | Media |
| 7 | `quotation.typ:49-52,175` | La fecha "Emitida" del PDF corta el ISO UTC en la `T`: el cliente final lee "1 de enero de 2027" | Baja (visible al cliente) |
| 8a | `QuotationsExportProcessor.cs:108`, `OrdersExportProcessor.cs:142`, `ClosedXmlCustomerExportBuilder.cs:66,89-90`, `ExportJobSupport.cs:35-36`, `ExportProducts.cs:167-168` | Celdas en `...+00:00` y nombres de archivo con hora UTC | Baja |
| 8b | `CustomerExportEmailTemplate.cs:27`, `ProductExportEmailTemplate.cs:27`, `QuotationsExportReadyEmailTemplate.cs:25` | El vencimiento del enlace se muestra como "04:00 UTC" | Baja |
| 8c | `ExportLoadSeeder.cs:72,310` | El seeder marca `Expired` con el hoy en UTC y no coincide con el punto 1 una vez corregido | Baja |

**Revisado y correcto en UTC** (no se toca): las ventanas de retención y purga (`PurgeRequestFailures`,
`StagingCleanupProcessor`, `PaymentProofOrphanCleanupProcessor`, `ExportJob.Retention`,
`OrphanUserCleanupWorker`), la vida de sesiones e invitaciones, el vencimiento de URLs firmadas y de
exports, los prefijos de storage `yyyy/MM` (particiones internas), la matemática pura sobre
`DateOnly` (`ReportComparisonWindow`, validadores de rango) y los timestamps de todos los DTOs.

## Decisiones

1. **El día de negocio es el día local del tenant**, siempre. Esto cierra la decisión de producto
   que `ReportDateRange` dejaba abierta.
2. **Se sigue guardando en UTC.** `IClock` no cambia: lo que cambia es cómo se **corta** un
   instante en días y cómo se **muestra**.
3. **No hay default a UTC.** Si no se puede resolver el huso del tenant, se falla; un default
   silencioso es justo el defecto que se está quitando.
4. **Sin backfill** (ver arriba).

## Diseño

### `ITenantClock` y `TenantCalendar`

Viven en `Modules.Tenancy.Application`, junto a `IExecutionContext` y `ITenantDirectory`. Todos los
módulos afectados ya referencian esa capa: Quotations, Reporting, Catalog y Customers desde
Application, y Notifications desde Infrastructure. No se abre ninguna referencia nueva.

```csharp
public interface ITenantClock
{
    Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class TenantCalendar
{
    public DateTimeOffset UtcNow { get; }
    public TimeZoneInfo TimeZone { get; }
    public DateOnly Today { get; }                                   // fecha local de UtcNow
    public DateTimeOffset ToLocal(DateTimeOffset instant);           // para mostrar
    public DateTimeOffset StartOfDayUtc(DateOnly date);              // 00:00 local → instante
    public DateTimeOffset EndOfDayExclusiveUtc(DateOnly date);       // 00:00 local del día siguiente
}
```

- `TenantCalendar` es **puro e inmutable**: se construye con `(DateTimeOffset utcNow, TimeZoneInfo)`
  y se prueba sin base ni DI.
- `StartOfDayUtc` usa `TimeZoneInfo.ConvertTimeToUtc`. Si las 00:00 locales caen en un hueco de
  horario de verano, toma el primer instante válido del día. Colombia no tiene horario de verano,
  pero el tipo no lo supone.
- La implementación (`TenantClock`, en `Tenancy.Infrastructure`) combina `IClock.UtcNow`,
  `ITenantDirectory.GetTimeZoneAsync` y `TimeZoneInfo.FindSystemTimeZoneById`. Si el tenant no
  existe, lanza `ResourceNotFoundException` (`tenancy.tenant.not_found`). Se registra scoped.
- **Una resolución por request.** Los procesos que recorren varios tenants piden un calendario por
  tenant, no uno por fila.

### Aplicación por punto

| # | Cambio |
| --- | --- |
| 1 | `QuotationExpirationProcessor`: primero consulta amplia `Sent` con `ValidUntil < hoyUTC + 2` (ningún huso adelanta más de un día a UTC). Después agrupa por tenant, resuelve el calendario y filtra `ValidUntil < calendar.Today` en memoria. |
| 2a/2b | El año del consecutivo es `calendar.Today.Year`. `IQuotationNumberGenerator` / `IOrderNumberGenerator` no cambian de firma. |
| 2c | `DefaultValidUntil` pasa a ser `calendar.Today.AddDays(DefaultValidityDays)`. |
| 3 | El handler convierte los `DateOnly` from/to con `StartOfDayUtc` / `EndOfDayExclusiveUtc` y **el repositorio recibe instantes**. Infrastructure no decide husos. Aplica a listado, conteo y export de cotizaciones y pedidos. |
| 4 | `ReportDateRange` se elimina. Las 4 fuentes de reporte reciben los instantes calculados con el calendario del tenant, y el comentario de la decisión pendiente se va con la clase. |
| 5 | La agrupación mensual pasa a hacerse en el huso del tenant con `AT TIME ZONE` en SQL (`EF.Functions.AtTimeZone` con el ID IANA). **Riesgo a verificar primero:** que Npgsql EF 10.0.3 lo traduzca sobre una propiedad `DateTimeOffset`. Si no lo traduce, se proyectan los instantes del período y se agrupa en memoria con `calendar.ToLocal`: el volumen es de un solo tenant y un período acotado. |
| 6 | `QuotationsReportSummary` toma `today` de `calendar.Today`. |
| 7 | `QuotationPdfDocumentMapper` manda la fecha de emisión como fecha local ya calculada (`yyyy-MM-dd`), y `quotation.typ` deja de cortar un ISO con hora. |
| 8a | Las celdas de fecha y hora de los 4 exports se escriben en hora local (`yyyy-MM-dd HH:mm`, sin offset), y los nombres de archivo usan hora local. |
| 8b | Las 3 plantillas de correo muestran `dd/MM/yyyy HH:mm` local, sin la etiqueta `UTC`. El que arma el correo resuelve el calendario del tenant del export. |
| 8c | `ExportLoadSeeder` usa el mismo `today` local que el punto 1. |

## Pruebas (TDD: RED con evidencia literal antes de GREEN)

- **`TenantCalendar`, unitarias:**
  - 2027-01-01T04:00Z en `America/Bogota` → `Today = 2026-12-31`.
  - 2026-09-17T00:30Z → `2026-09-16`, que es la frontera diaria de las 19:30 locales.
  - `StartOfDayUtc(2026-12-31)` = `2026-12-31T05:00Z`.
  - `EndOfDayExclusiveUtc(2026-12-31)` = `2027-01-01T05:00Z`.
  - Un huso con horario de verano en el día del cambio.
- **`TenantClock`:** tenant inexistente → `ResourceNotFoundException`.
- **Cada punto** lleva una prueba que hoy falla **en la frontera**, con el reloj fijo en
  `2027-01-01T04:00Z` y el tenant en `America/Bogota`:
  - 1: una cotización que vence el 2026-12-31 **no** expira.
  - 2: sale `QUO-2026-…` / `PED-2026-…`, y la vigencia por defecto es 2027-01-15.
  - 3: un filtro "hasta 2026-12-31" incluye lo creado a las 23:00 locales.
  - 4 y 5: el reporte de diciembre incluye ese pedido.
  - 6: no cuenta como vencida.
  - 7: el PDF dice 31 de diciembre de 2026.
  - 8: celdas, nombres y correos muestran la hora local.
- **Pruebas existentes que fijan el comportamiento viejo y se reescriben:**
  - `QuotationApiTests.cs:30`, `OrderApiTests.cs:89`, `OrderListApiTests.cs:38` arman el número con
    `DateTime.UtcNow.Year`. Pasan a reloj fijo, porque tomar el año de UTC además las vuelve
    intermitentes cada fin de año.
  - `QuotationsReportSummaryHandlerTests.cs:95-107` (`SummarizingResolvesTodayFromTheClockInUtc`) se
    renombra y se mueve a la frontera.
  - `CustomerReportSummaryApiTests.cs:53-54` usa `DateTime.UtcNow.Year/Month`.
  - Las pruebas de plantillas y exports con "UTC" o `+00:00`:
    `CustomerExportEmailTemplateTests.cs:36`, `QuotationsExportEmailTemplateTests.cs:30`,
    `QuotationsExportProcessorTests.cs:35,107,111`, `OrdersExportProcessorTests.cs:248`.
  - `ExportLoadSeedTests.cs:72-73,91-92,185,213`.
- **Arquitectura:** `TenantClock` en `Tenancy.Infrastructure` y los consumidores sin referencias
  nuevas. `ArchitectureTests` en verde sin cambios de regla.

## Fuera de alcance

- **Formato de numeración configurable por tenant** (prefijo, año opcional, ancho, siguiente
  número). Es el spec siguiente y depende de este.
- **Timestamps de la API.** Siguen viajando como `DateTimeOffset` UTC; mostrarlos en hora local es
  trabajo del frontend.
- **Seeders que usan `DateTimeOffset.UtcNow` directo** (`TenancySeeder`, `IdentitySeeder`,
  `TenancyDatabaseInitializer`). Sólo lo usan para instantes de auditoría, así que están bien.
