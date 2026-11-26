---
paths:
  - "tests/**"
  - "src/Api/**"
  - "**/launchSettings.json"
  - "src/Bootstrapper/Authentication/**"
  - "src/Bootstrapper/Seeding/**"
  - "src/Bootstrapper/Startup/**"
---

# Pruebas de integración y entorno local

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **El stub de desarrollo concede sólo los permisos de tenancy por defecto.** Una prueba que
  necesita un permiso de otro módulo tiene que pedirlo por `X-Permissions`, o su 403 va a
  venir del permiso faltante y no de lo que cree estar probando.
- **Un tenant insertado directo después del arranque no tiene los cinco roles de fábrica** (spec 2026-11-25, roles por
  tenant): sólo `admin` sale del código; los demás los siembran el alta y el arranque. Si la prueba invita o resuelve uno de
  ellos, el helper que inserta el tenant llama `ITenantRoleSeeder.SeedAsync` (ejemplo: `SupportApiHarness.SeedTenantAsync`).
  Sin eso, invitar responde `422 tenancy.membership.role_unknown` y quien ya nombra el rol no recibe ningún permiso, así que una
  prueba negativa sigue verde por el motivo equivocado.
- **Probar auth en local tiene dos trampas.** El interruptor y los dos modos están en
  [README § Activar y desactivar el modo de desarrollo](../../README.md#activar-y-desactivar-el-modo-de-desarrollo-auth).
  Lo que ahí no se dice: (1) `UseDevelopmentStub` es `true` por defecto en `Development`
  (`QepAuthenticationMode.UseDevelopmentStub`), pero **los dos perfiles de `launchSettings.json` lo fijan en
  `false`**, así que un `dotnet run` normal corre con auth real; para usar los headers `X-*` hay
  que pedir el stub con `$env:Authentication__UseDevelopmentStub = "true"`. (2) En modo stub
  **no se registra el middleware de CSRF** (`Program.cs`), así que un `POST` sin
  `X-Qep-Client: web` pasa; ese header sólo se puede probar con el stub apagado.
- **Los user-secrets se cargan solos en `Development`.** Los agrega
  `WebApplication.CreateBuilder`; el repo no necesita código propio para eso.
- **La cadena de conexión no está en `appsettings.json`, y es requerida.** Se quitó el
  2026-08-11: apuntaba a una base inexistente —ni siquiera la del `compose.yaml` de este repo,
  que crea `qep` en `5432`— y llevaba contraseña, que es justo lo que un `appsettings*.json`
  versionado no debe llevar. Ausente, los seis `AddXInfrastructure` fallan al arrancar con
  `InvalidOperationException: Connection string 'QepDatabase' is required.` **El guard es
  `?? throw`, o sea sólo contra `null`:** dejar la clave con `""` lo atraviesa y el error lo
  termina tirando Npgsql, que no explica nada. En local se pone por user-secrets (ver
  [README § Ejecución local](../../README.md#ejecución-local)); en k8s la fija `prod-secret.yaml`
  como `ConnectionStrings__QepDatabase` — **no** el ConfigMap, que es texto plano para
  cualquiera con `get` sobre ConfigMaps en el namespace.
- **Endurecer una regla de dominio rompe las pruebas de integración que arman el cuerpo a
  mano, y el síntoma no se parece a la causa.** El handler devuelve `422 validation.failed`
  donde la prueba esperaba `Created`, o —peor— un 422 que la prueba sí esperaba pero por
  otro motivo. Pasó dos veces en una semana y costó 23 de 24 fallas el 2026-09-05: `CityId`
  pasó a obligatorio en `CreateCompanyRequest` (`613912a`, 2026-08-31) y once pruebas de
  Companies seguían posteando sin él; `ValidUntil` pasó a obligatorio para enviar (`86efda1`,
  2026-09-04) y `ReportingApiHarness.CreateSentQuotationAsync` —de un día antes— tumbó las
  doce de Reporting desde una sola línea. **Al agregar un campo requerido o una precondición,
  hay que barrer las pruebas de integración por cuerpos crudos**, no sólo los harness: las que
  usan el harness se arreglan solas y esconden el problema.
- **En esta máquina las pruebas de integración necesitan `$env:Seed__Enabled = "false"`.** Los user-secrets traen
  `Seed:*` y el host de pruebas falla en el arranque con `Seed:OwnerEmail is required when Seed:Enabled is true`. No es
  del código: es la configuración local del developer (2026-10-21).
- **No pongas `ConnectionStrings:QepDatabaseListener` en tus user-secrets sin fijarla en el harness** (spec 2026-10-29,
  D-2). Sólo `SeedStartupTests` la fija vacía; las demás factorías de integración corren en `Development` y cargan los
  user-secrets, y el candado de arranque (`StartupLockConnectionString`) y el listener de Messaging la usan en **cada** host
  de prueba. Con un valor inalcanzable, todo host falla al arrancar; con uno alcanzable, todos los hosts de prueba se
  serializan en un solo advisory lock global (`pg_advisory_xact_lock(20261113, 1)`) de esa base, aunque cada uno tenga su
  propio contenedor. Si la necesitas en local, fíjala en el harness (vacía o al contenedor de la prueba).
- **Las pruebas de integración comparten host por clase (`Shared<Modulo>Host`), y no todas pueden** (2026-11-14; Quotations
  bajó de 63 a 16 min). Una prueba nueva usa `sharedHost.StartTestAsync()` sólo si corre con la configuración por defecto y no
  toca nada global: dobles que anotan llamadas, colas o workers que reclaman filas de toda la base, `IClock` o
  `WithWebHostBuilder`, conteos o SQL sin tenant, semillas o migraciones. Si toca algo de eso, levanta su propio host
  (`StartDatabaseAsync` + `new QepApiFactory`); las de migración usan `StartEmptyDatabaseAsync`. Una prueba compartida que rompe
  la regla falla según el orden de las demás de su clase, y el síntoma no se parece a la causa. `OutboxMessagesAsync` ya
  descarta los eventos de las pruebas anteriores (`OutboxBaseline`).
- **Una prueba de migración que baja a una versión vieja tiene que volver a la última antes de llamar a la
  API.** El modelo de hoy pide las columnas de las migraciones posteriores (`AddFreight`); sin
  `MigrateAsync(null)` al final, el `POST` de la prueba da 500 (`QuotationSettingsMigrationTests`).
