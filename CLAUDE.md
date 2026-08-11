# `qep-backend` — reglas del repositorio

Monolito modular .NET 10. Este archivo es **prescriptivo**: reglas, precedentes y gotchas
que **no** se deducen leyendo el código. Lo descriptivo —qué existe, cómo se corre, los
contratos HTTP, la arquitectura y el glosario SDD— vive en [`README.md`](README.md) y no se
duplica acá.

Es autosuficiente para el trabajo diario: no depende del `CLAUDE.md` de la carpeta
contenedora, que es local, no está versionado y puede no existir en un clon nuevo.

Este repositorio trabaja **estrictamente con Spec-Driven Development**. No es una guía: es de
cumplimiento obligatorio. Vocabulario del método en
[README § Glosario SDD](README.md#glosario-sdd).

## Topología

`qep-backend` y `qep-frontend` son **repositorios git independientes**, cada uno con su
remoto y **su propio developer**. La carpeta que los contiene **no es un repositorio**. Nunca
se commitea desde ahí.

| Qué                                                                                | Dónde                                                        |
| ---------------------------------------------------------------------------------- | ------------------------------------------------------------ |
| **Ledger de backend — de acá arranca cada sesión**                                 | [`sdd/02-plan/plan-maestro.md`](sdd/02-plan/plan-maestro.md) |
| **Specs de los slices de backend**                                                 | [`sdd/03-modulos/`](sdd/03-modulos/)                         |
| Reglas de ejecución, método, requisitos, registro de módulos, ADRs, fichas y gates | `qep-frontend/sdd/` — **autoridad única, no se duplica**     |

**Una fila de slice vive en exactamente un ledger: el de su repo.** Un trabajo que cruza los
dos repos se parte en dos slices con IDs propios desde el planteo. Los slices cerrados que ya
cruzaban (`AUTH-04`, `AUTH-05`, `AUTH-11`) no se parten retroactivamente.

**Dependencia real que queda:** para consultar el método, los requisitos por ID o la ficha de
un módulo hace falta el checkout de `qep-frontend` como hermano de éste. El trabajo diario
—leer el ledger, abrir el spec activo, escribir código y probarlo— no lo necesita. Si no está
disponible, un contrato **no** se transcribe de memoria: se registra `DECISIÓN-PENDIENTE`.

**Que el checkout exista no alcanza: hay que verificar en qué rama está.** El 2026-08-11 se
leyó `qep-frontend/sdd/` estando ese repo en `feature/catalog`, nueve commits detrás de
`develop`, y se reportaron como faltantes un ADR y una sección de `AGENTS.md` que sí existen.
El árbol de trabajo del otro repo es un **estado**, no la autoridad. Antes de concluir que
algo falta ahí: `git -C ../qep-frontend branch --show-current`, y si el dato no aparece,
`git -C ../qep-frontend log -S "<término>" --all` más `git branch -a --contains <sha>`.

## Antes de escribir código

1. [`sdd/02-plan/plan-maestro.md`](sdd/02-plan/plan-maestro.md) — qué sigue, en este repo
2. El spec del slice activo, en [`sdd/03-modulos/<modulo>/slices/`](sdd/03-modulos/)
3. `qep-frontend/sdd/AGENTS.md` §7b y `SDD-ADR-05` — convenciones de capa
4. La ficha y el `gate.md` del módulo, en `qep-frontend/sdd/03-modulos/<modulo>/`

## Reglas duras

- **Nunca imprimir el valor de un secreto.** `dotnet user-secrets list` y equivalentes se
  corren enmascarados o contando (`| Select-String -Pattern "Clave" | Measure-Object`). Para
  verificar que un secreto existe alcanza con eso. `user-secrets` los mantiene fuera del repo
  —su único trabajo— pero **no están cifrados**: lo que se lea queda en el historial de la
  sesión, en un log o en una captura. Lo que se filtre se rota. Precedente: el 2026-08-09 se
  imprimió la API key de Infobip completa al verificar si estaba configurada. Qué secretos usa
  el repo y cómo se cargan: [README § Secretos de usuario](README.md#secretos-de-usuario).
- **Sin spec no hay código.** Todo cambio pertenece a un slice con ID, **salvo infraestructura
  y despliegue**, que el owner declaró fuera del alcance del método el 2026-08-11
  (`DECISIÓN-PENDIENTE-INFRA-01`). El corte es **por efecto, no por carpeta**: la pregunta es
  si cambia el comportamiento observable de la API. Mover un valor entre `appsettings.json`,
  ConfigMap y Secret está fuera; cambiar lo que ese valor **hace**, no. La tabla completa está
  en [`sdd/02-plan/plan-maestro.md` § Alcance del método](sdd/02-plan/plan-maestro.md). La
  exención no alcanza al handoff: el ledger se actualiza igual.
- **Sin gate cerrado no hay implementación.** Un módulo `Propuesto` o `Definido` no recibe
  código, aunque su spec parezca completo.
- **TDD obligatorio.** RED antes que GREEN, con evidencia literal de ambos en el spec.
- **No inventar.** Campos, estados, rutas, permisos, roles y códigos de error deben existir en
  el código o en el spec. Lo que falte se registra como `DECISIÓN-PENDIENTE`.
- **Un slice a la vez en este repositorio.** El frontend corre en paralelo con el suyo.
- **Los IDs no se renumeran nunca.** Al partir un slice se usa sufijo de letra (`CAT-02a`,
  `CAT-02b`) y el ID padre permanece como fila en el ledger.
- **La autoridad es el código.** Ante discrepancia entre el código y un documento, gana el
  código y el documento se corrige (`SDD-ADR-01`).
- **Commits:** conventional commits con el ID del slice en el scope. **Sin atribución de IA.**
  Si la rama actual es la por defecto (`main`), se crea rama antes de commitear.

## Entorno de desarrollo

**Windows con PowerShell.** Todo comando que se entregue al developer va en PowerShell.

| Qué                 | Bash (no sirve)       | PowerShell                                              |
| ------------------- | --------------------- | ------------------------------------------------------- |
| Variable de entorno | `VAR=valor comando`   | `$env:VAR = "valor"`, en línea aparte                   |
| curl real           | `curl`                | **`curl.exe`** — `curl` es alias de `Invoke-WebRequest` |
| Encadenar           | `A && B`              | `A; if ($?) { B }` — no hay `&&` ni `\|\|`              |
| Ver archivo         | `cat`, `head`, `tail` | `Get-Content`, `-TotalCount`, `-Tail`                   |

**Cuerpos JSON a un `.exe`:** PowerShell rompe las comillas dobles al pasarlas. Van a archivo
y se mandan con `-d "@archivo.json"`. Git Bash está instalado y es alternativa válida, pero se
ofrece después de la versión de PowerShell.

Esto vive acá y no en un archivo compartido porque **este repo tiene su propio developer**
(`SDD-ADR-08`): declarar su entorno en su repo no le impone nada al del frontend.

## Convenciones del backend

Capas según `SDD-ADR-05` (`Domain` → `Application` → `Infrastructure` → `Api`, un assembly
cada una), verificadas por `tests/ArchitectureTests/` con **un archivo por módulo**. Un módulo
nuevo trae su propio `<Modulo>LayerTests.cs`. El detalle de los patrones ya implementados —CQ
propio, DDD, outbox/inbox, repositorio + unit of work, concurrencia optimista— está en
[README § Patrones técnicos y componentes](README.md#patrones-técnicos-y-componentes).

Lo que hay que saber **antes** de escribir, y no se ve leyendo un módulo ya hecho:

- **Aislamiento de tenant, doble capa.** La ruta lleva `{tenantId:guid}` y la política exige
  el permiso; **además** el handler revalida tenant y permiso antes de tocar el repositorio
  (`CatalogAuthorization`, `StorageAuthorization`). Devuelve **403, nunca 404**: un 404
  confirmaría que el id existe en otro tenant. Todo método de repositorio recibe `tenantId`.
- **Un permiso nuevo necesita dos mitades:** la constante en `<Modulo>Permissions` **y** su
  política en `AddAuthorization` (`QepServiceCollectionExtensions`), que se registran una por
  una a mano. Sin la política, `RequireAuthorization` no resuelve y el síntoma es **500, no
  403** — no se parece en nada a la causa.
- **Un caso de uso con texto libre lleva validador de FluentValidation aunque el dominio ya
  valide:** el dominio da un código (`DomainException` → 422), el validador da el campo
  (`ValidationException` → 422 `validation.failed`, con el mapa `errors`). Ningún status HTTP
  se arma a mano en un handler: el mapeo es central en `src/Api/ApiExceptionHandler.cs`.
- **Traducir errores de base va en Infrastructure, no en Application.**
  `Modules.<Modulo>.Application` no referencia EF Core ni Npgsql, y `ArchitectureTests` lo
  verifica. Discriminar por **nombre de índice**, no sólo por `SqlState 23505`: hay varios
  índices únicos y devolver el código equivocado manda a corregir el campo equivocado
  (`SDD-CT-06`, `catalog.product.code_taken`).
- **Auditoría y outbox en la misma transacción.** Dos caminos: el atómico (`IAuditRecorder`,
  atado al `DbContext` del productor) y el de outbox (`I<Modulo>AuditPublisher`), para
  operaciones operativas. Una prueba que verifica sólo el status HTTP deja pasar el efecto que
  importa.

## Gotchas verificados

- **`Api.exe` corriendo bloquea todo.** `dotnet build`, `dotnet test` y los comandos `ef`
  fallan por archivo bloqueado. Detener el proceso primero. El nombre es `Api.exe` porque
  `src/Api/Api.csproj` no declara `AssemblyName`.
- **Las migraciones se generan con el factory de diseño, no con `--startup-project`.**
  `Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`:

  ```powershell
  dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations
  ```

- **Las factories de integración deben fijar `Notifications:EmailProvider`** (`SDD-CT-17`).
  Sin eso heredan lo que diga `appsettings.json`; con `infobip` y las claves ausentes,
  `NotificationsOptionsValidator` falla al arrancar y **todas** las pruebas del archivo mueren
  antes de su aserción.
- **El stub de desarrollo concede sólo los permisos de tenancy por defecto.** Una prueba que
  necesita un permiso de otro módulo tiene que pedirlo por `X-Permissions`, o su 403 va a
  venir del permiso faltante y no de lo que cree estar probando.
- **Probar auth en local tiene dos trampas.** El interruptor y los dos modos están en
  [README § Activar y desactivar el modo de desarrollo](README.md#activar-y-desactivar-el-modo-de-desarrollo-auth).
  Lo que ahí no se dice: (1) `UseDevelopmentStub` es `true` por defecto en `Development`
  (`QepAuthenticationMode.cs:16`), pero **los dos perfiles de `launchSettings.json` lo fijan en
  `false`**, así que un `dotnet run` normal corre con auth real; para usar los headers `X-*` hay
  que pedir el stub con `$env:Authentication__UseDevelopmentStub = "true"`. (2) En modo stub
  **no se registra el middleware de CSRF** (`Program.cs:61-65`), así que un `POST` sin
  `X-Qep-Client: web` pasa; ese header sólo se puede probar con el stub apagado.
- **Los user-secrets se cargan en `Development` sin ayuda de nadie.** Los agrega
  `WebApplication.CreateBuilder`, que es comportamiento del framework. El
  `if (IsEnvironment("Local"))` que este archivo describía en `Program.cs:26` ya no existe: era
  redundante en `Development` y lo eliminó `ccd2eca`.
- **La cadena de conexión no está en `appsettings.json`, y es requerida.** Se quitó el
  2026-08-11: apuntaba a una base inexistente —ni siquiera la del `compose.yaml` de este repo,
  que crea `qep` en `5432`— y llevaba contraseña, que es justo lo que un `appsettings*.json`
  versionado no debe llevar. Ausente, los seis `AddXInfrastructure` fallan al arrancar con
  `InvalidOperationException: Connection string 'QepDatabase' is required.` **El guard es
  `?? throw`, o sea sólo contra `null`:** dejar la clave con `""` lo atraviesa y el error lo
  termina tirando Npgsql, que no explica nada. En local se pone por user-secrets (ver
  [README § Ejecución local](README.md#ejecución-local)); en k8s la fija `prod-secret.yaml`
  como `ConnectionStrings__QepDatabase` — **no** el ConfigMap, que es texto plano para
  cualquiera con `get` sobre ConfigMaps en el namespace.
- **`SDD-CT-14` abierta:** 5 pruebas de `RealAuthenticationApiTests` fallan con
  `Expected: Created / Actual: Unauthorized`. Son preexistentes: al medir regresión hay que
  verificarlas **por nombre**, no por conteo.
- **`SDD-CT-07` abierta:** un registro de tenant que falla después de aprovisionar deja el
  usuario en `identity.users` **sin membresía**. `RegistrationEndpoints.cs:90-105` aprovisiona
  antes de crear el tenant, en módulos con unidades de trabajo distintas y sin compensación.
- **`Conversations` y `Reporting` no existen**, aunque los requisitos las supongan. Los
  módulos construidos son Audit, Authorization, Catalog, Identity, Notifications, Storage y
  Tenancy.

## Cierre de sesión de trabajo

Actualizar [`sdd/02-plan/plan-maestro.md`](sdd/02-plan/plan-maestro.md) con estado, evidencia
literal, comandos y su resultado real, commit y handoff. Siempre, incluso si el trabajo quedó
bloqueado o sin commit. **Nunca el ledger del frontend por un slice de este repo.**

Si cambió el estado de un módulo, eso sí se actualiza en
`qep-frontend/sdd/01-contexto/registro-de-modulos.md`, que es autoridad de producto.

Los comandos de verificación (`restore` / `format` / `build` / `test`) están en
[README § Verificación](README.md#verificación).
