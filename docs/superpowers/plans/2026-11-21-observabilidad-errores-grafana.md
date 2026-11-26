# Errores y APIs externas en Grafana — plan de implementación

> **Para agentes:** SUB-SKILL REQUERIDA: usar superpowers:subagent-driven-development (recomendado) o superpowers:executing-plans para implementar este plan tarea por tarea. Los pasos usan casillas (`- [ ]`).

**Meta:** Que Prometheus cuente todo log `Error`/`Critical` de QEP y cada falla de una llamada a Siigo, sin tenant como etiqueta.

**Arquitectura:** Un `ILoggerProvider` que solo cuenta (en `BuildingBlocks.Observability`) y un `SiigoMetrics` (en `Modules.Siigo.Infrastructure`) que `SiigoApiClient.RecordAsync` alimenta. Las series de alerta son `ObservableCounter` sembrados en cero: `increase()` toma la primera muestra como base y una serie que nace al ocurrir no dispararía su alerta.

**Stack:** .NET 10, `System.Diagnostics.Metrics`, OpenTelemetry (exporta `Qep.*` por OTLP), xUnit v3.

**Spec:** [`docs/superpowers/specs/2026-11-21-observabilidad-errores-grafana-design.md`](../specs/2026-11-21-observabilidad-errores-grafana-design.md)

## Restricciones globales

- Meters con prefijo `Qep.` (`QepObservability.MeterWildcard = "Qep.*"`): salen por OTLP sin tocar la lista.
- **Ninguna etiqueta lleva tenant, conexión, factura, ruta, mensaje de log ni mensaje de excepción.**
- `Modules.Siigo.Application` no referencia observabilidad: `SiigoMetrics` vive en `Modules.Siigo.Infrastructure/Api/`.
- El cliente HTTP `siigo` sigue sin loggers: no se agrega ninguna línea de log. `SiigoNoLeakTests` sigue en verde.
- `AddQepLogging` limpia los proveedores y deja la consola JSON; el proveedor nuevo no cambia lo que se escribe a stdout.
- Sin migraciones. TDD: RED antes que GREEN con la salida literal. Un RED de compilación no levanta contenedores (regla del workspace).
- Commits convencionales, sin atribución de IA. Rama `feature/observabilidad-errores`; no se toca `docs/estado.md`.
- Comentarios y documentación en español tuteando; identificadores en inglés.

## Foco de revisión

Entradas que el spec implica y ninguna tarea cubriría sola; cada una tiene su prueba en la tarea dueña:

1. Un log `Error` con estado y excepción nulos no lanza ni se loguea a sí mismo (Tarea 1).
2. Una excepción de tipo genérico abierto o anidado: `FullName` puede ser `null`; la etiqueta cae a `Name` y nunca queda vacía (Tarea 1).
3. Incrementos en paralelo suman exacto: 1000 llamadas concurrentes dan 1000 (Tarea 2).
4. Una falla sin status (`http_` vacío) se cuenta como `unknown`, no como una serie `http_` (Tarea 2).
5. Tras un reinicio la primera falla cuenta: las series de alerta existen en cero antes de ocurrir nada (Tareas 1 y 2).

---

### Tarea 1: Contador de errores logueados

**Archivos:**
- Crear: `src/BuildingBlocks/BuildingBlocks.Observability/ErrorCountingLoggerProvider.cs`
- Modificar: `src/BuildingBlocks/BuildingBlocks.Observability/QepObservability.cs:112-125` (`AddQepLogging`)
- Prueba: `tests/Bootstrapper/Bootstrapper.UnitTests/ErrorCountingLoggerProviderTests.cs`

