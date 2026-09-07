# `qep-backend` — reglas del repositorio

Monolito modular .NET 10. Este archivo es **prescriptivo**: reglas, precedentes y gotchas
que **no** se deducen leyendo el código. Lo descriptivo —qué existe, cómo se corre, los
contratos HTTP y la arquitectura— vive en [`README.md`](README.md) y no se duplica acá.

Es autosuficiente para el trabajo diario: no depende del `CLAUDE.md` de la carpeta
contenedora, que es local, no está versionado y puede no existir en un clon nuevo.

## Topología

`qep-backend` y `qep-frontend` son **repositorios git independientes**, cada uno con su
remoto y **su propio developer**. La carpeta que los contiene **no es un repositorio**. Nunca
se commitea desde ahí.

**Que el checkout hermano exista no alcanza: hay que verificar en qué rama está.** El
2026-08-11 se leyó `qep-frontend` estando ese repo en `feature/catalog`, nueve commits detrás
de `develop`, y se reportó como faltante contenido que sí existe. El árbol de trabajo del otro
repo es un **estado**, no la autoridad. Antes de concluir que algo falta ahí:
`git -C ../qep-frontend branch --show-current`, y si el dato no aparece,
`git -C ../qep-frontend log -S "<término>" --all` más `git branch -a --contains <sha>`.

## Reglas duras

- **Nunca imprimir el valor de un secreto.** `dotnet user-secrets list` y equivalentes se
  corren enmascarados o contando (`| Select-String -Pattern "Clave" | Measure-Object`). Para
  verificar que un secreto existe alcanza con eso. `user-secrets` los mantiene fuera del repo
  —su único trabajo— pero **no están cifrados**: lo que se lea queda en el historial de la
  sesión, en un log o en una captura. Lo que se filtre se rota. Precedente: el 2026-08-09 se
  imprimió la API key de Infobip completa al verificar si estaba configurada. Qué secretos usa
  el repo y cómo se cargan: [README § Secretos de usuario](README.md#secretos-de-usuario).
- **`kubectl get secret` imprime los valores. `describe` no.** El campo `.data` es base64 —
  codificación, no cifrado— así que `-o yaml`, `-o json` y cualquier `-o custom-columns` que
  incluya `.data` filtran el secreto completo. Para ver **qué claves** tiene un Secret, sin sus
  valores:

  ```powershell
  kubectl -n <ns> describe secret <nombre>            # nombres y tamaño en bytes
  kubectl -n <ns> get secret <nombre> -o jsonpath='{range $k,$v := .data}{$k}{"\n"}{end}'
  ```

  Precedente: el 2026-08-11 se volcaron las cuatro credenciales de `qep-backend-secret`
  —contraseña de base, API key de Infobip y el par de R2— al verificar un despliegue con
  `-o custom-columns=KEYS:.data`. Las cuatro se rotaron. Es la **segunda** vez que pasa en este
  proyecto: la primera fue la API key de Infobip el 2026-08-09.

- **TDD obligatorio.** RED antes que GREEN, con evidencia literal de ambos.
- **No inventar.** Campos, estados, rutas, permisos, roles y códigos de error deben existir en
  el código. Lo que falte se registra explícitamente como decisión pendiente, no se asume.
- **La autoridad es el código.** Ante discrepancia entre el código y un documento, gana el
  código y el documento se corrige.
- **Commits:** conventional commits. **Sin atribución de IA.**
- **Antes de commitear se comprueba la rama, y si es la por defecto (`main`) se crea una
  primero.** La comprobación es `git branch --show-current` **en ese momento**, no lo que se
  haya leído al abrir la sesión: el 2026-09-05 el snapshot del arranque decía `main` mientras
  el repo estaba en `develop`, y se le informó al developer la rama equivocada. La rama es un
  **estado** y cambia entre comandos —mismo criterio que con el checkout de `qep-frontend`—,
  así que ni el contexto inicial ni un `git status` de hace diez minutos son autoridad sobre
  ella.

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

Esto vive acá y no en un archivo compartido porque **este repo tiene su propio developer**:
declarar su entorno en su repo no le impone nada al del frontend.

## Convenciones del backend

Capas `Domain` → `Application` → `Infrastructure` → `Api`, un assembly cada una, verificadas
por `tests/ArchitectureTests/` con **un archivo por módulo**. Un módulo nuevo trae su propio
`<Modulo>LayerTests.cs`. El detalle de los patrones ya implementados —CQ propio, DDD,
outbox/inbox, repositorio + unit of work, concurrencia optimista— está en
[README § Patrones técnicos y componentes](README.md#patrones-técnicos-y-componentes).

Lo que hay que saber **antes** de escribir, y no se ve leyendo un módulo ya hecho:

- **Las APIs se construyen como backend for frontend, no como REST genérica.** El consumidor
  es `qep-frontend` y ninguno más, así que la forma de cada respuesta se decide por lo que la
  pantalla necesita dibujar, no por la pureza del recurso. Ante una decisión discutible gana la
  que le ahorra lógica al cliente, **y el motivo se escribe en el DTO**: desde el backend no se
  ve la pantalla que lo obligó, y sin esa nota el siguiente lo "simplifica".
- **Qué significa eso en concreto, con los precedentes ya escritos.** Un agregado que la
  pantalla no puede calcular es un endpoint, no un problema del cliente:
  `/reports/sales/summary` existe porque sumar páginas de 50 filas del lado del cliente da un
  total que depende de la página que se esté mirando, y `MaxPageSize` impide pedir el período
  entero. Las colecciones de tamaño fijo viajan **completas, incluso en cero**
  (`ReportStatusSliceDto`, `PriceChangeFieldSliceDto`): un elemento que desaparece obliga a la
  pantalla a conocer el enum del backend para dibujar el que falta. Un total que no cierra se
  explica con un campo y no con una omisión (`QuotationValidityDto.WithoutExpiry`). Los enums
  viajan con su nombre (`Sent`, `PriceBaseUsd`) porque el diccionario lo tiene el frontend. El
  422 de validación lleva el mapa `errors` (`ApiExceptionHandler.cs:51`), el único 422 que el
  formulario sabe leer para marcar el input. Y una colección que se edita vuelve entera y en
  orden, que es lo que el formulario repinta.
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
  (precedente: `catalog.product.code_taken`).
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

- **Subir de banda el SDK toca tres archivos, y `rollForward` no ayuda.** `global.json` pide
  `latestPatch`, que sólo rueda **dentro** de la misma banda de feature: de `10.0.4xx` a
  `10.0.4xx` sí, de `4xx` a `5xx` no. Se mueven juntos `global.json`, el tag del `FROM` del
  `Dockerfile` —clavado a la banda, nunca al flotante `10.0`— y el requisito del README. CI
  no, que usa `global-json-file`. Si el `Dockerfile` queda atrás, el `docker build` muere con
  `SDK not found` y exit 155 sin que nadie haya tocado el repositorio. Las rutas de SDK del
  allowlist en `.claude/settings.json` llevan comodín a propósito, para que no sean un cuarto
  lugar que se olvida.
- **Cambiar `Directory.Packages.props` sin regenerar los lock files no rompe tu máquina: rompe
  CI y el `docker build`.** Los 74 `packages.lock.json` —uno por `.csproj`, activados por
  `RestorePackagesWithLockFile` en `Directory.Build.props`— se regeneran con
  `dotnet restore --force-evaluate` y se commitean **junto** con el props. En local un `restore`
  normal los actualiza solo y no protesta; CI y el `Dockerfile` corren `--locked-mode`, donde
  NuGet no resuelve nada sino que exige que el grafo coincida exacto, y fallan con `NU1004`. El
  diff de los locks es además el único lugar donde se ve el arrastre transitivo: el parche de
  .NET del 2026-09-05 movió `Microsoft.IdentityModel.*` de `8.0.1` a `8.19.2`, que no está
  declarado en el props y por lo tanto no aparece en su diff.
- **`Notifications:EmailProvider` vale distinto según el ambiente, y las dos mitades son
  necesarias.** `appsettings.json` trae `log` —el canal que registra el correo y no lo manda—
  porque es el mismo archivo en local y en producción y su único trabajo es ser el default que
  arranca sin credenciales. Producción lo pisa con `infobip` desde `k8s/prod-configMap.yaml`, y
  `NotificationsOptionsValidator` **rechaza `log` en `Production`** con `ValidateOnStart`: sin esa
  mitad, olvidarse la clave dejaría las invitaciones marcadas como entregadas sin que salga un
  correo, sin error y sin log. Mismo par que `Quotations:WhatsApp:*` con Zenvia. Hasta el
  2026-09-06 el default era `infobip`, así que una factoría de integración que no fijara la clave
  mataba **todas** las pruebas de su archivo en el arranque; ya no, pero todas la fijan igual y
  conviene seguir haciéndolo: deja explícito qué canal ejerce la prueba.
- **El stub de desarrollo concede sólo los permisos de tenancy por defecto.** Una prueba que
  necesita un permiso de otro módulo tiene que pedirlo por `X-Permissions`, o su 403 va a
  venir del permiso faltante y no de lo que cree estar probando.
- **Probar auth en local tiene dos trampas.** El interruptor y los dos modos están en
  [README § Activar y desactivar el modo de desarrollo](README.md#activar-y-desactivar-el-modo-de-desarrollo-auth).
  Lo que ahí no se dice: (1) `UseDevelopmentStub` es `true` por defecto en `Development`
  (`QepAuthenticationMode.cs:15`), pero **los dos perfiles de `launchSettings.json` lo fijan en
  `false`**, así que un `dotnet run` normal corre con auth real; para usar los headers `X-*` hay
  que pedir el stub con `$env:Authentication__UseDevelopmentStub = "true"`. (2) En modo stub
  **no se registra el middleware de CSRF** (`Program.cs:64-67`), así que un `POST` sin
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
- **Defecto conocido sin cerrar:** un registro de tenant que falla después de aprovisionar
  deja el usuario en `identity.users` **sin membresía**. `RegistrationEndpoints.cs:89-104`
  aprovisiona antes de crear el tenant, en módulos con unidades de trabajo distintas y sin
  compensación.
- **Una plantilla de WhatsApp rechazada con "An error occurred while sending the template for
  approval" no la rechazó Meta:** es Zenvia que no logró enviarla. El contenido, el tono y la
  categoría nunca se revisaron, así que cambiarlos no sirve — mirá el `name` (sólo minúsculas,
  dígitos y guiones bajos), el `examples` (sin `$`, `#`, `%`) y el `Content-Type` del PDF de
  ejemplo. Costó cuatro intentos el 2026-09-04. El procedimiento entero está en
  [README § Plantilla de WhatsApp](README.md#plantilla-de-whatsapp-zenvia).
- **El texto de la plantilla no se edita: se crea otra.** Meta no permite modificar una plantilla
  aprobada, así que cambiar una coma implica plantilla nueva, aprobación nueva y apuntar
  `Quotations:WhatsApp:TemplateId` al `id` nuevo. Si además cambian las variables, se toca
  `WhatsAppQuotationMessage` y `ZenviaWhatsAppSender` — y el `TemplateId` viaja en **el mismo
  commit** que ese código: apuntar a una plantilla cuyas variables no coinciden con las que el
  backend manda rompe el envío en producción sin error de compilación.
- **`Conversations` no existe**, aunque los requisitos la supongan. Los doce módulos
  construidos son Audit, Authorization, Catalog, Companies, Customers, Geography, Identity,
  Notifications, Quotations, Reporting, Storage y Tenancy — cada uno con su
  `<Modulo>LayerTests.cs` en `tests/ArchitectureTests/`.

## Verificación

Los comandos (`restore` / `format` / `build` / `test`) están en
[README § Verificación](README.md#verificación).