**Interfaces:**
- Produce: `public sealed class ErrorCountingLoggerProvider(Meter meter) : ILoggerProvider`, en el namespace `BuildingBlocks.Observability`. **No** implementa `ISupportExternalScope` (decisión del spec: `AddSensitivePathRedaction` solo reenvuelve a quien lo implementa).
- Produce, en el `Meter` recibido: `Counter<long>` `qep.log.errors` (unidad `{entry}`; etiquetas `level`, `category`, `exception_type`) y `ObservableCounter<long>` `qep.log.error_entries` (unidad `{entry}`; etiquetas `level`, `has_exception`), esta última con las cuatro combinaciones `Error|Critical` × `true|false` reportadas siempre, desde cero.
- `exception_type` es `exception.GetType().FullName ?? exception.GetType().Name`, o `none` sin excepción.

- [ ] **Paso 1: escribir las pruebas que fallan** (`ErrorCountingLoggerProviderTests`). Cada una crea su propio `Meter` (`new Meter("test-" + Guid.NewGuid())`) y lo pasa al proveedor, así que no comparten estado con otras corridas. Se escuchan con `MeterListener` filtrando por `instrument.Meter == meter`, como `MessagingMetricsTests`:
  - `AnErrorWithAnExceptionCountsByLevelCategoryAndType`: `LogError(new InvalidOperationException("secreto"), "mensaje secreto")` en la categoría `"Tests.Cat"` suma 1 a `qep.log.errors` con `("Error", "Tests.Cat", "System.InvalidOperationException")`, y 1 a `qep.log.error_entries` con `("Error", "true")`.
  - `ACriticalWithoutAnExceptionCountsAsNone`: `LogCritical("x")` suma `("Critical", "Tests.Cat", "none")` y `error_entries ("Critical","false")`.
  - `AWarningAndBelowCountNothing`: `LogWarning`, `LogInformation`, `LogDebug`, `LogTrace` no suman a ningún instrumento, y `ILogger.IsEnabled(LogLevel.Warning)` es `false`.
  - `NoTagCarriesTheMessage`: ni una etiqueta de ninguna medición contiene `"secreto"`.
  - `TheEntriesSeriesStartAtZero`: sin loguear nada, `listener.RecordObservableInstruments()` entrega exactamente las cuatro series de `qep.log.error_entries` con valor `0`.
  - `AnErrorWithoutStateOrExceptionDoesNotThrow` (foco 1): `logger.Log(LogLevel.Error, new EventId(1), (object?)null, null, (s, e) => string.Empty)` no lanza y cuenta `none`.
  - `AnOpenGenericExceptionTypeKeepsANonEmptyTag` (foco 2): una excepción cuyo tipo es un genérico anidado en una clase privada de la prueba produce una etiqueta no vacía.
  - `TheProviderDoesNotImplementExternalScopes`: `Assert.False(typeof(ISupportExternalScope).IsAssignableFrom(typeof(ErrorCountingLoggerProvider)))`.
  - `RegistrationOrderAgainstTheRedactionDoesNotChangeTheCount`: dos `ServiceCollection` con `AddLogging(b => b.AddQepLogging())` y `AddSensitivePathRedaction()` en un orden y en el otro; un `LogError` con categoría única por prueba suma 1 en ambos, escuchando el meter estático `QepObservability.Meter` y filtrando por esa categoría.

- [ ] **Paso 2: correr y ver el RED** (error de compilación; esa salida es la evidencia):
  `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~ErrorCountingLoggerProviderTests"` → Esperado: `error CS0246: The type or namespace name 'ErrorCountingLoggerProvider' could not be found`.

- [ ] **Paso 3: implementar `ErrorCountingLoggerProvider`.** Un logger interno por categoría con `IsEnabled(level) => level >= LogLevel.Error`; `Log` sale pronto si no está habilitado, no formatea el mensaje ni lee el estado, y envuelve todo en `try/catch` (contar nunca lanza ni loguea). Los cuatro acumuladores de `error_entries` son `long[4]` con `Interlocked`; el callback del `ObservableCounter` devuelve siempre las cuatro `Measurement<long>`.

- [ ] **Paso 4: registrar en `AddQepLogging`** con `logging.AddProvider(new ErrorCountingLoggerProvider(Meter))`, después de la consola JSON y dentro del mismo método.

- [ ] **Paso 5: correr y ver el GREEN:** el mismo comando del paso 2 → Esperado: todas pasan. Luego `dotnet test tests/Bootstrapper/Bootstrapper.UnitTests --filter "FullyQualifiedName~SensitivePathTelemetryTests"` → Esperado: pasan (la redacción sigue igual).

- [ ] **Paso 6: commit** `feat(observability): count logged errors by category and exception type`

---

### Tarea 2: Contador de fallas de Siigo y cierre

**Archivos:**
- Crear: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoMetrics.cs`
- Modificar: `src/Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoApiClient.cs:23-28` (constructor) y `:396-423` (`RecordAsync`)
- Modificar: `src/Modules/Siigo/Modules.Siigo.Infrastructure/SiigoInfrastructureExtensions.cs:58` (registro)
- Modificar: `tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs` (entrada junto a la línea 243)
- Modificar: `.claude/rules/siigo.md` (una regla nueva)
- Prueba: `tests/Modules/Siigo/Modules.Siigo.UnitTests/SiigoMetricsTests.cs` (nueva) y `SiigoApiClientTests.cs` (casos nuevos)

**Interfaces:**
- Produce: `internal sealed class SiigoMetrics(IMeterFactory meterFactory)` con `void RecordFailure(SiigoCallOperation operation, string reason)`. Meter `Qep.Siigo`; `ObservableCounter<long>` `qep.siigo.call_failures` (unidad `{call}`; etiquetas `operation` = nombre del enum, `reason`). Sembrado en cero: cada valor de `SiigoCallOperation` × `timeout`, `network`, `credentials_rejected`, `invalid_response`, `partner_id_missing`, `auth_without_token`. Un `reason` vacío o en blanco se registra como `unknown`. Estado: `ConcurrentDictionary<(string Operation, string Reason), StrongBox<long>>` con `Interlocked`; el callback devuelve todas las entradas.
- Consume: `SiigoApiClient` recibe `SiigoMetrics? metrics = null` como último parámetro del constructor primario (opcional: `SiigoApiClientTests` y el arnés de integración construyen el cliente a mano).

- [ ] **Paso 1: escribir las pruebas que fallan.**
  - `SiigoMetricsTests` (con `new ServiceCollection().AddMetrics().BuildServiceProvider()` y `MeterListener`, igual que `MessagingMetricsTests`):
    - `TheSeriesStartAtZeroForEveryOperationAndFixedReason`: tras `new SiigoMetrics(factory)` y `RecordObservableInstruments()`, existen `Enum.GetValues<SiigoCallOperation>().Length × 6` series, todas en `0`.
    - `AFailureAddsOneToItsSeries`: `RecordFailure(InvoiceCreate, "http_400")` deja `("InvoiceCreate","http_400") = 1` y las demás en `0`.
    - `ABlankReasonCountsAsUnknown`: `RecordFailure(Auth, "http_")` y `RecordFailure(Auth, " ")` suman en `("Auth","unknown")`; no existe una serie `http_`.
    - `ConcurrentFailuresAddUpExactly` (foco 3): `Parallel.For(0, 1000, ...)` sobre `RecordFailure(InvoiceCreate, "timeout")` da exactamente `1000`.
  - En `SiigoApiClientTests`, con un `SiigoMetrics` real sobre el mismo `AddMetrics()` y un `MeterListener`; el cliente de prueba (`NewClient`) recibe el `SiigoMetrics` como sexto argumento:
    - `ADoubleUnauthorizedOnAnInvoiceCountsTheCallersOperation`: 401 en `/v1/invoices` tras reautenticar suma `("InvoiceCreate","credentials_rejected") = 1` (la captura trae la ruta real, no `auth`).
    - `ARejectedAuthCountsAsAuth`: `/auth` rechazado suma `("Auth","credentials_rejected") = 1`.
    - `ATimeoutCountsItsReason`: un timeout en una lectura suma `(<operación de la lectura>, "timeout") = 1`.
    - `AnInvoiceBadRequestCountsHttp400`: 400 en `/v1/invoices` suma `("InvoiceCreate","http_400") = 1`.
    - `AMissingPartnerIdCountsEvenWithoutAFailureLog`: cliente sin `ISiigoCallFailureLog`, `PartnerId` vacío → suma `(<operación>, "partner_id_missing") = 1`.
    - `ASuccessfulCallCountsNothing`: una lectura exitosa deja todas las series en `0`.

- [ ] **Paso 2: correr y ver el RED** (compilación): `dotnet test tests/Modules/Siigo/Modules.Siigo.UnitTests --filter "FullyQualifiedName~SiigoMetricsTests|FullyQualifiedName~SiigoApiClientTests"` → Esperado: `error CS0246: ... 'SiigoMetrics' ...`.

- [ ] **Paso 3: implementar `SiigoMetrics`** con la firma y el estado de arriba. El meter se crea con `meterFactory.Create("Qep.Siigo")`, como `ShopifyMetrics`.

- [ ] **Paso 4: cablear `SiigoApiClient`.** En `RecordAsync`, **antes** de la guarda `failures is null || capture is null`, llamar `metrics?.RecordFailure(operation, reason)` con la misma regla de operación de la fila: `Auth` si `capture?.Path == "auth"`, si no la `operation` recibida. Registrar `services.AddSingleton<SiigoMetrics>();` junto a `SiigoTokenCache` en `SiigoInfrastructureExtensions`.

- [ ] **Paso 5: correr y ver el GREEN:** el comando del paso 2 → Esperado: pasan; el resto de `SiigoApiClientTests` sigue pasando.

- [ ] **Paso 6: inventario de réplicas.** Correr `dotnet test tests/ArchitectureTests --filter "FullyQualifiedName~MultiReplicaInventoryTests"`. Si pide clasificar `SiigoMetrics` (singleton con estado), agregar `new("Modules.Siigo.Infrastructure.Api.SiigoMetrics", StateClassification.PerPodByDesign, "Modules/Siigo/Modules.Siigo.Infrastructure/Api/SiigoMetrics.cs:<línea del diccionario> — contadores por pod; Prometheus los suma entre réplicas por etiqueta")` en `MultiReplicaInventory.cs`. Esperado: verde. Si no la pide, no se agrega nada.

- [ ] **Paso 7: no filtración.** Correr una sola vez la clase de integración: `dotnet test tests/Modules/Siigo/Modules.Siigo.IntegrationTests --filter "FullyQualifiedName~SiigoNoLeakTests"` (antes de lanzarla: `Get-Process testhost, dotnet -ErrorAction SilentlyContinue`; si hay otra corrida, esperar). Esperado: pasa, con cero líneas en la categoría del cliente HTTP.

- [ ] **Paso 8: documentar.** Agregar a `.claude/rules/siigo.md` una regla: «`qep_siigo_call_failures_total` no lleva tenant, conexión ni ruta (cardinalidad); el tenant se busca en `siigo.call_failures` o por `TraceId`. Un motivo nuevo de `SiigoApiClient` que alimente `RecordAsync` aparece solo; uno que deba sembrarse para la alerta entra en la lista fija de `SiigoMetrics`.»

- [ ] **Paso 9: formato y compilación de lo tocado:** `dotnet build src/Modules/Siigo/Modules.Siigo.Infrastructure` y `dotnet build src/BuildingBlocks/BuildingBlocks.Observability` sin advertencias nuevas (`TreatWarningsAsErrors`).

- [ ] **Paso 10: commit** `feat(siigo): count failed Siigo calls by operation and reason`

---

## Cierre (fuera de las tareas)

Antes de mergear a `develop`: suite de `Modules.Siigo.UnitTests`, `Bootstrapper.UnitTests` y `ArchitectureTests` completas, una por una (sin paralelo entre sesiones), y revisión fuerte de toda la rama una sola vez. El merge a `develop` actualiza `docs/estado.md`. Después siguen el documento de `migracion-k8s` (dashboard y tres alertas, con las series de este plan) y el spec del frontend.
