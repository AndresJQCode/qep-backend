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

- **`.mcp.json` se versiona: la clave va por `${VAR}`, nunca literal.** No está en `.gitignore`,
  así que un valor escrito ahí se commitea. Claude Code expande variables de entorno en `url`,
  `headers`, `command`, `args` y `env`, así que el archivo puede viajar en el repo sin secreto
  adentro y cada quien pone el suyo en su entorno. La única que usa hoy es
  `QCODE_PDF_API_KEY` — de dónde sale y dónde se configura, en
  [README § `QCODE_PDF_API_KEY`](README.md#qcode_pdf_api_key--el-mcp-de-qcode-pdf). **No es** la
  clave con la que el backend renderiza cotizaciones (`Quotations:Pdf:ApiKey`): `qcode-pdf`
  resuelve el `clientId` por el hash de la clave, así que compartirla mezclaría las pruebas de
  un desarrollador con el tráfico real en sus logs, y rotarla obligaría a tocar producción.

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
- **Prohibido mergear a `main`** (decisión del owner, 2026-10-10). Los merges de un agente
  terminan en `develop`; llevar `develop` a `main` lo hace el owner, nunca un agente. Un pedido de
  "mergear" o "publicar" sin destino significa `develop`.
- **[`docs/estado.md`](docs/estado.md) se actualiza en `develop`, al mergear.** Es el estado global
  del proyecto, y sin esta regla se queda viejo. El 2026-10-21, a un día de creado, decía «sin empezar» para
  variantes con el backend ya mergeado, y once specs construidos seguían en «Por verificar». Cada merge a
  `develop`, decisión del owner o despliegue actualiza la fila que corresponde y el Registro, en el mismo
  merge o en un commit `docs:` inmediatamente después. **Las ramas de feature no lo tocan:** si cada rama lo
  edita, dos ramas en paralelo chocan ahí en cada merge. Nada pasa a «Construido» sin evidencia en el código:
  la clase, la ruta o el commit.

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

## Cómo se escriben los mensajes

**En español colombiano, tuteando.** Vale para las respuestas del asistente al developer y
para todo texto que el sistema le muestra o le manda a una persona: correos, plantillas de
WhatsApp, mensajes de error legibles, avisos. **Pisa el estilo de salida del asistente**, que
por defecto vosea: sin esta regla, cada sesión nueva vuelve al rioplatense.

| Sí                                       | No (voseo)                                  |
| ---------------------------------------- | ------------------------------------------- |
| tienes, puedes, quieres                  | tenés, podés, querés                        |
| elige, revisa, intenta de nuevo          | elegí, revisá, intentá de nuevo             |
| dime, avísame, escríbenos                | decime, avisame, escribinos                 |
| No tienes permiso para ver este reporte. | No tenés permiso para ver este reporte.     |

- Registro profesional y cercano: nada de modismos rioplatenses (`dale`, `che`, `laburo`), y
  tampoco regionalismos colombianos marcados (`parce`, `bacano`). "Listo", "claro" y "de una"
  sí caben en la conversación con el developer; en un texto del producto, no.
- **No se traduce lo que es contrato:** identificadores, códigos de error
  (`quotation.export.empty`), nombres de enum (`Sent`), rutas y permisos quedan tal cual.
- Un comentario de código o una documentación que se dirige al lector también tutea. Lo que
  ya está escrito con voseo se corrige cuando se toca el archivo, no en un barrido aparte.

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
  `/reports/orders/summary` existe porque sumar páginas de 50 filas del lado del cliente da un
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
- **Toda migración es expand/contract: durante el rollout, pods viejos atienden contra el esquema nuevo** (spec
  2026-10-29, D-5). Agregar va en una versión; quitar, renombrar o volver `NOT NULL` lo que el código viejo escribe, en
  otra. Una migración que rompe al código viejo se despliega con corte (`Recreate` o el backend a cero réplicas), y el
  handoff del spec dice si es aditiva; si no lo dice, no lo es. Las reglas completas:
  [README § Migraciones con pods viejos y nuevos](README.md#migraciones-con-pods-viejos-y-nuevos-expandcontract).
- **Un hosted service o un singleton con estado nuevo se clasifica en `MultiReplicaInventory`, en el mismo commit**
  (`tests/ArchitectureTests/ArchitectureTests/MultiReplicaInventory.cs`, spec 2026-10-29, D-6). `MultiReplicaInventoryTests`
  lo detecta en el contenedor real y se pone en rojo si falta, o si queda una entrada que ya no está registrada. La
  clasificación lleva su evidencia (archivo, línea y mecanismo: `SKIP LOCKED`, lease, advisory lock, `DELETE` por corte…).
  `Unsafe` se puede listar, pero `MoreThanOneReplicaRequiresNoUnsafeEntry` bloquea `replicas > 1` mientras quede uno.
- **Una inicialización nueva al arrancar va dentro del lambda de `RunUnderStartupLockAsync` en `Program.cs`, nunca
  después.** Fuera del candado de arranque (`pg_advisory_xact_lock(20261113, 1)`, `StartupLock.cs`), dos pods la corren a
  la vez. Lo que dicen los logs 4108, 4109 y 4110 está en el mismo § del README.
- **Las migraciones se generan con el factory de diseño, no con `--startup-project`.**
  `Api.csproj` no referencia `Microsoft.EntityFrameworkCore.Design`:

  ```powershell
  dotnet ef migrations add <Nombre> --project src/Modules/<Modulo>/Modules.<Modulo>.Infrastructure --context <Modulo>DbContext -o Persistence/Migrations
  ```

- **Subir de banda el SDK toca tres archivos, y `rollForward` no ayuda.** `global.json` pide
  `latestPatch`, que sólo rueda **dentro** de la misma banda de feature: de `10.0.4xx` a
  `10.0.4xx` sí, de `4xx` a `5xx` no. Se mueven juntos `global.json`, el tag del `FROM` del
  `Dockerfile` —clavado a la banda, nunca al flotante `10.0`— y el requisito del README. Si el `Dockerfile` queda atrás, el `docker build` muere con
  `SDK not found` y exit 155 sin que nadie haya tocado el repositorio. Las rutas de SDK del
  allowlist en `.claude/settings.json` llevan comodín a propósito, para que no sean un cuarto
  lugar que se olvida.
- **Cambiar `Directory.Packages.props` sin regenerar los lock files no rompe tu máquina: rompe
  el `docker build`.** Los 152 `packages.lock.json` —uno por `.csproj`, activados por
  `RestorePackagesWithLockFile` en `Directory.Build.props`— se regeneran con
  `dotnet restore --force-evaluate` y se commitean **junto** con el props. En local un `restore`
  normal los actualiza solo y no protesta; el `Dockerfile` corre `--locked-mode`, donde NuGet
  no resuelve nada sino que exige que el grafo coincida exacto, y falla con `NU1004`. El
  diff de los locks es además el único lugar donde se ve el arrastre transitivo: el parche de
  .NET del 2026-09-05 movió `Microsoft.IdentityModel.*` de `8.0.1` a `8.19.2`, que no está
  declarado en el props y por lo tanto no aparece en su diff.
- **`Notifications:EmailProvider` vale distinto según el ambiente, y las dos mitades son
  necesarias.** `appsettings.json` trae `log` —el canal que registra el correo y no lo manda—
  porque es el mismo archivo en local y en producción y su único trabajo es ser el default que
  arranca sin credenciales. Producción lo pisa con `infobip` desde `k8s/prod-configMap.yaml`, y
  `NotificationsOptionsValidator` **rechaza `log` en `Production`** con `ValidateOnStart`: sin esa
  mitad, olvidarse la clave dejaría las invitaciones marcadas como entregadas sin que salga un
  correo, sin error y sin log. Hasta el
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
  categoría nunca se revisaron, así que cambiarlos no sirve — mira el `name` (sólo minúsculas,
  dígitos y guiones bajos), el `examples` (sin `$`, `#`, `%`) y el `Content-Type` del PDF de
  ejemplo. Costó cuatro intentos el 2026-09-04. El procedimiento entero está en
  [README § Plantilla de WhatsApp](README.md#plantilla-de-whatsapp-zenvia).
- **El texto de la plantilla no se edita: se crea otra.** Meta no permite modificar una plantilla
  aprobada, así que cambiar una coma implica plantilla nueva, aprobación nueva y editar el
  `templateId` de la conexión de Zenvia de cada tenant (no hay cuenta global desde el plan
  2026-10-21). Si además cambian las variables, se toca `WhatsAppQuotationMessage` y
  `ZenviaWhatsAppSender` — y el `templateId` de las conexiones se actualiza en **el mismo
  despliegue** que ese código: una plantilla cuyas variables no coinciden con las que el backend
  manda rompe el envío en producción sin error de compilación.
- **`Pos` necesita la capacidad `pos` y no viene con el signup.** `GrantDefaultModulesOnSignup`
  concede siete módulos (los seis base y `shopify`) sin `pos`, así que en un tenant nuevo todo `/pos/*`
  da 403 aunque el rol tenga los permisos: se prende con la fila de `tenancy.tenant_modules` (spec de entitlements,
  «Operación»). En pruebas, `PosApiHarness.EnablePosAsync`. Y `cashier` es clave de rol de sistema
  reservada: antes de desplegar se comprueba que ningún tenant tenga un rol custom con esa clave
  (spec 2026-10-07, «Despliegue»).
- **La cotización por Meta no usa `OutboundGuards` ni `ConversationOpener.OpenAsync`** (spec 2026-10-21). `QuotationTemplateOutbound`
  busca la conversación por teléfono (`OpenByPhoneAsync`) y no por `customer_id`, para no caer en el hilo de otro número (D16b);
  pasa `seen: 0` para no tocar los no leídos (D16a); y el PDF va por `id` de media, no por URL. Un cambio en el reclamo o la
  liquidación (`IOutboundMessages`, `OutboundSettlement`) se prueba también con `QuotationTemplateOutboundTests`.
- **El módulo de cobros recurrentes se llama `ServiceContracts` en el código y `billing` en todo lo desplegado** (owner,
  2026-10-30). Carpeta `src/Modules/ServiceContracts/`, namespaces `Modules.ServiceContracts.*`, agregado `ServiceContract`
  (antes `BillingService`), tipos `ServiceContracts*` (`ServiceContractsDbContext`, `ServiceContractsPermissions`,
  `IServiceContractsAuditPublisher`…), en pantalla «Contratos de servicio». **No cambiaron, y no se cambian sin migración de
  datos ni despliegue conjunto con el frontend:** el esquema `billing`, la clave de módulo `billing` (`TenantModuleKey.ServiceContracts`),
  los permisos `billing.service.read|manage`, las rutas `/api/v1/tenants/{id}/billing` y `/billing` del SPA, las acciones de
  auditoría y los códigos de error `billing.*`, la sección `Billing` de configuración (`ServiceContractsOptions.SectionName`) y el id
  de la migración `20261029180546_InitialBilling`. Conservan «Billing» a propósito porque nombran el cobro y no el módulo, o son de
  otro módulo: `BillingCycle`, `BillingChannel`, `CustomerBillingProfile`; `MessageTemplateKind.BillingReminder|BillingOverdue`
  (persistidos como texto), `IBillingTemplateOutbound` y `BillingTemplateDefinitions` (Messaging), las plantillas `Billing*Email*`
  (Notifications), `BillingAccount`/`BillingParty` (Quotations: la facturación de un pedido) y el rol de sistema `billing`
  (`SystemRoleKeys.Billing`). Un `grep Billing` que encuentre algo fuera de esa lista dentro del módulo es un resto del renombre.
- **`CustomerMergeReferences` también escribe en `loans.loans`** (`customer_id`, sin subir versión; las cuotas no guardan cliente). Es
  una sentencia más antes de la de Shopify, que sigue siendo la última. Renombrar esa columna compila y rompe la fusión en
  ejecución; la amarran `CustomerMergeReferencesTests` y `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema`.
- **`Conversations` como módulo no existe; la bandeja de WhatsApp es `Messaging`** (spec 2026-10-09).
  Los veintitrés módulos construidos son ApiAccess, Audit, Authorization, Buyers, Catalog, Companies, Customers, Geography,
  Identity, Integrations, Loans, Messaging, Notifications, Pipeline, Pos, Quotations, Reporting, Shopify, Storage, Store, Suppliers, Support y Tenancy — cada
  uno con su `<Modulo>LayerTests.cs` en `tests/ArchitectureTests/` (`ApiAccessLayerTests` el de ApiAccess). `Platform` también existe, pero sin
  `PlatformLayerTests` (H26). Messaging no referencia
  Integrations, Customers ni Storage: lo que necesita entra por `IMessagingConnectionDirectory`,
  `IMessagingCustomerDirectory` e `IMessagingMediaStore`, con adaptadores en Bootstrapper.
- **La clave de una conversación es el BSUID, no el teléfono** (spec 2026-10-10). `wa_id` puede venir
  `null` y dos conversaciones con BSUID pueden compartir número (reciclado). Un cuerpo de webhook de prueba
  sin `from_user_id` se descarta en silencio —la entrega queda procesada y no aparece nada—: es el síntoma,
  no un bug de la ingesta.
- **El historial de coexistencia no pasa por `IngestOnceAsync`; `history_managed` decide si la regla de abiertas corre**
  (spec 2026-10-29). La apagan: el entrante en vivo (`InboundIngestion`), `Resolve` y `Reopen`, la respuesta desde QEP
  (`OutboundMessages`), toda asignación (`Conversation.Assign` —tomar, transferir—, el traspaso del bot y
  `TryAutoAssignAsync`), iniciar el asistente desde el hilo (`StartAutomationByMember`), el eco (`CoexistenceIngestion`, R11) y marcar leída o no leída (`MarkReadAsync`, `MarkUnreadAsync`,
  `MarkUnreadFromAsync`). La campaña no. Una acción nueva del asesor sobre una conversación tiene que decidir qué hace con la
  bandera: si no la apaga, la siguiente parte del historial le pisa el estado o los no leídos sin error (precedente: marcar
  leída faltaba y el contador volvía). Y el resumen de la importación sólo escribe si la parte cambia algo: subir `version`
  sin cambio le da un 412 al asesor.
- **Un mensaje del historial no trae `to`: la dirección sale de comparar `from` con el id del hilo** (producción, 2026-10-30).
  Los ecos sí traen `to`, y un fixture de historial que también lo llevaba pasó en verde y escondió el bug: 795 mensajes del
  negocio entraron como del contacto. Un fixture de Meta se copia de la forma real (`messaging.webhook_deliveries`), no de otro
  campo del webhook.
- **Un eco (`smb_message_echoes`) a un número desconocido crea la conversación `Resolved`, no `Open`** (P-5, owner
  2026-10-30). Abierta y sin asignar caería en la bandeja y en el triage del embudo (`TriageRules.IsPending` sólo mira
  abierta + cliente) sin que haya nada pendiente. No lleva `history_managed`, así que la regla de abiertas no la abre; la
  reabre el entrante en vivo con su `Reopened`. No la «arregles» a `Open` para que se vea el eco: el hilo está en
  «Resueltas». Lleva `started_by_echo`, que saca su creación de las sesiones del reporte (`MessagingReportSource`): sin eso, el
  eco más el `Reopened` contarían dos sesiones por una conversación. No lo cambies por «tiene entrante»: una conversación que
  abre un asesor con plantilla no tiene entrante y sí cuenta desde su creación (S3).
- **Lo importado se marca con `imported` (conversación y mensaje), y esa marca no se apaga nunca** (owner 2026-10-30). No es
  `history_managed`, que se apaga con cualquier acción del asesor: el reporte no puede depender de una bandera que un «marcar
  leída» borra. `MessagingReportSource` la lee por nombre en SQL crudo: una conversación importada no arranca sesión al crearse
  (la arranca su primer entrante en vivo, salvo que ya la haya arrancado un `Reopened`), y un mensaje importado nunca es entrante
  de arranque ni respuesta, porque su `created_at` es la hora de la importación y una parte tardía cae *después* del entrante en
  vivo. Para «primer entrante» sí cuenta, con `occurred_at`. Un camino nuevo que escriba historial en `messaging.messages` tiene
  que poner `imported = true`, o infla el reporte del día en que llega. Lo amarra `CoexistenceReportTests`. El `fromHistory` del
  resumen sale de `imported` y `last_inbound_at`, no de `history_managed`: tomarla, transferirla o marcarla leída apaga esa bandera
  sin abrir la ventana, y el aviso del compositor tiene que seguir mientras responder exija plantilla.
- **Un cliente puede ser `Incomplete`** (sin CUC, dirección ni clasificación): lo crean la ingesta
  de Messaging, la importación de pedidos de Shopify (`Customer.CreateIncompleteFromExternalSale`, sin BSUID ni IGSID)
  y `POST /pos/customers` (incompleto **con** documento y teléfono, sin CUC; spec 2026-10-14, POSC-05). Los de
  Messaging y Shopify no traen documento, los del POS sí: el pre-chequeo de identificación de Pos (el 422 con `existing`)
  depende de que `FindExistingIdentificationsAsync` devuelva también a los incompletos dueños de un documento; no lo
  "optimices" con `Completeness == Complete`. Todo código nuevo que lea `Cuc`, `IdentificationType`, `Address`,
  `Country` o `ClassificationId` de un `Customer` tiene que tratar el `null`; Quotations lo rechaza con
  `client_incomplete` **antes** que `client_cuc_missing`, y un reporte que agrupe por clasificación o ciudad
  filtra `Completeness == Complete`.
- **El webhook de Meta (`/api/webhooks/whatsapp`) no se prueba con el stub de auth.** En modo stub el
  middleware de CSRF ni se registra, así que la exención por ruta sólo se ve con
  `Authentication__UseDevelopmentStub=false`. Y su limitador `webhook` no es el `Public`: es concurrencia
  global con cola, porque un 429 hace que Meta reintente hasta 7 días. El resto está en
  [README § Mensajería](README.md#mensajería-whatsapp-cloud).
- **La expresión normalizada del documento vive en dos lugares y tienen que ser iguales carácter por carácter** (spec 2026-10-24).
  `CustomerIdentificationSql.NormalizedNumber` (las consultas de coincidencias y anomalías) y el literal de la migración
  `AddCustomerIdentificationNormalizedIndex` (`upper(regexp_replace(identification_number, '[^0-9A-Za-z]', '', 'g'))`): una migración
  no lee constantes. Si difieren, la consulta sigue dando el resultado correcto pero Postgres deja de usar
  `IX_customers_tenant_identification_normalized` y recorre la tabla, sin error ni prueba de resultado que se ponga roja. Sólo lo
  dice `TheMatchQueryUsesTheNormalizedIndex`; cambia las dos juntas, con una migración nueva para el índice.
- **Todo camino que inserte o reclame destinatarios de campaña respeta el candado de `MarkFrozenAsync`** (spec 2026-10-24 §4.4). El
  desduplicado `duplicate_contact` corre una sola vez, al terminar de congelar la audiencia, con la campaña `FOR UPDATE`;
  `AddRecipientsAsync` inserta con `FOR SHARE` y sólo si `audience_frozen_at IS NULL`, y el worker no reclama mientras sea `NULL`. Un
  camino nuevo que escriba en `campaign_recipients` fuera de ahí deja un `Pending` que nadie desduplica y manda dos plantillas al
  mismo número; y un destinatario sembrado a mano en una prueba nunca sale si su campaña no tiene `audience_frozen_at`.
- **Un fixture de `message_templates` sin `id` deja el catálogo vacío** (spec 2026-10-14). El catálogo de envío lee
  del espejo `messaging.message_templates`, que indexa por `meta_template_id`, y el refresco salta los elementos sin
  `id`: el síntoma es `messaging.template.not_available` en una prueba que antes pasaba, no un error del espejo. Y el
  webhook `message_template_status_update` hay que suscribirlo en la app de Meta: sin él los estados sólo cambian
  con el refresco.
- **Integrations no usa el `IAuditRecorder` ni el `IOutboxWriter` compartidos:** los dos están
  ligados a `TenancyDbContext`, y una segunda ligadura le robaría en silencio la auditoría o el outbox a
  Tenancy. Un módulo nuevo con auditoría atómica declara su propio puerto (`IIntegrationsAuditRecorder`,
  como `IIdentityAuditRecorder`). Y un consumidor de una conexión nunca referencia
  `Modules.Integrations.Application`: declara su puerto y el adaptador va en Bootstrapper
  (`IntegrationsLayerTests`).
- **El token de Shopify de una conexión lo refresca sólo `ShopifyTokenRefreshWorker`; el canje y el enlace adquieren y guardan
  uno nuevo, y los tres van bajo el candado `ShopifyTokenLock.Namespace`** (spec 2026-11-12). El canje
  (`CompleteShopifyInstall`) guarda el token en la instalación y, si la tienda ya tiene conexión, canjea con el candado de esa
  conexión; el enlace (`LinkShopifyStore`) refresca el de la instalación con el candado de la instalación y lo pasa a la
  conexión con el de la conexión. El refresh token rota: dos refrescos de la misma tienda, o un refresco y un canje o un
  reenlace a la vez, dejan guardado un par que Shopify ya retiró, y la tienda se cae una hora después sin error. El candado es
  un advisory lock de transacción de dos claves `(20261112, hashtext(id))` sobre el id de la conexión o de la instalación
  (`IIntegrationsUnitOfWork`): el worker usa la variante que no espera y salta la conexión; el canje y el reenlace, la que
  espera, **antes** de llamar a Shopify o de cargar la conexión. Ningún request tiene dos candados a la vez, y así no hay
  ciclo. El `Version` optimista no alcanza: el perdedor ya gastó el refresh token. Un camino nuevo que pida o refresque un
  token de Shopify —o que reemplace los secretos de una conexión que ya existe— toma el mismo candado; los consumidores sólo
  leen el guardado.
- **Rotar el client secret de la app de Shopify en el Partner Dashboard sin actualizar `Backend-prod` deja todas las tiendas en
  `token_expired` en el siguiente refresco** (spec 2026-11-12). El refresco se firma con ese secreto, Shopify responde un 4xx y
  el worker lo toma como definitivo; la única salida es que cada comercio vuelva a abrir la app. El secreto se rota en los dos
  lados en el mismo despliegue.
- **Una conexión puede publicar `integrations.connection-needs-attention.v1` dos veces seguidas, y el payload trae
  `failureCode`** (owner, 2026-11-12: el evento identifica el error). `ReportTokenExpiredAsync` publica también desde
  `NeedsAttention` con otro código (por ejemplo `webhooks_not_registered` → `token_expired`); desde `Paused` no publica, y ya en
  `token_expired` no hace nada. Hoy nadie consume ese evento: el primero que lo haga (un correo, un aviso) decide por
  `failureCode`, o le llegan dos avisos por la misma conexión.
- **Con el token de Shopify vencido, `ShopifyConnectionRef` sale `IsOperational = true` y `AccessToken = null`** (spec
  2026-11-12 §6.4, D7). No lo «arregles» a no operativa: `RedactShopAsync` daría la tienda por desinstalada y borraría su espejo,
  y la ingesta dejaría de importar, todo porque el worker iba tarde. Quien llame a la Admin API mira `AccessToken`, no
  `IsOperational` (`ReconcileStore` salta con 503 `sync_unavailable`; `ListOperationalAsync` la deja fuera). Lo amarran
  `ShopRedactWithAnExpiredTokenDeletesNothingOfTheCurrentConnection` y `OrdersTopicWithAnExpiredTokenStillImports`.
- **`token_expired` sale sólo de un 4xx salvo 429 en el refresco.** El gateway clasifica todo no-2xx que no sea 429 ni 5xx como
  `Rejected` (un 3xx incluido), y eso sirve al canje y al probador; el worker filtra por status (`IsDefinitiveRejection`) porque
  `token_expired` sólo se sale reautorizando. Un consumidor nuevo del refresco hace lo mismo.
- **Instagram es un canal de la misma bandeja de Messaging, no un módulo** (spec 2026-10-14). La conversación lleva
  `channel` (`WhatsApp` | `Instagram`) y en Instagram `user_id` es el IGSID numérico y `wa_id` es siempre `null`: todo
  código nuevo que empareje por teléfono tiene que tratar ese `null`. Las plantillas, las campañas y `POST /conversations`
  responden `messaging.channel_unsupported`; el catálogo de plantillas de una conexión de Instagram devuelve `[]`, no 422.
- **El webhook de Instagram (`/api/webhooks/instagram`) se firma con `Meta:Instagram:AppSecret`, no con `Meta:App:AppSecret`**
  (V-1): son dos secretos distintos aunque vivan en la misma app de Meta. El verificador acepta cualquiera de los dos para
  Instagram, así que una firma inválida en producción casi siempre es que falta `META_INSTAGRAM_APP_SECRET` en el pipeline.
  El `timestamp` de Instagram viene en **milisegundos**; el de WhatsApp, en segundos. Y los echo (lo que el negocio responde
  desde la app) llegan dentro de `messages` con `is_echo`, no en un campo aparte: la ingesta los deduplica por `mid` con lo
  que QEP ya mandó.
- **Un adjunto de Instagram no tiene `meta_media_id`: tiene `message_media.source_url`**, una URL pública con vencimiento
  que el worker de medios baja sin token. `MediaTransfer` decide el camino por `SourceUrl`; `InsertMediaAsync` acepta
  `meta_media_id = ''` si hay URL. Hacia afuera no hay subida a Meta: al adjunto saliente se le da una URL prefirmada de R2
  de 10 minutos (`IMessagingMediaStore.CreateDownloadUrlAsync`), y la nota de voz se transcodifica a WAV con Concentus
  (`ConcentusOpusTranscoder`) porque Instagram no acepta Opus. V-4 y V-8 (que Meta descargue la URL firmada y acepte el
  WAV) siguen **sin verificar contra Meta real**: el handoff dice qué mirar.
- **El payload de los eventos de Quotations es contrato con Pipeline** (spec 2026-10-13 §4.5).
  `QuotationLifecycleEventPublisher` escribe `quotations.quotation.sent.v1`, `…converted.v1` y
  `quotations.order.cancelled.v1` con campos camelCase, y `QuotationEventPayloads` los lee por nombre: renombrar
  un campo compila, pasa las pruebas de Quotations y deja los mensajes marcados como procesados sin efecto. Se
  cambia en los dos lados, y en `QuotationEventPayloadsTests`. Pipeline lee las conversaciones de Messaging por
  `MessagingDbContext` desde Bootstrapper (`PipelineConversationDirectory`), como Reporting lee Quotations: no
  por un puerto de Messaging.
- **Un pedido tiene datos propios y puede no tener cotización** (spec 2026-10-15). `Order` lleva cliente,
  asesor, cuenta de cobro, partes, líneas y totales; `QuotationId` es opcional y `Source` dice de dónde nació
  (`Quotation`, `Manual`, `Shopify`). Convertir **copia**: la cotización `Converted` queda congelada y editar un
  pedido editable edita `order_items`, nunca `quotation_items`. Un pedido sin sesión se crea sólo por
  `IExternalOrderWriter` (acepta cliente incompleto); el manual exige cliente completo con CUC. Los eventos
  `quotations.order.created.v1` y `order.cancelled.v1` llevan `source` y `quotationId` nullable: Pipeline los lee
  por nombre y guarda la foto en `pipeline.deal_orders` (un `order.created` **con** cotización no hace nada: ya
  actuó el `converted`). `Observaciones` del Excel sale de `Order.Notes`, que es un solo campo al
  convertir (el `notes` del cuerpo la reemplaza; `null` hereda las de la cotización; `""` la deja vacía; nunca se
  concatenan). Un `INSERT` a mano en `quotations.orders` (siembras, pruebas) tiene que llenar las columnas propias
  y copiar `order_items`/`order_parties`: son `NOT NULL` desde `AddOrderOwnData`.
- **El embudo de un tenant nace en su primer acceso, no al prender el módulo.** Tenancy no publica nada al
  activar una capacidad; `PipelineProvisioner` crea el embudo con `INSERT … ON CONFLICT (tenant_id) DO NOTHING`.
  Una consulta directa a `pipeline.stages` de un tenant que nunca abrió el tablero devuelve cero filas: no es
  un error.
- **Dueño de un negocio: la API exige membresía activa, el evento no.** Crear un negocio por la API con un
  responsable que no es miembro activo da 422 `pipeline.deal.owner_not_member`; el negocio que crea un evento
  de cotización conserva al asesor de la cotización aunque ya esté inactivo: la cotización existe y su
  asesor es un hecho, no una elección del usuario. No unifiques las dos reglas.
- **El orden del historial lo da el ordinal del dominio, no `occurred_at`.** `DealStageHistoryEntry.Ordinal` lo asigna
  el agregado (único por `(tenant_id, deal_id)`); dos cambios en el mismo instante se ordenan por él. Y la carrera
  del triage se cierra con el índice único `IX_triage_decisions_conversation_inbound`, que
  `PipelineUnitOfWork` traduce a `pipeline.triage.already_decided` **por nombre de índice**: no la muevas a una
  comprobación previa en el handler, que dejaría dos negocios.
- **EF ordena los `UPDATE` de un `SaveChanges` por clave primaria, no por el orden en que tocaste las entidades.**
  Con un índice único parcial que admite una sola fila (`IX_business_hours_tenant_default`, `WHERE is_default`), «quitar
  el predeterminado al anterior y ponérselo al nuevo» en un solo `SaveChanges` falla con 409 cada vez que el nuevo tiene
  un id **menor** (GUID v7 más antiguo): su `UPDATE … true` corre antes que el `… false` del anterior. La prueba que sólo
  mueve el predeterminado del más viejo al más nuevo pasa por casualidad. La solución es dos `SaveChanges` en una
  transacción explícita con el orden fijo (`BusinessHoursRepository.SwapDefaultAsync`, 2026-10-21), como
  `TryAutoAssignAsync`; y cualquier `SaveChanges` fuera de `MessagingUnitOfWork` pasa por `MessagingSaveChanges.SaveAsync`,
  que es donde viven las traducciones por nombre de índice —un `SaveChanges` directo del `DbContext` responde 500 donde
  tocaba 409 o 412. Un intercambio parecido en otro módulo (un «único activo» por tenant) tiene el mismo problema.
- **Dos eventos del mismo cambio en el hilo necesitan `occurred_at` distinto.** `MessageQueries` ordena por
  `occurred_at, id` y los GUID v7 **no** están ordenados dentro del mismo milisegundo, así que «pausó el asistente» salía
  antes que «tomó la conversación». `ConversationAssignmentSteps.CommitAsync` y `TryAutoAssignAsync` escriben el evento
  n-ésimo de un cambio en `now + n µs`; una prueba que compare los tipos sin orden esconde la regresión.
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
- **Un `FromSql` crudo debe listar todas las columnas mapeadas de la entidad.** El `SELECT … FOR UPDATE` de
  `QuotationSettingsStore` no se puede componer (EF lo envolvería en una subconsulta), así que EF no completa
  las columnas que falten: al agregar `freight_tax_rate_id` a `tenant_quotation_settings`, guardar el mínimo de
  compra dio 500 hasta que se sumó al `SELECT`. Al extender una tabla, busca los `FromSql` que la leen.
- **Una prueba de migración que baja a una versión vieja tiene que volver a la última antes de llamar a la
  API.** El modelo de hoy pide las columnas de las migraciones posteriores (`AddFreight`); sin
  `MigrateAsync(null)` al final, el `POST` de la prueba da 500 (`QuotationSettingsMigrationTests`).
- **Al agregar una columna al catálogo del Excel de pedidos, revisa los encabezados de las fijas de la semilla.**
  Una columna nueva cuyo encabezado choca con una visible del layout guardado nace oculta en silencio, sin
  error: la regla de choque está en
  [README § Columnas del Excel de pedidos](README.md#columnas-del-excel-de-pedidos-por-tenant-homologación).
- **El webhook de Shopify (`/api/webhooks/shopify`) se prueba con el stub de auth apagado**, igual que el de Meta: en
  modo stub `RequireCsrfHeaderMiddleware` ni se registra, así que una prueba con los headers `X-*` pasa aunque la
  exención esté rota (`ShopifyApiHarness.WithRealAuthentication`, `ShopifyWebhookApiTests.WebhookIsExemptFromCsrf`). El
  webhook se exime por el prefijo `/api/webhooks/` y el inicio B de la instalación por ruta exacta
  (`SignedAnonymousPaths`, duplicada de `IntegrationsEndpoints` y amarrada por `ShopifyInstallApiTests`); `install/complete`
  **no** está exento: lo llama el SPA con `X-Qep-Client` (2026-10-18, `0c6c06ae`). Una ruta anónima nueva que firme Shopify va a esa
  lista, con su prueba, o responde 403 sólo en producción.
- **Un pedido externo repetido lo frena `IX_orders_tenant_external_key`, no la búsqueda previa.** `ExternalOrderService`
  busca por la clave antes de crear, pero el webhook y la conciliación pueden llegar a la vez, y el worker puede caer entre
  crear el pedido y guardar la fila del espejo: lo único que deja un solo pedido es el índice único parcial
  `(tenant_id, source, external_key)`, que `QuotationsUnitOfWork` traduce **por nombre** a `order.order.external_key_taken`
  y el adaptador resuelve releyendo el existente (`AlreadyExisted`). No lo cambies por una comprobación en el handler. La
  clave es `"{shopId}:{shopifyOrderId}"` y no la conexión, para que borrar y volver a enlazar la tienda no duplique; un
  `INSERT` a mano en `quotations.orders` con `source = 'Shopify'` necesita una clave que no exista. Precedente (Review
  Focus 1 de `docs/superpowers/plans/2026-10-18-shopify-backend.md`): borrar la fila del espejo y recibir el pedido otra vez deja un pedido, y si llega cancelado lo anula —hasta
  `8b58fe9f` (2026-10-18) la fila nueva cancelada quedaba `Ignored` con el pedido vivo (`ReimportAfterMirrorRowDeleted*`).
- **Todo snapshot nuevo de una fila del espejo de Shopify pasa por `ShopifyOrderIngestion.ApplyAsync`.** El webhook, la
  conciliación, «Sincronizar ahora» y el reintento manual llaman al mismo método, que captura el estado anterior antes de
  `ApplySnapshot` y corre la reacción de pago y cancelación (§6.3) antes del guardado (2026-10-18, `82c4b2d9..f9b98fd1`, `WorkerFailedReactionIsSeenAgainOnRetry`). Llamar al importador o
  a `ApplySnapshot` directo compila y pasa las pruebas del importador, y pierde en silencio el pago o la cancelación.
  Desde el spec 2026-11-14 también detecta las ediciones: `IShopifyOrderEditDetector.DetectAsync` corre ahí, después del espejo
  y antes de la reacción, y sólo crea el cambio (no escribe en Quotations); `ApplyPendingAsync` aplica el pendiente de una tienda
  `Automatic` **después** del guardado y atrapa el `concurrency.conflict` de Quotations (el cambio queda `Pending`). Un cambio
  con algún grupo en conflicto no se aplica solo ni en `Automatic`: queda `Pending` para «Cambios por revisar» (spec §3). Un
  camino nuevo que dispare ediciones fuera de la ingesta rompe la regla del único camino y deja cambios sin espejo que los
  explique. Hay otros que escriben la fila **sin** traer un snapshot, y no son un bug: las decisiones sobre un cambio
  (`RecordApplied` al aceptar, `AdoptPayload` al rechazar o revisar), el trabajo de corrección de ciudad (`RecordProjection`) y
  la corrección del asesor (`SetQepAttributes`). Ninguno llama a `ApplySnapshot`.
- **La corrección del asesor no mueve `applied_projection`; la corrección de ciudad del sistema sí** (plan D3, spec
  2026-11-14 §5). La proyección es lo que QEP escribió desde Shopify: si la corrección del asesor la moviera, la siguiente edición
  de Shopify sobre ese grupo dejaría de ser conflicto y pisaría lo del asesor, también después del eco. Lo amarran
  `ACorrectionDoesNotMoveTheProjection`, `AfterAnEchoALaterShopifyEditOfThatGroupIsStillAConflict` y
  `TheEchoOfAPushedCorrectionCreatesNoChange`; el trabajo de sinónimos la mueve (`AnAppliedOrderCorrectionMovesTheProjectionAndEnqueuesAPush`).
  Una fila sin `applied_projection` (importada antes) toma la del pedido actual (D6, `AnOrderImportedBeforeTheSyncTakesAShopifyNoteEditWithoutConflict`).
  Y un eco que el cambio no aplicaba nunca mueve la proyección: sólo `SettleAsync`/`ApplyAsync` la mueven para los grupos que el
  propio cambio aplicaba.
- **`orderUpdate` reemplaza la dirección entera, y un envío sólo manda los grupos que se encolaron** (spec 2026-11-14, plan D12).
  `company` (donde muchas tiendas guardan el documento) y `countryCode` salen del **último** snapshot; sin ellos, corregir la
  ciudad borra la «Empresa» en Shopify. Mandar un grupo no encolado pisa la nota de Shopify con el `BuyerNotes` vacío de un pedido
  importado antes (sin backfill). Lo amarran `PushKeepsCompanyAndCountryFromTheLatestSnapshot` y
  `APushOnlySendsTheGroupsItWasEnqueuedFor`. Del `userError` de Shopify sólo se loguean los campos: el mensaje puede traer datos
  del comprador (`ThePushNeverLogsTheTokenOrTheUserErrorMessage`).
- **`current_quantity` ausente es `quantity`, y el descuento va sobre la cantidad original** (plan D2). Leer sólo la vigente deja
  en 0 toda línea de un payload que no la trae (las fixtures no la traen); calcular el porcentaje sobre la vigente cambia el precio
  neto de una línea a la que le quitaron unidades (`discount_allocations` reparte el descuento de la original).
- **El `PUT …/shopify/orders/{id}/corrections` enrola el `QuotationsDbContext` del request en la transacción de Shopify**
  (`ShopifyOrderCorrectionWriter`, como `CatalogProductDeletionCleanup`): el pedido, `qep_attributes` y el envío encolado
  quedan juntos o no quedan (`AShopifyFailureAfterTheOrderWriteRollsBackBoth`). Después del commit ese contexto apunta a una
  transacción terminada: nada en el scope vuelve a usarlo (el handler arma la respuesta con el estado que devolvió
  `CorrectAsync`, sin releer el pedido). Un camino nuevo que corrija un pedido de Shopify y encole su envío pasa por ahí, nunca por `IShopifyOrderWriter`.
- **Un envío a Shopify `Held` nunca lo reclama el worker; sólo lo mueven los dos endpoints de sincronización** (owner,
  2026-11-14). Una corrección no se pierde por la conexión: guardada con la conexión no operativa (`connection_inactive`), con la
  conexión caída al mandar (`connection_missing`) o con `access_denied`, el envío queda retenido con el motivo en `failure_code`.
  `ListPendingAsync` y `TryClaimAsync` filtran `status = 'Pending'`, así que reactivar la conexión no lo manda: lo sueltan
  `POST …/orders/{id}/push` y `POST …/stores/{connectionId}/pushes/release`. `IX_order_pushes_mirror_pending` cubre `Pending` y
  `Held` (un solo envío abierto por pedido) y se fusiona igual en los dos; si cambias el filtro, cambia también `FindOpenAsync`. Un
  motivo nuevo de la conexión va a `HoldAsync` del procesador, no a `FailAsync`: un `Failed` no se puede volver a mandar.
- **Límite conocido de la detección de ediciones** (spec 2026-11-14): dos snapshots del mismo pedido que tocan el mismo grupo,
  intercalados por milisegundos (B reemplaza a A antes de que la escritura de A en Quotations confirme, y el segundo guardado de A
  falla), pueden dejar la proyección vieja y un `qep_changed` falso en la edición siguiente. No se cerró; la alternativa es un
  candado advisory por fila del espejo alrededor de la detección y la aplicación. No lo «arregles» moviendo la proyección con
  la corrección del asesor.
- **`ShopifyLayerTests` prohíbe que el módulo Shopify referencie módulos de negocio:** Application sólo ve Tenancy y Audit.
  La conexión (Integrations), el pedido (Quotations), el cliente (Customers), el producto (Catalog) y la ciudad
  (Geography) entran por los puertos de `ShopifyPorts.cs`, con adaptador en `src/Bootstrapper/Shopify*.cs`. El costo son
  dos copias que se cambian juntas, cada una con su prueba de paridad en `Bootstrapper.UnitTests`, que ve los dos módulos:
  `ShopifyWebhookTopics` vive en Integrations.Application (los que el enlace registra) y en Shopify.Application (los que el
  worker atiende), amarradas por `ShopifyWebhookTopicsParityTests` (2026-10-18, `7b83a958`), y
  `ShopifyCashOnDeliveryGateways.Normalize` copia `CarrierName.Normalize`, amarrada por `ShopifyNormalizationParityTests`
  (2026-10-18, `e157d9c2`).
- **`shopify.custom_line_mappings` es contrato con `lulo-crm-migrator`, que la siembra por SQL** (`INSERT … ON CONFLICT
  (connection_id, title) DO NOTHING`, spec 2026-11-13). Renombrar una columna (`id`, `tenant_id`, `connection_id`, `title`,
  `variant_id`, `created_at`, `created_by`) o el índice `IX_custom_line_mappings_connection_title` compila, pasa las pruebas de
  este repo y rompe la siembra de la migración de Lulo v1; se cambia junto con el spec del migrador. `title` va recortado y
  `created_by` es id de **membresía** activa, no de usuario (como `stores.updated_by`): el migrador siembra la del dueño. Y
  `IsCustom` exige las llaves de ids presentes en `null` **y que la línea no tenga SKU**: un fixture sin `product_id`/`variant_id`
  no es una línea personalizada, y con «ausente = nulo» todo pedido de los fixtures quedaría en espera. Una variante borrada en
  Shopify deja `variant` y `product` en `null` pero conserva el SKU: esa línea es de producto y nunca casa por nombre (los dos
  parsers y `ShopifyOrderImporter.CustomTitleOf` aplican la misma regla). El 409 sale sólo del índice
  (`ShopifyUnitOfWork` lo traduce por nombre): no lo muevas a una consulta previa en el handler.
- **`develop` no va a `main` sin el frontend de Shopify** (2026-10-18). `shopify` está en `DefaultForNewTenants`
  (`TenantModuleKey.cs`, decisión del owner del 2026-10-15) y en `Production` la app está configurada a la fuerza —sin ella
  el pod no arranca—, así que el catálogo de Integraciones de todo tenant nuevo trae la tarjeta Shopify con
  `onboarding.kind = "ShopifyOAuth"`. El frontend de `develop` (`c940537`, `provider-catalog.tsx`) trata todo `kind` que no
  sea `Form` ni `InstagramLogin` como WhatsApp: el «Conectar» de Shopify lleva a `/integrations/whatsapp/new`, el Embedded
  Signup de Meta. Pasa igual si `develop` llega a `main` por otra razón (un hotfix, otra feature): antes de llevarlo, el
  frontend de Shopify tiene que estar listo para desplegarse junto. La alternativa es del owner: sacar `shopify` de
  `DefaultForNewTenants` hasta que exista el frontend (una línea y su prueba, más la entrada de `pos` de este archivo, que
  cuenta siete módulos, y el README). El paso a paso está en el README, § Integración con Shopify, «Despliegue».
- **Un token del pipeline sin resolver llega literal, no vacío.** Si la variable no está en el grupo `Backend-prod`, la
  plantilla deja `#{NOMBRE}#` en el ConfigMap o el Secret, y un `Require` que sólo mira `IsNullOrWhiteSpace` lo da por
  configurado: el pod arranca y rechaza cada firma (es el síntoma que la entrada de Instagram atribuye a
  `META_INSTAGRAM_APP_SECRET`). `ShopifyAppOptionsValidator` trata `#{…}` como ausente en `Production` desde `f72c7d1b` (2026-10-18),
  como `DeploymentInfo.Normalize`; un secreto requerido nuevo lleva la misma regla.
- **La variante por defecto lleva el id del producto, y el relleno de `variant_id` depende de eso** (spec 2026-10-20 §8).
  `Product.Create` le da a la primera variante el id del producto —también al crear con opciones—, y `AddProductVariants`
  hizo lo mismo con cada producto que existía. Por eso `AddVariantToLines` (Quotations) y `AddVariantToSaleLines` (Pos)
  rellenan con `variant_id = product_id` sin consultar `catalog`, que es otro esquema y otro `DbContext`. No cambies
  `Create` para que estrene un id: las pruebas que mandan el id del producto como `variantId` dejan de valer en silencio.
  Al revés tampoco sirve: `variant_id = product_id` **no** dice que la variante sea la por defecto (un producto con
  opciones también tiene una con ese id); eso lo dice `OptionIds` vacío, y fuera de Catalog no se ve.
- **Un `INSERT` a mano en `quotation_items`, `order_items` o `sale_lines` necesita `variant_id`.** Es `NOT NULL` sin default
  desde `AddVariantToLines` y `AddVariantToSaleLines`, y va junto con el `product_id` **de esa variante**. Del lado de
  Catalog, `catalog.products` ya no tiene `code` y `catalog.product_prices` se une por `variant_id`: un SQL que lea el
  catálogo pasa por `catalog.product_variants` (precedente: `ExportLoadSeeder`). Las pruebas de migración que siembran
  **antes** de esas migraciones no lo llevan: la migración lo rellena.
- **Un cuerpo crudo con `productId` ya no sirve en las líneas.** Las de cotización, pedido y POS (`AddQuotationItemRequest`,
  `BatchQuotationItemAdditionRequest`, `QuotationEditItemRequest`, `OrderItemAdditionRequest`, `OrderEditItemRequest`,
  `PosSaleLineRequest`, `PosPreviewLineRequest`) llevan `variantId`, y la copia de escalas `sourceVariantId` /
  `targetVariantIds`. Un objeto anónimo con `productId` se deserializa con `VariantId = Guid.Empty` y responde `422
  validation.failed` por el `VariantId` vacío, con la clave según la ruta: `VariantId` en `POST /quotations/{id}/items`,
  `ToAdd[i].VariantId` en el batch y en `POST /orders/{id}/items`, `Items[i].VariantId` en las listas completas
  (alta, guardado y preview de cotización y pedido) y `Lines[i].VariantId` en el POS. Una variante repetida en una lista
  completa sale en `errors.Items`, sin posición. El síntoma es una prueba que esperaba `Created`, no un error de nombre
  de campo. Los records tipados se arreglan solos; el barrido es por `new { productId` y por JSON literal.
- **`CatalogVariantUsageChecker` lee tablas de Quotations y Pos por nombre.** Es SQL crudo en Bootstrapper sobre
  `quotations.quotation_items`/`order_items` (unidas a `quotations`/`orders` por el tenant) y `pos.sale_lines` (unida a
  `pos.sales`), y corre en la conexión del `PUT` de producto para quedar dentro de su transacción. Renombrar
  `variant_id` o una de esas tablas compila, y el `GET` y el `PUT` de producto responden 500 (`42703`). Sólo
  `ProductVariantDeletionApiTests` usa un doble del puerto; el resto de las pruebas de Catalog que leen o guardan un
  producto corre el checker real contra el esquema migrado y se cae con ese 500, que no se parece a la causa. Se cambia
  junto con el checker y sus pruebas: `CatalogVariantUsageCheckerTests` (que arma réplicas mínimas de las tablas, a
  mano: no siguen solas a la migración) y las dos `TheCatalogUsageQueryFindsTheBackfilledVariant`
  (`VariantLinesMigrationTests`, `SaleLinesVariantMigrationTests`), que resuelven el checker real del contenedor y lo
  corren contra el esquema de las migraciones.
- **`CustomerMergeReferences` escribe en ocho esquemas por nombre.** La fusión de clientes conoce por nombre las tablas de
  quotations, pos, pipeline, messaging, billing, loans, support y shopify: es SQL crudo en Bootstrapper sobre
  `quotations.quotations`/`orders.client_id`, `pos.sales.customer_id`,
  `pipeline.deals`/`deal_quotations`/`deal_orders`/`triage_decisions.customer_id`, `messaging.conversations.customer_id`,
  `messaging.campaigns.audience_customer_ids`, `billing.services`/`charges`/`customer_profiles.customer_id`,
  `loans.loans.customer_id`, `support.tickets.customer_id` (sin subir `version`, como el préstamo) y `shopify.orders.qep_customer_id`, sobre la
  conexión del `CustomersDbContext` para quedar dentro de su transacción. Renombrar una de esas columnas compila y la fusión
  responde 500 (`42703`); como todo es una transacción, no fusiona nada. Lo amarran `CustomerMergeReferencesTests` (réplicas
  hechas a mano: no siguen solas a una migración) y `CustomerMergeAcrossModulesApiTests.TheRealAdapterRunsAgainstTheMigratedSchema`.
  Una tabla nueva que guarde un id de cliente vivo entra en `MoveAsync` y en esas dos pruebas, o la fusión la deja apuntando a un
  cliente inactivo. Y `merged_into_id` apunta siempre a un no fusionado: quien lea un fusionado sigue un solo salto.
- **`CatalogProductDeletionCleanup` escribe en `store.order_request_lines`/`order_requests` por nombre y enrola el
  `QuotationsDbContext` en la transacción de Catalog.** El borrado permanente de un producto (`DELETE .../catalog/products/{id}`)
  vive en Bootstrapper por la misma razón que `CustomerMergeReferences`: borra las líneas de las solicitudes de la tienda en
  `New` con SQL crudo (`request_id`, `variant_id`, `status`, `version`) y llama a `IQuotationVariantDeletion` de Quotations sobre
  la conexión del `CatalogDbContext` (`SetDbConnection` + `UseTransactionAsync`), todo envuelto en `CustomersWriteRaces.RunAsync`
  para que un deadlock o una falla de serialización sea 412 y no 500. Renombrar una de esas columnas compila y deja el `DELETE` del
  producto en 500 (`42703`/`42P01`); como es una transacción, no queda nada escrito. Lo amarran `ProductDeletionCleanupTests`
  (Quotations) y `ProductDeletionStoreCleanupTests` (Store), que resuelven el adaptador real y lo corren contra el esquema migrado.
  Tras confirmar, ese `QuotationsDbContext` queda apuntando a una transacción terminada: nada más en el scope debe usarlo.
- **El `Version++` del fusionado es lo que impide fusionar al mismo cliente en dos que quedan.** `ApplyMergedInto` pasa por
  `Touch` y sube la versión del fusionado, así que dos fusiones simultáneas del mismo cliente hacia distintos que quedan
  chocan por concurrencia optimista, y `merged_into_id` lo saca de toda fusión posterior (`customers.merge.already_merged`).
  No quites el `Touch` ni marques fusionados con un `ExecuteUpdate` que se salte el agregado.
- **`MarginReportSource` lee `quotations.order_items`/`orders` (`unit_cost`, `paid_at`), `pos.sale_lines`/`sales` y `catalog.products`/`product_variants` por nombre.** Renombrar una columna compila y deja `/reports/margin` en 500; lo amarra `MarginReportApiTests`. Una vía nueva a `FullPaymentReceived` pasa `OrderLineCosts` al dominio: si no, el pedido cuenta en el reporte con líneas «sin costo».
- **`OrdersReportSource` lee `quotations.orders` y `pos.sales` por nombre** (spec 2026-10-23). Es SQL crudo en
  Bootstrapper, un `UNION ALL` de las dos tablas (`ReportRowsSql`), porque son de `DbContext` distintos y EF no los une.
  Renombrar una columna de cualquiera de las dos compila y deja `/reports/orders` y `/summary` respondiendo 500: lo amarran
  `OrdersReportPosApiTests` y las demás pruebas `OrdersReport*`, que corren contra el esquema migrado. Un estado nuevo de
  `PosSaleStatus` u `OrderStatus` que no sea venta tiene que entrar en `is_void`, o empieza a sumar en el resumen sin
  error. Y un filtro opcional nuevo se omite del texto cuando no viene, no se manda como parámetro nulo: PostgreSQL no
  tipa un `NULL` suelto (`could not determine data type of parameter`).
- **`QueueMetricsSource` lee seis tablas por nombre** (observabilidad fase 2, 2026-10-30): `messaging.webhook_deliveries`,
  `automation_inbox`, `message_media` (unida a `messages`), `campaign_recipients` (unida a `campaigns`),
  `platform.outbox_messages` y `shopify.webhook_deliveries`. Es SQL crudo en `src/Bootstrapper/Observability/`. Renombrar una
  columna compila y deja esa cola sin dato; si es `webhook`, dispara `QepQueueMetricsMissing`. Una cola nueva entra en
  `QueueNames`, en `QueueMetricsSourceTests`, en el dashboard y en las reglas de alerta. La edad mira sólo `attempts = 0`, y la de
  `automation` repite el `NOT EXISTS` de `AutomationInbox.ClaimAsync`: si cambias el reclamo, cambia también la lectura. En una
  prueba, el registro del tenant deja eventos sin despachar en `platform.outbox_messages`: ciérralos antes de esperar una cola vacía.
- **Borrar una variante es explícito: `deletedVariantIds`. Omitirla del `PUT` responde `catalog.variant.missing_in_request`.**
  No lo «simplifiques» a «la que no viene se borra» (era lo que pedía el plan, y el spec §6 lo corrigió): la tabla de
  variantes llega a 2.048 filas y el formulario la pagina, así que una fila ausente puede ser una página que no se
  mandó. La listada se borra si ninguna línea la usa y se desactiva si alguna sí; borrarla se lleva su historial de
  precios por `CASCADE`.
- **La regla de recorte de la foto del nombre vive en `BuildingBlocks.Domain/SnapshotText.cs`.** El nombre visible de una
  variante pasa de los 200 de `product_name` (producto de 200 más hasta tres valores de 60), y
  `QuotationProductSnapshot` y `PosSaleLine` lo recortan con `SnapshotText.Fit` (200 con «…», sin partir un par
  sustituto). No copies la regla en un módulo —la copia de Pos ya se retiró una vez—: Quotations y Pos no se ven entre
  sí, y dos copias divergen sin que nada se ponga rojo. Ensanchar la columna es una migración en cada módulo más su
  constante (`QuotationProductSnapshot.NameMaxLength`, `PosSaleLine.ProductNameMaxLength`).
- **`Messaging:Automation:Enqueue` está prendido (plan 2) y es lo único que hace que el bot conteste.** Sin el flag el webhook no
  encola y el bot queda mudo sin error. La cola se purga (filas procesadas a los 7 días, `Automations:Workers:InboxRetentionDays`) y
  antes de desplegar la migración `AddAutomationEngine` en un ambiente donde alguien puso a mano `automation_status` ≠ 'None', la
  migración normaliza esas filas a 'None'.
- **El motor sólo responde una conversación `Open`, sin asesor y con el asistente `Active`; `IBotOutbound` lo vuelve a verificar al
  enviar** (`RunProcessor.DecideAsync`): entre el claim del inbox y el envío un asesor puede haber tomado la conversación.
- **Una fila de `automation_inbox` tiene tipo** (spec 2026-11-03). `ReleaseRun` es la señal de que una persona resolvió, tomó, transfirió o
  devolvió la conversación: `RunProcessor` sólo cierra la corrida viva (`Interrupted`, `human_took_over`) si arrancó antes de la fila y
  `HumanTookOver` sigue siendo verdadero, y nunca arranca ni avanza nada. Dentro de `Engine`, una fila sin mensaje sigue siendo un arranque
  manual: todo lector nuevo usa `LEFT JOIN` y filtra por `kind`. Una acción humana nueva sobre una conversación con asistente decide si
  encola la señal, en el handler y no en el agregado (el bot también resuelve). Una fila del inbox puede existir sin mensaje —un arranque
  manual (spec 2026-10-31) o una señal—, y por eso `run_units.inbound_message_id` y `conversation_runs.last_inbound_message_id` pueden
  apuntar a una fila y no a un mensaje; el camino manual del motor es `ResumeAsync`, no el de un entrante. Un `INNER JOIN` del inbox a
  `messages` pierde esas filas en silencio: el arranque se encola, se marca procesado y el bot no contesta, sin error. La regla del
  `LEFT JOIN` cubre también a quien lea esas dos columnas: tiene que tratar el mensaje `null`.
- **`Conversation.ContinueAutomation` no escribe `AutomationResumedAt`, y esa ausencia es todo el mecanismo** (spec 2026-11-01). Sellarlo
  hace que `BotAttendance.HumanTookOver` dé verdadero, la corrida termine `Interrupted` y el flujo reinicie: justo lo que `automation/continue`
  evita. No hay columna ni bandera de «continuada». Por la misma razón `AutomationPauseReason = Manual` **no** prueba que ningún asesor
  haya hablado: tomar, transferir o responder sobre una pausa no cambia el motivo, y después de liberar queda igual. Se mide con los
  eventos del hilo desde `AutomationPausedAt` (`WasTakenSinceAsync`); una regla nueva sobre «sin asesor» tiene que hacer lo mismo. Y la
  decisión está en un solo lugar para el resumen y el POST (`ManualRunStarter.CanContinueAsync`): si el modal y el botón discrepan,
  promete lo que no hace.
- **Un entrante que llega fuera de orden no se encola para el bot** (`InboundIngestion.ShouldEnqueue`, 2026-10-23). La cola
  ordena por llegada y Meta reintenta una entrega fallida hasta horas después: el 2026-10-22, con QEP sin recibir unos diez
  minutos, un «hola» de las 21:42 llegó a las 22:01 detrás de uno de las 21:56 que el bot ya había contestado, y el bot mandó
  el mismo mensaje otra vez sin que el cliente escribiera. Por eso se compara su `occurred_at` contra el `last_inbound_at`
  **de antes** (el CTE `old`; el `SET` ya lo pisó con `GREATEST`). No lo muevas al motor: ahí sólo cubriría uno de los tres
  caminos. Una prueba que postee webhooks con timestamps decrecientes ya no ejerce el bot desde el segundo.
- **El `clientId` de un mensaje del bot no depende del `attempts` de la fila.** Es `DeterministicClientId.For(runId,
  baseStepNumber, effectIndex)` y el id de una corrida nueva sale del id del mensaje del inbox (`RunIdFor`), así un reintento de la
  fila —o un `Start` que se deshizo— reusa corrida y `clientId`s y Mensajería, idempotente por `clientId`, no duplica (P2-R5). El
  plan escribía `(runId, nodeId, índice, attempts)`: con `attempts` el reintento SÍ reenvía. El motor sube `StepNumber` en cada bloque
  que entra, también al reenviar uno que espera, para que dos unidades confirmadas nunca compartan base.
- **El cliente HTTP del bloque se registra en Bootstrapper, no en `AddAutomationsInfrastructure`**, porque su handler anti-SSRF
  (`ExternalApiUrlPolicy.CreateHandler`) es de `Integrations.Infrastructure`. Sin redirecciones, sin proxy, `RemoveAllLoggers()`: el
  log de `HttpClient` escribiría la URL con la query del cliente. Cambiar el handler sin pasar por ahí deja a cualquier admin con un
  proxy hacia la red interna; `CompositionRootTests` amarra que el handler sea el de la política.
- **Una llamada HTTP del bloque no se puede deshacer, y la unidad sí.** Si el proceso muere entre la llamada y el commit, el
  reintento vuelve a llamar (P2-R37; la API de Clasic no tiene llave de idempotencia). Si `http_calls` no se puede escribir, se loguea
  y se sigue: lanzar deshace la unidad y repite la llamada.
- **Nada de lo que responde una API externa, sus headers ni la clave sale a logs, auditoría ni `http_calls`** (D-H5, D-H6). La
  prueba `TheKeyNeverAppearsInLogsAuditOrHttpCalls` corre el host en `Trace`, porque el log de headers de `HttpClient` es `Trace`:
  a `Information` quitar `RemoveAllLoggers()` pasaría sin verse. `authHeader` no puede ser un header reservado (`Host`, `Content-*`,
  `Proxy-*`…, P2-R30); la lista vive en `ExternalApiUrlPolicy.IsReservedHeader` y copiada en el patrón del catálogo
  (`IntegrationProviders.ExternalApi`), amarradas por una prueba: se cambian juntas.
- **Un valor del cliente en la ruta del bloque HTTP se escapa y se vigila** (P2-R31/R32/R34): `Uri.EscapeDataString`, y un segmento
  `.`/`..` (también `%2E%2E`) o un valor vacío o de sólo `/` y `\` (P2-R46) rechaza la llamada (`path_dot_segment`,
  `path_value_empty`) sin hacerla, porque
  `System.Uri` resuelve `..` aun codificado y un `{id}` vacío llega a la colección con un `PUT`. El cuerpo se interpola
  JSON-aware (P2-R11): dentro de un string, el texto escapado; suelto, el literal JSON (`null` si falta la variable).
- **El mapeo de la respuesta no usa la gramática de las variables.** `mapping.from` acepta `$`, `$[0]`, `[0].id` y rutas con puntos
  (`JsonPath.IsValidResponsePath`, P2-R36): Clasic responde arreglos en la raíz (`[].id`, Anexo C). Un `from` que no cumple es
  `http.mapping_path_invalid` al validar (antes guardaba `null` en silencio). Y el validador detecta `{referencia}` con un patrón
  más laxo (`VariableToken`: `{_x}`, `{a..b}`) que `TemplateText`, que es lo que el motor reemplaza.
- **Esperas con tope, pero el lease de 30 s no acota la unidad.** El adjunto del bloque HTTP se espera 20 s (4 × 5 s, un presupuesto
  por bloque para la url y el archivo), no los 60 s de D-H8; el motor corta en `EngineReentries.Max = 25` reentradas por unidad (HTTP
  u horario que vuelven a entrar al motor) con `automations.run.loop_detected`, igual que el simulador (P2-R40); y un solo
  `ClaimAsync` reclama una fila, para que cada unidad arranque con el lease entero (P2-R24). Aun así el peor caso (25 × (20 + 10) s
  más 15 s por envío) pasa el lease: por eso el candado de la conversación es `pg_try_advisory_xact_lock` y, ocupado, la fila se
  reprograma a 5 s (P2-R45) en vez de bloquear al worker; cada reprogramación gasta un intento (README, «El lease de 30 s no acota la
  unidad»). No vuelvas al `pg_advisory_xact_lock` que espera.
- **`http_calls.outcome` mide 24, no 16** (spec §4.2 dice 16; `response_too_large` ya tiene 18, migración `WidenHttpCallOutcome`).
  Un outcome nuevo más largo es otra migración.
- **La causa de un traspaso del bot vive en el evento `AutomationPaused` (`details.handoff`), no en la corrida** (spec 2026-10-24).
  `conversation_runs.failure_code` sólo la tiene cuando falló; un bloque «Pasar a un asesor» alcanzado deja `HandedOff` sin código. Un
  `connection_unavailable` del bloque HTTP lleva el motivo en `ErrorCode` (`secret_unreadable`, `not_chosen`, …) y sigue sin fila en
  `http_calls`. `IIntegrationConnections.ResolveAsync` devuelve `ConnectionResolution`: un consumidor nuevo lee `.Connection` y decide qué
  hace con `.Unavailable`, y los códigos `snake_case` los arma sólo `ConnectionReasonCodes` (Bootstrapper). `HandoffCauseKind`
  (Automations) y `AutomationHandoffKind` (Messaging) se cambian juntos: los amarra `HandoffCauseKindParityTests`.
- **El simulador corre el borrador, no la versión publicada, y `Start` exige cuerpo** (`{ "liveHttp": false }`; sin cuerpo no es lo
  mismo). Con `liveHttp` un bucle de HTTP puede tardar `25 × timeoutSeconds` en un solo request. El bloque HTTP del simulador usa
  `JsonPath.SelectResponse`, el mismo mapeo del ejecutor.
- **Las plantillas del sistema de Automatizaciones se cargan al arrancar**: un JSON embebido roto tumba el host (a propósito).
- **`GET /customers` de Clasic responde `items`/`totalCount`, no `data`/`total`, y el cliente se busca con `?document=`, no con
  `?identificationNumber=`** (plan 4, 2026-10-23). `identificationNumber` es la búsqueda del admin y casa por subcadena: con «1» el
  bot leería el cliente, las direcciones y la orden de otra persona; `document` es igualdad exacta. `?phone=` recibe el `wa_id`
  completo (`57…`) y Clasic recorta a diez dígitos; con el valor vacío responde **400 a propósito** (QEP manda `telefono = ''` sin
  `wa_id`, y un filtro vacío ignorado le aceptaría la política a un cliente cualquiera): ese 400 va por `error` al asesor, no se
  «arregla» en Clasic. Y `/services` y `/departments/*` son públicas, así que la ruta de prueba de la conexión «API externa» tiene
  que ser una protegida (`/failures`, D-K10): con una pública, «Probar» da OK con cualquier clave.
- **En una Lista dinámica, `x.id` es el id de la fila (texto) y `x.item.id` es el valor del elemento.** Un cuerpo JSON hacia
  una API que espera un número lleva `{x.item.id}` sin comillas: `"{x.id}"` compila, pasa las pruebas con el fake y Clasic
  responde 500 (`invoiceItems[0].id` como string; verificado el 2026-10-23 en la revisión final del plan 4). En una query de
  URL sí va `{x.id}`, porque es texto.
- **El respaldo colombiano del formato regional vive en tres copias que se cambian juntas** (spec 2026-10-23, D-R5), porque los
  módulos no se ven entre sí: `RegionalNumberFormat`/`RegionalDateFormat` (`Modules.Tenancy.Application`),
  `EmailRegionalFormat.Colombian` (`Modules.Notifications.Application`, que no ve Tenancy) y `QuotationPdf.LegacyNumberFormat`
  (dominio de Quotations: el formato con el que se supone generada una fila de `quotation_pdfs` con `number_format` nulo). Cambiar
  `TenantNumberFormats.CommaDecimal` sin la del dominio compila y regenera en silencio todos los PDF guardados de los tenants
  colombianos en el siguiente export. Las amarran `EmailRegionalFormatTests.ColombianMatchesTheTenancyFallback` y
  `QuotationPdfTests.ALegacyPdfWithoutFormatCountsAsCommaDecimal`. Y el historial de cambios es texto congelado: un tenant que
  cambia de formato ve las filas viejas con el anterior, y eso no es un bug.
- **`OrdersExportProcessor` no lee el formato del tenant, a propósito** (D-R8): la hoja del pedido la importa el ERP.
  «Unificarlo» con el Excel de cotizaciones compila, pasa sus pruebas unitarias y rompe la importación en silencio. La amarra
  `OrderExportApiTests.TheErpSheetIgnoresTheTenantRegionalFormat`.
- **`other` de Botones/Lista existe sólo con `fallback = otherOutput`** (spec 2026-10-25 D-3): `FlowNodeKinds.OutputsOf` la omite con
  `repeat` y `handoff`, el motor no la sigue aunque la versión publicada la tenga, y `FlowNormalizer` la quita al guardar. Al agotar los
  intentos sin salida conectada manda `Chatbot.ExhaustedAction` (`Restart` por defecto), no el flujo. El traspaso a un miembro pasa por
  `IMessagingAssignees.CanReplyAsync` en el adaptador: si no puede, cae sin asignar sin fallar.
- **`customers.data_consents` es prueba legal: sólo se inserta** (spec 2026-10-26 D-5). Nada la edita ni la borra salvo la fusión,
  que mueve `customer_id` (`CustomerMergeStore.MoveOwnedAsync`), y el borrado de clientes, que **no la borra**: la tabla ya no tiene
  FK a `customers` y cada fila guarda `customer_name` y `customer_phone_e164`, una foto tomada al borrar (spec 2026-11-10); el estado vigente es la última fila por `(occurred_at, sequence)`.
  Una respuesta es una fila: el reintento de una unidad del motor lo frena `IX_data_consents_reply_message`, que `CustomersUnitOfWork`
  traduce por nombre; no lo cambies por la consulta previa. Los CHECK de la tabla exigen identidad de canal en lo que escribe el bot y
  en `Revoked` + `ThisConversation`, y la prohíben en `Revoked` + `Other`: un `INSERT` a mano tiene que cumplirlos. El texto que se
  guarda es el que salió (`RunState.ConsentPrompt`, columna `conversation_runs.consent_prompt`), no la plantilla del bloque.
  **Dos relojes:** la fila del bot lleva la hora de Meta (truncada al segundo) y la revocación la del servidor; ordenar por
  `(occurred_at, sequence)` es lo buscado, nunca por `sequence` sola. **Un toque vale sólo para la pregunta que cita:** los ids
  `accepted`/`rejected` son fijos y WhatsApp deja tocar los botones viejos, así que un toque cuyo `reply_to_message_id` no es el
  `ConsentPrompt.PromptMessageId` vigente es una respuesta inválida (`FlowEngine.IsStaleConsentTap`); la fila guarda el citado en
  `replied_to_message_id`. **Deploy y rollback:** el kind `dataConsent` es sólo hacia adelante —un backend viejo no lee un flujo
  publicado que lo tenga— y el `Down` de `AddCustomerDataConsents` borra la prueba legal: nunca se corre. El rollback es revertir el
  código y dejar la tabla.
- **«Política de datos» tiene tres modos para «No acepto»** (spec 2026-10-26 repetir): `onReject` = `repeat` | `rejectedOutput` (default de todo bloque viejo) | `handoff`. `rejected` existe sólo con `rejectedOutput`: `OutputsOf` la omite y `FlowNormalizer` la quita. En `repeat`, el contador es `RunState.ConsentRejections` (columna `conversation_runs.consent_rejections`), no `Attempts` ni `ConsentPrompt`: vuelve a 0 en `Go`, `Leave` y `Restart`, nunca en `Resend`. En los dos modos sin `rejected`, todo camino que hoy saldría por ella (intentos agotados, cliente sin registrar) traspasa con `onRejectMemberId`, sin leer la regla de agotamiento del chatbot. Los modos son sólo hacia adelante: tras un rollback del backend, el motor viejo sigue la `rejected` ya quitada como `null` y termina con `EndRun(OutputUnconnected)` ante «No acepto» o el agotamiento, así que toda versión publicada en `repeat` o `handoff` hay que republicarla en `rejectedOutput`.
- **El sinónimo de una ciudad casa después de los dos nombres, y confirmar uno igual a una ciudad real es 422** (spec 2026-10-26).
  `CityRepository.MatchByNameAsync` compara el nombre, luego el oficial del DANE y sólo si ninguno casa busca el sinónimo vivo:
  una fila de sinónimo con el texto de una ciudad real —sólo llega por SQL— nunca le gana. `resolve` e `impact` lo rechazan con
  `geography.synonym.matches_city`. Si inviertes el orden, el sinónimo le quita el pedido a una ciudad legítima sin error. Lo
  amarran `ARealCityNameWinsOverASynonymWithTheSameText` y `CityOperatorApiTests`.
- **`CityCorrectionInsights` lee `quotations`, `customers`, `shopify`, `tenancy` e `identity` por nombre.** Es SQL crudo en
  Bootstrapper que alimenta los impactos y los contadores de la consola de ciudades. Renombrar una columna de cualquiera de esas
  tablas compila y deja respondiendo 500 (`42703`) a los dos `impact`, a `GET synonyms` y `GET unresolved/{id}`, y a `resolve`,
  `PUT synonyms/{id}` y `retry` **después** de confirmar su escritura (la respuesta lee los contadores): el cambio queda hecho
  aunque el cliente vea el 500. El recorrido (`POST backfill`) no lee este adaptador. Lo amarra `CityCorrectionInsightsTests`
  (en el proyecto de Shopify, que es el que sabe armar pedidos, clientes y huellas reales): una tabla o columna nueva que lea el
  adaptador entra ahí.
- **Una plantilla del sistema se envía desde su fila, no desde una definición** (spec 2026-10-31). `QuotationTemplateDefinition.BaseBody` y
  `BillingTemplateDefinition.BaseBody` son sólo el texto inicial: cada tenant lo cambia. Un dato nuevo disponible para el texto entra en
  `SystemTemplateVariables` y en los valores de `QuotationTemplateOutbound` o `ServiceContractsMetaWhatsAppSender`, con su etiqueta; si falta en
  el segundo, `SystemTemplateSend.Build` devuelve `null` y el envío falla con `message_rejected`. `POSITIONAL` sólo existe en filas viejas, y
  `LegacyPositionalKeys` da su orden: no lo cambies. Nombrada o no se decide por las claves, no por la columna, porque el refresco guarda
  `POSITIONAL` si Meta no manda el campo. Las claves son en español (`numero_cotizacion`, `valor_total`, `vigencia`…). Las cuatro primeras
  variables son las fijas de `FIXED_VARIABLE_NAMES` del frontend: si esa lista crece, `SystemTemplateVariables` también, y hay que darle valor
  en `TemplateSenderValues`; llevan guion bajo, por eso `FlowValidator.CheckName` las exime de la regla de nombres propios. `fullname`,
  `order_number`, `total` y `valid_until` salieron de la lista pero las cotizaciones aprobadas los llevan: `LegacyKeys` les da su valor en
  `SystemTemplateSend.Build` y `IsAdoptable` los acepta; no los limpies.
- **El payload de `geography.city_synonym.*` es contrato con Shopify y vive literal en dos pruebas.** `GeographyEventPublisher`
  lo escribe y `CitySynonymEventPayloads` lo lee por nombre: renombrar un campo compila, pasa las pruebas de cada lado por
  separado y deja el mensaje marcado como procesado sin trabajos sembrados. Se cambia en los dos lados y en las dos pruebas con el
  mismo literal, `GeographyEventPublisherTests` y `CitySynonymEventPayloadsTests`.
- **Un trabajo de corrección de ciudad aplica la ciudad *actual* del sinónimo, y uno borrado salta lo pendiente.**
  `ShopifyCityCorrectionProcessor` relee el sinónimo al procesar: si el operador lo corrigió entre el evento y el trabajo manda la
  ciudad nueva, y si lo borró, el trabajo termina `Skipped` con `synonym_deleted` y el pedido queda como estaba. Por eso borrar
  no publica nada ni deshace lo ya corregido, y por eso `IShopifyCitySynonymLookup` devuelve también los borrados: filtrarlos
  haría que el trabajo no supiera por qué saltó. Lo amarran `CitySynonymEndToEndTests.DeletingWhileJobsArePendingSkipsThem` y
  `ShopifyCityCorrectionProcessorTests`.
- **`run_units` se escribe en el mismo `SaveChanges` de la unidad y vive 3 días (`RunDetailRetentionDays`); armarla nunca lanza
  (`RunUnitWriter`).** Un camino nuevo que cierre una corrida fuera del motor llama `WriteClosed`, o la vista no explica por qué
  terminó.
- **La regla de redondeo de un documento vive en dos columnas nullable, y `null` es `PriceRounding.LegacyFor`** (spec 2026-10-27).
  `LegacyFor` tiene el literal `"COP"` porque BuildingBlocks no ve el catálogo; `TheLegacyRuleMatchesTheDefaultForEveryCatalogueCurrency`
  la amarra a `PriceRoundingDefaults.For`. Una moneda nueva con otros decimales pone roja esa prueba: decide ahí si los documentos viejos
  de esa moneda se leen con la regla vieja. La regla sólo baja al documento en `QuotationPricingRecalculation`/`OrderPricingRecalculation`
  (`UsePriceRounding`); un camino nuevo que rehaga líneas sin pasar por ahí calcula con la regla guardada, no con la vigente. Los pedidos
  externos nunca toman la del tenant.
- **ImageSharp sigue en 3.1.12 con sus avisos suprimidos, y subirla exige una licencia de Six Labors** (2026-10-07,
  ampliado el 2026-10-27 con GHSA-v76p-62qx-wwq2). Un `NU1903` nuevo de `SixLabors.ImageSharp` rompe el restore, el build y
  el `docker build` por `TreatWarningsAsErrors`, y no es culpa de la rama en curso. Las versiones corregidas (3.2.0 y 4.x)
  traen en `build/SixLabors.ImageSharp.targets` el target `SixLabors_ValidateLicense`, que corre antes de `CoreCompile` y en
  `Release` —el Docker— falla sin `SixLaborsLicenseKey` ni `sixlabors.lic`; en `Debug` sólo avisa, así que en local no se ve.
  Mientras no haya clave, cada aviso nuevo se analiza: se suprime en `Directory.Build.props` **sólo** si su vector queda fuera
  de las `Configuration` restringidas (`ImageSharpConfiguration` de Storage: JPEG, PNG, WebP; `ImageSharpMessagingImageCompressor`:
  JPEG, PNG) y se documenta ahí el porqué. Un aviso en el decodificador de JPEG, PNG o WebP no se suprime: obliga al upgrade.
  **Para el upgrade:** pedir la licencia Community (gratis por debajo de USD 1M de facturación anual; la condición la confirma
  el owner) en https://licensing.sixlabors.com/; guardar la clave como secreto en `Backend-prod` y pasarla al `docker build`
  como build-arg hacia la propiedad `SixLaborsLicenseKey`, nunca commiteada (un `sixlabors.lic` en el repo es la clave en
  claro); subir la versión en `Directory.Packages.props` con `dotnet restore --force-evaluate` de los lock files; quitar los
  `NuGetAuditSuppress` y su comentario; y verificar con un build `Release` local con la clave puesta. La 4.x es salto de mayor:
  revisar la API en Storage y en `ImageSharpMessagingImageCompressor`.
- **El override del webhook vive en la WABA, no en la configuración** (addendum 2026-10-30 del spec 2026-10-29). Quitar
  `Meta:App:WebhookOverrideCallbackUrl` no le dice nada a Meta: la WABA sigue mandando a esa URL hasta que alguien repita
  `POST /{wabaId}/subscribed_apps` sin cuerpo. Y al revés: **cualquier** suscripción sin cuerpo lo borra, según la documentación
  de Meta, también un signup de QEP con la clave vacía o una re-suscripción desde lulo chat, y el síntoma es que los webhooks
  vuelven a la URL de la app sin ningún error. Lo único que dice el estado real es `GET /{wabaId}/subscribed_apps`; el procedimiento
  está en [README § Historial de coexistencia](README.md#historial-de-coexistencia).
- **La API externa no usa `IExecutionContext` sino `IApiCaller`** (spec 2026-10-30). El tenant sale de la clave, no de la ruta ni de
  un header, y una clave nunca recibe claims `permission`: ninguna política interna pasa con ella. Un scope nuevo se registra con
  `ApiScopeDefinition` en DI (la constante vive en el módulo dueño) y sus rutas cuelgan del grupo de `ExternalApiEndpoints`, que fija
  el esquema `ApiKey`; una ruta externa fuera de ese grupo no recibe ese esquema. Los `code` `external.*` son contrato público con
  los integradores: no se renombran.
- **Un camino nuevo que lea autores de `messaging.messages` tiene que tratar `sent_by_kind = 4`** (clave de API; el 3 está reservado
  para agentes IA). Ese autor no tiene `sent_by_member_id`: lleva `sent_by_api_key_id` y `sent_by_api_key_name`, así que un join al
  miembro lo pierde en silencio. Y uno que muestre «primera respuesta» debe excluirlo: un envío automático de un sistema externo no
  es una respuesta de una persona.
- **El indicador de escritura de WhatsApp marca el mensaje como leído para el contacto** (decisión del owner, 2026-10-31). Cloud API lo
  manda en la misma llamada que el leído (`status: "read"` + `typing_indicator`), así que el contacto ve el chulo azul aunque en QEP la
  conversación siga sin leer. No es un bug: `POST …/typing` no toca `unread_count`, y no hay que «arreglar» la diferencia. Instagram no
  tiene ese efecto: `typing_on` va solo.
- **El listener de tiempo real no puede pasar por PgBouncer en modo `transaction`** (2026-11-01). `MessagingEventsListener` hace
  `LISTEN messaging_events` y ahí no sobrevive: el log dice «listening», el hub conecta y no llega ningún evento, sin error. `pg_notify`
  sí funciona por el pooler (`PgMessagingNotifier`). Por eso el listener lee `ConnectionStrings:QepDatabaseListener` (directo a
  `pg-platform-rw`) y cae a `QepDatabase` si falta, está vacía o es un `#{…}#` sin resolver (`ListenerConnectionString`). El log de
  arranque dice qué clave usó (`QepDatabaseListener` o `QepDatabase`), nunca el valor. **La causa está inferida, no medida:** se
  verifica tras desplegar con los bytes del WebSocket en el ingress; handshake más pings de 13 bytes y nada más significa que siguen
  sin llegar eventos.

- **`PlatformUsageSource` lee `tenancy.tenants`, `messaging.messages` y `messaging.conversations` por nombre** (spec 2026-11-01). Es SQL crudo en
  Bootstrapper sobre la conexión de `MessagingDbContext`: una conversación cuenta una vez por canal si tuvo un entrante en vivo (`direction = 1`,
  `NOT imported`) desde el corte, y el canal sale de `conversations.channel`, no de la conexión. Renombrar una de esas columnas compila y deja
  `GET …/operator/usage` en 500 (`42703`); lo amarra `PlatformUsageApiTests`, que corre contra el esquema migrado. Si agregas un tipo de mensaje
  que no deba contar como actividad del cliente, tiene que quedar fuera de `direction = 1` o llevar `imported`, o infla el reporte sin error.
- **La presencia de la bandeja es compartida sólo con `Valkey:Areas:Presence`, y es condición para subir `replicas`** (spec 2026-11-13, D-2).
  `MessagingHub` escribe en `IMessagingPresence` y el reporte de uso lee por `IMessagingPresenceReader`; los registra `AddQepValkey`. Prendida,
  cada pod escribe a sus conectados en `qep:presence:{tenantId}` (sorted set, latido de 30 s en `PresenceHeartbeatWorker`, ventana de lectura de
  90 s, `EXPIRE 120`). Apagada, o con Valkey caído, vuelve a ser por pod: el registro local `MessagingPresence`, y con más de una réplica cada una
  ve sólo a sus conectados, a medias y sin error (con Valkey caído queda un `Warning` por lectura y `qep_valkey_fallbacks_total{area="presence"}`).
  Desconectar quita al miembro sólo si el pod no le tiene otra pestaña (`MessagingPresence.HasConnection`); si sigue conectado en otro pod, el
  latido de ese pod lo vuelve a poner en ≤ 30 s. Un camino nuevo que lea la presencia pasa por el lector, no por `Snapshot()`, que es sólo local.
- **Las reglas del perfil y de la contraseña del comprador duplican las de Store e Identity, y las amarra `BuyerParityTests`** (`tests/Bootstrapper/Bootstrapper.UnitTests`).
  Buyers no ve a Store ni a Identity, así que `BuyerPasswordRules` y `BuyerProfileValidator` son copias de `PublicOrderRequestBodyValidator`, `StoreRequestBilling`/`StoreRequestShipping`
  y de los límites de contraseña de Identity. Cambiar una sin la otra pone la prueba en rojo; el error es de paridad, no del código que acabas de tocar.
  La regla del tipo de documento ya no es una copia: Store y Buyers leen la misma `DocumentTypesByCountry` (`BuildingBlocks.Domain`), y los casos de paridad cubren la regla por país.
- **La tabla de tipos de documento por país vive una sola vez, en `DocumentTypesByCountry` (`BuildingBlocks.Domain`, carpeta `Identification`).** Companies conserva su tabla tipada
  propia, atada por `DocumentTypesByCountryTests`; Customers (`IdentificationTypeParser`) y Quotations (`PartyFiscalTypesParser`) las atan sus pruebas de paridad. Customers y las
  partes de facturación comparan el tipo con **su propio** país en cada escritura (una fila guardada que no cumple se lee, pero no se puede volver a guardar igual); un país vacío
  en una parte es `CO`.
- **`PosCustomerLookupTests.TheIdentificationTypesMatchTheCustomersModule` compara Pos con `AllowedFor("CO")`, igual que la importación de clientes por Excel y la de contratos de
  servicio.** Ensanchar Customers no ensancha a ninguno de ellos; si el owner lo quiere, es una decisión aparte.
- **`MergeForStore` toma el país de la solicitud junto con su documento cuando el cliente no tiene documento** (la dirección sale de la solicitud y el teléfono conserva su E.164
  anterior cuando se reemplaza el país); `CustomerMergePlan` rellena documento y país como un par.
- **El payload de `store.buyer-signup-requested.v1` es un contrato entre Store y Buyers, leído por nombre.** Store lo escribe como `StoreBuyerSignupRequestedPayload` y Buyers
  lo lee como `BuyerSignupRequestedMessage` (record propio, camelCase), sin referencia entre los módulos; lo amarra `BuyerParityTests.TheSignupEventContractMatches`. Renombrar un
  campo en un lado compila y deja las altas procesadas sin efecto. El payload **nunca** lleva la contraseña en claro, sólo `passwordHash`; por eso el housekeeping de Buyers
  borra a las 24 h esas filas del outbox de plataforma.
- **`buyers.consents` es prueba legal: sin clave foránea a propósito, nunca se borra, y el `Down` de `InitialBuyers` no se corre** (borraría los consentimientos de todos los
  compradores). Ni borrar la cuenta ni `BuyerHousekeepingWorker` lo tocan; cualquier purga nueva de Buyers tiene que dejarlo fuera.
- **Todo `/api/v1/public/stores/{host}/buyer/*` responde `Cache-Control: no-store`, y lo pone `BuyerNoStoreMiddleware` antes de `UseRateLimiter`.** Un endpoint nuevo del
  comprador no necesita hacer nada, pero mover el middleware después del limitador deja los `429` cacheables. Las rutas de Buyers leen el cuerpo a mano con tope de 64 KB
  (`ReadBodyAsync`): no uses el binder de minimal API ahí.
- **El header de sesión del comprador está declarado dos veces.** `PublicBuyerEndpoints.BuyerSessionHeader` (Buyers) y `StorefrontClient.BuyerSessionHeader` (Store, que no
  ve a Buyers); `BuyerParityTests.TheBuyerSessionHeaderMatches` los ata. El BFF sólo borra la cookie con el código `store.buyer.session_invalid`: no devuelvas un 401 con
  sesión válida (por eso la contraseña actual mala es `422 store.buyer.password_incorrect`).
- **La anulación del comprador es más estricta que `Order.Cancel`, y su 409 también cubre la carrera.** Sólo anula con `Status == Open && PaymentStatus == PaymentPending && PaymentProofs.Count == 0 && !IsPrepared && !IsInvoiced` (D5 y spec 2026-11-10, `Order.CanBeCancelledByBuyer`: un pedido con cualquier comprobante, preparado o facturado ya no se anula desde la tienda); fuera de eso responde `409 order.order.not_cancellable_by_buyer`. La `RequestConcurrencyException` se traduce en `BuyerOrderGateway` al mismo 409 tras un reintento: la tienda nunca ve un 412, no tiene versión que mandar. El dominio no puede lanzar `ResourceConflictException` (Domain sólo ve `BuildingBlocks.Domain`): lanza con el código de `BuyerOrderErrors` y Application lo traduce. `BuyerStorefront` lleva `TenantId`: una prueba que lo construya a mano necesita los tres argumentos.
- **`Order.LinkBuyerAccount` no sube `Version`, pero `Version` es token de concurrencia.** El enlace del alta diferida es invisible para el `If-Match` de la consola, y aun así su `UPDATE` lleva `WHERE version = …`: si otra escritura ganó, `BuyerOrderGateway.LinkAsync` relee y reintenta una vez (sólo si sigue vacío). El checkout resuelve la sesión del comprador siempre que llega el header, no sólo con `updateProfile`; `ScalarTextAsync` de las pruebas de Store devuelve `""` para un `NULL`.
- **El enlace del pedido de la tienda con la cuenta del comprador lo deciden el correo de facturación y la marca `buyer_account_set_by_member`** (spec 2026-11-15). El checkout y el inicio de sesión enlazan por correo verificado, sólo en pedidos de origen `Store`. Cuando un asesor cambia o quita la cuenta (`PUT`/`DELETE …/orders/{id}/buyer-account`, `Order.SetBuyerAccountByMember`) la marca queda en `true`, también al quitar sobre un pedido sin cuenta, y con la marca en `true` ningún camino automático (`LinkBuyerAccount`, `LinkStoreOrdersByBillingEmailAsync`) toca la cuenta. Un camino automático nuevo respeta la marca, o deshace la decisión del asesor en el siguiente inicio de sesión del comprador. El correo del detalle lo resuelve `IQuotationBuyerAccountLookup` (adaptador en Bootstrapper): una cuenta borrada deja el id en el pedido y el detalle responde `buyerAccount: null`. La auditoría (`quotation.order.buyer_account_changed`) lleva sólo los dos ids en `changedFields`, nunca correos; el historial del pedido (`BuyerAccountChanged`, sólo la decisión del asesor) sí nombra los correos, a propósito (owner, 2026-11-15). No «unifiques» las dos reglas.
- **La nota del comprador es `Order.BuyerNotes`, no `Order.Notes`** (spec 2026-11-10, D9; Shopify desde el spec 2026-11-14). `Notes` es del asesor: la sobrescriben `UpdateNotes`, `AddPaymentProofs` y `AttachExternalShipping`, y en un pedido de Shopify lleva «Shopify #N» y la dirección cruda, nunca la nota del comprador, y no viaja a Shopify. `BuyerNotes` la escriben `CreateFromStore`, la importación de Shopify (`CreateExternal`) y la sincronización de Shopify, y el asesor **sólo** en pedidos de Shopify (`UpdateExternalBuyerNotes`, que responde `order.order.source_invalid` en cualquier otro origen; `EditExternalShippingParty` no la toca). En un pedido de la tienda pública sigue intocable: un método nuevo que la edite ahí le muestra al comprador un texto que no escribió. Los pedidos anteriores a `AddOrderBuyerNotes` la tienen en `NULL` a propósito (sin backfill), y los de Shopify importados antes del spec 2026-11-14 también: su nota sigue dentro de `Notes`.
- **`support.ticket.view` es una política, no un permiso: exige `support.ticket.read` o `support.ticket.manage`** (plan de soporte
  fase 1, D5; el owner fijó que quien tenga cualquiera de los dos ve todos los tickets del tenant). Vive en `SupportPolicies.TicketView`,
  fuera de `SupportPermissions`, para que no se cuente ni se asigne como permiso, y se registra con `AddAnyPermissionRequirement`
  (`QepServiceCollectionExtensions`); `SupportPolicies.SettingsView` (`support.settings.view`) hace lo mismo con `read`, `manage` o
  `support.settings.manage`. Una ruta de lectura de Soporte con `RequireAuthorization(SupportPermissions.Read)` deja afuera a un rol con
  sólo `manage`, y el handler tiene que revalidar con la misma regla (`SupportAuthorization.EnsureCanViewAsync` /
  `EnsureCanViewSettingsAsync`), no con `EnsureAsync(Read)`. Es el primer módulo con este patrón.
- **Una columna con id de membresía en el esquema `support` entra en `SupportUserReferenceProbe`** (`src/Bootstrapper/`). Hoy revisa
  `tickets.assignee_member_id`, `ticket_events.actor_member_id`, `ticket_replies.author_member_id`,
  `unclassified_messages.resolved_by_member_id`, `form_versions.created_by`, `legal_text_versions.created_by` y
  `ticket_notes.author_member_id`; una columna nueva que no se sume ahí deja borrar al usuario que
  referencia, sin error. Los ids de membresía dentro de `ticket_events.details` (responsable anterior y nuevo de un
  `AssigneeChanged`) **no** se revisan, a propósito: si el usuario se borra, la traza los muestra como miembro removido («Sin
  nombre»). No los «arregles» sumando un escaneo del JSON: retendría a todo usuario que alguna vez fue responsable de un ticket.
- **El calendario de Soporte no es el de la bandeja** (owner, 2026-11-15, spec §13.4, revierte la decisión del 2026-11-14). Es lunes a
  viernes, menos los festivos de Colombia si el país del tenant es `CO` (`SupportBusinessCalendar`, en Bootstrapper por
  `ColombianHolidays`); no lee el horario de atención ni sus excepciones. No lo «unifiques» con `BusinessHoursSchedule`: un horario
  que abre los sábados correría los términos legales. Lo amarra
  `SupportBusinessCalendarTests.TheCalendarIsMondayToFridayLessColombianHolidaysOnEveryDayOfAYear`.
- **Un ticket `Resolved` o `Closed` no cambia de responsable, tipo ni cliente: `422 support.ticket.not_editable`** (R9, owner
  2026-11-15). La guarda está en el agregado (`SupportTicket.EnsureEditable`), no en los handlers, para que la cumpla todo camino
  nuevo; rechaza aunque el valor sea el mismo. La fusión de clientes (`CustomerMergeReferences`, SQL crudo) sigue moviendo tickets en
  cualquier estado, a propósito: es el sistema, no un asesor. **Extendida (owner, 2026-11-15):** fijar o soltar un mensaje y «Seguir el
  hilo» se permiten `Resolved` y dan el mismo `422 support.ticket.not_editable` en `Closed`, también apagar un hilo ya apagado
  (`SupportTicket.EnsureNotClosed`, desde `RecordMessageEvent` para `MessagePinned`/`MessageReleased` y desde `SetFollowThread`).
  Reemplazó a `support.ticket.follow_closed`, que ya no existe. Responder se permite en cualquier estado. Los caminos del sistema no pasan
  por esa guarda: el worker del hilo vincula entrantes (`SupportThreadFollower`), la fusión y el borrado de clientes son SQL crudo, y
  clasificar desde «sin clasificar» o «De ninguno» (`MessageClassified`, `MessageDismissed`) tampoco la toman.
- **El teléfono del cliente vinculado va al fijo de la foto del solicitante, nunca al celular** (owner, 2026-11-15, P3;
  `TicketRequesterNormalizer.WithCustomerContact`). `customers.phone_e164` puede ser fijo, y una plantilla de WhatsApp a un fijo la
  rechaza Meta: la respuesta queda `Failed` sin que el alta lo advierta. El celular de la foto sale sólo del solicitante o del `wa_id`
  de la conversación; sin él, `reply-options` dice `no_mobile`. No lo «arregles» copiando el teléfono al celular para habilitar la
  plantilla.
- **Las pruebas de plazos de Soporte comparten un host por clase con el reloj movible: `ClockedSupportHost`** (`SupportTicketsApiTests`,
  `SupportTicketActionsApiTests`, `SupportInboxApiTests`). Rompe a propósito la regla de «una prueba que toca el reloj levanta su host»:
  el `ITenantClock` del host es un `MutableTenantClock` y lo comparten sólo las pruebas de esa clase. Hoy es seguro porque xUnit corre
  las pruebas de una clase una tras otra, `StartTestAsync()` devuelve el reloj a `ClockedSupportHost.Reception` (viernes
  2026-11-13 10:00 en Bogotá) antes de cada una, y cada prueba siembra su propio tenant y sólo afirma sobre él. Se rompe, con fallas
  que dependen del orden, si una prueba nueva usa el host sin llamar a `StartTestAsync()` (hereda el reloj que dejó la anterior) o si se
  activa el paralelismo dentro de la clase (dos pruebas moverían el mismo reloj). Una prueba que necesite otra configuración del host
  levanta el suyo.
- **`SupportThreadFeed` lee `messaging.messages` por nombre** (Soporte fase 2, plan 2026-11-15). Es SQL
  crudo en Bootstrapper sobre la conexión de `SupportDbContext`, igual que los demás lectores cruzados: renombrar `occurred_at`,
  `direction`, `imported` o `conversation_id` compila y deja el seguimiento del hilo sin vincular nada (el worker sólo loguea). Lo
  amarra `SupportBootstrapperAdaptersTests`, que corre el adaptador real contra el esquema migrado. Un entrante es `direction = 1` y
  `NOT imported`: un camino nuevo que escriba entrantes del cliente con otra marca los saca o los mete en Soporte sin error.
- **`ConversationEventType` y `MessageTemplateKind` ganaron valores al final** (`TicketOpened`, `TicketClosed`; `SupportReply`), y los
  dos se guardan por nombre (`details->>'type'`, `message_templates.kind`). No los reordenes ni los insertes en el medio. Escribir en el
  hilo desde Soporte pasa por `IConversationEventAppender`, enrolado en la transacción de Soporte por `SupportConversationEvents`: nunca
  un `INSERT` de Soporte en `messaging.*`.
- **Responder desde Soporte no toma la conversación** (plan E12): `ISupportReplyOutbound` no pasa por `OutboundGuards`, no la asigna, no
  cambia su estado y liquida con `seen: 0`. Lo autoriza Soporte con `support.ticket.manage`. La liquidación sí es la de siempre
  (`OutboundSettlement`), que es la que apaga `history_managed`: un camino nuevo de envío desde Soporte que se salte `IOutboundMessages`
  deja que la siguiente parte del historial pise la conversación. Lo amarra `SupportReplyOutboundTests.ASupportReplyTurnsOffHistoryManaged`.
- **Un vínculo `Linked` del worker no sube la versión del ticket ni escribe evento** (plan E7). Si lo hiciera, cada mensaje del cliente
  le daría un 412 al asesor que tiene el ticket abierto. Sólo sube cuando cambia el estado (`WaitingOnCustomer` → `Open`); pasar sola
  una fila de «sin clasificar» (`SupportThreadFollower.ReassignAsync`) tampoco escribe evento. El único `MessageClassified` lo escribe
  el asesor al asignar. La idempotencia del worker es por índices (`IX_ticket_messages_ticket_message`, `PK_unclassified_messages`,
  traducidos por nombre), no por candados: no la cambies por una consulta previa.
- **El retomado de una respuesta `Pending` sale por su fila, nunca por el reintento** (`SendTicketReply`, revisión del bloque C y
  revisión final). Una `Pending` de más de 60 s se retoma con el canal, la entrega, la conexión, el `target_wa_id`, el adjunto y el texto
  guardados, sin mirar `If-Match`. La plantilla al celular retomada no vuelve a revisar el celular de la foto, la conexión ni la
  aprobación de la plantilla: Mensajería, idempotente por `clientId`, devuelve primero lo que ya salió, y esas revisiones la marcarían
  `Failed` aunque el mensaje sí haya salido. Si el canal la rechaza o ya no tiene ruta, queda `Failed` con el motivo y nunca se borra
  (sólo una respuesta nueva rechazada antes de enviar se borra). El correo no tiene `clientId` del otro lado: retomar una `Pending` de
  correo lo manda otra vez, y eso está aceptado (README).
- **El adjunto de una respuesta de Soporte se guarda en `ticket_replies` (`upload_id`, `file_name`) y viaja hasta
  `ISupportReplyOutbound`**, que arma la clave del staging con la membresía de quien responde: la sesión de subida tiene que abrirla el
  mismo asesor, para esa conversación. Un retomado manda el adjunto guardado, nunca el del reintento. Las reglas que Soporte copia de
  Mensajería (nombre 256, pie 1024, permiso de la sesión) viven en `SupportReplyAttachmentRules` y las amarra `SupportReplyParityTests`.
- **El feed del seguimiento mira sólo las últimas `Support:Workers:FeedLookbackHours` (24) por `created_at`** (`SupportThreadFeed`,
  revisión final). Sin el límite recorría cada conversación seguida desde `follow_since` en cada tick, para siempre. Un worker caído más
  que el lookback deja sin vincular lo que llegó mientras tanto: no lo «arregles» quitando el límite, sube la configuración.
- **`CustomerDeletionReferences` desvincula la conversación de los tickets que la usaban** (plan E18): `conversation_id` en `NULL`,
  seguimiento apagado, `ticket_messages` y `unclassified_messages` de esa conversación borrados y `ticket_replies.message_id` en `NULL`,
  subiendo `version`. El ticket sobrevive (lo bloquea su propio cliente, no la conversación). Una tabla nueva de Soporte con un id de
  conversación o de mensaje entra ahí y en `CustomerDeletionAcrossModulesTests`.
- **Las pruebas de integración corren con `SupportThreadFollowWorker` prendido salvo que lo saquen** (`Support:Workers:Enabled`,
  verdadero si falta). Cada 5 s lee toda la base sin tenant: una prueba que siembra entrantes y espera verlos sin clasificar o sin
  vincular pierde la carrera contra el worker. `SupportApiHarness.OpenOwnHostAsync` lo apaga y además lo saca del contenedor por
  `ImplementationType` (si la bandera dejara de leerse, la prueba sigue sin worker); `SupportHostWithoutWorkers` hace lo mismo por clase.
- **El cuerpo de la plantilla `SupportReply` no termina ni empieza con una variable** (Meta rechaza esos cuerpos). El texto base cierra
  con «Si tienes dudas, responde este mensaje.» y `TheBaseBodyNeitherStartsNorEndsWithAVariable` lo amarra. Un texto propio de un tenant
  (cuerpo de `POST …/template-management/support-reply`) tiene la misma restricción en Meta; la aprobación es por conexión.
- **`ValkeyFixedWindowRateLimiter.AttemptAcquireCore` tiene que seguir negando** (spec 2026-11-13, D-3).
  - `RateLimitingMiddleware` llama primero `AttemptAcquire` y sólo con un rechazo pasa a `AcquireAsync`. Si el camino
    síncrono concede, por ejemplo desde el respaldo de memoria, todas las políticas salen de Valkey en silencio y vuelven
    a contar por pod. Lo amarran `TheSynchronousAttemptDefersToTheAsyncPath` y los `*SharedAcrossTwoHosts` de
    `ExternalApiSurfaceTests`.
  - Bajo la ACL de producción (`-@scripting`) no hay Lua. El contador es `MULTI` + `INCR` + `EXPIRE … NX` + `EXEC`.
  - Una política nueva de ventana fija pasa por `SharedRateLimitPartitions.FixedWindow`, no por
    `RateLimitPartition.GetFixedWindowLimiter`. Si no, queda por pod y sin error.
  - Una partición que lleve el patrón de ruta va con `hashPartition: true`.
- **Todo camino nuevo que revoque o borre sesiones llama `RemoveAsync` de la caché antes y después del guardado** (spec 2026-11-13,
  D-4). Vale para Identity (`ISessionCache`) y para el comprador (`IBuyerSessionCache`): el `DEL` de antes cubre el request que lee
  durante la transacción, el de después al que leyó Postgres antes del commit. Si falta, con `Valkey:Areas:Sessions` prendido la sesión
  revocada se sigue sirviendo hasta 15 min, sin error. Un fallo se loguea en `Error` con el id de la sesión, nunca con el hash. Lo
  amarran `CachedSessionTests` (Identity) y `CachedBuyerSessionTests` (Buyers); los caminos de hoy son `SessionService`,
  `SessionRevocationWorker`, `OrphanUserCleanupWorker`, `BuyerSessions` y `DeleteBuyerAccount`.
- **Una `BuyerSession` que devuelve `BuyerSessions.TryResolveAsync` no se guarda.** Con la caché prendida es una entidad reconstruida
  (`BuyerSession.Restore`) que EF no sigue: un `Revoke` o un `Touch` sobre ella y un `SaveChanges` no mandan ningún `UPDATE`, y el
  logout queda «hecho» con la fila viva. Revocar pasa por `BuyerSessions.RevokeCurrentAsync`, que lee la fila. Lo amarra
  `CachedBuyerSessionTests.LogoutWithACachedSessionRevokesTheRow`.
- **Todo cambio que altere los permisos efectivos de un tenant llama `ITenantAccessVersion.BumpAsync` antes y después del guardado**
  (spec 2026-11-13, D-5). Roles, membresías (suspender, reactivar, quitar, activar, aceptar invitación), módulos y estado del tenant ya
  lo hacen. Si un camino nuevo no lo llama, con `Valkey:Areas:Permissions` prendido la caché sirve los permisos de antes hasta 60 s: un
  miembro suspendido sigue entrando, sin error. Lo amarra `CachedAuthorizationTests` (Tenancy).
- **Una tabla de cola nueva entra al mapa de `QueueDoorbellInterceptor`** (`BuildingBlocks.Infrastructure`, `Tables`), con su cola en
  `DoorbellQueues`, y su worker espera con `doorbell.Listen(...)`. Sin eso el worker sólo se entera en la red de seguridad, no al
  insertar, y nada falla. El `DbContext` que mapee una de esas tablas lleva el interceptor: lo amarra
  `CompositionRootTests.EveryDbContextThatMapsADoorbellTableHasTheInterceptor`. Lo que no es un `INSERT` (iniciar o reanudar una
  campaña es un `UPDATE`) timbra a mano después del commit (`Campaigns.cs`).
- **Un consumidor nuevo de `platform.outbox_messages` escucha `DoorbellQueues.Outbox`**, con `DoorbellQueues.SafetyNet(DoorbellQueues.Outbox)`,
  como `OutboxPublisherWorker` o `NotificationsDeliveryWorker`. Un timbre de `outbox` despierta a todos los oyentes del proceso; un
  consumidor que siga con su `Task.Delay` de antes funciona, pero no se entera del timbre.
- **Un comando nuevo contra Valkey tiene que pasar la ACL de producción** (`~qep:* &qep:* +@all -@dangerous -@scripting`, DP-V4):
  sin Lua (`EVAL`, `SCRIPT`), sin `KEYS`, `FLUSH*`, `INFO` ni `CONFIG`, y toda clave o canal con prefijo `qep:`. El gateway traga
  un `NOPERM` como respaldo del área, así que en producción no se ve como error: se ve como
  `qep_valkey_fallbacks_total{area=…}` subiendo. Las primitivas de presencia y limitadores las cubre `ValkeyAclTests`; sesiones,
  permisos, timbres, líder del muestreador y compuerta del toque, `ValkeyAclCompatibilityTests` (las dos en `Bootstrapper.UnitTests`),
  que exige el efecto en Valkey **y** cero respaldos con los adaptadores reales. Un área nueva suma la suya. Si falla, es un hallazgo: la ACL no se relaja.
- **El presupuesto de envío de campañas se queda en Postgres** (`messaging.connection_send_budgets`, decisión del owner 2026-11-13).
  D-6 del spec de Valkey no se construyó: no hay adaptador de Valkey para el cubo.
- **Un código de falla nuevo de Soporte va a `SupportChannelReasons` y, si sale de Mensajería, también a su copia en
  `SupportReplyOutbound`; `SupportFailureCodeParityTests` los amarra. El frontend traduce por esta lista** (Tarea 8a, owner 2026-11-15).
  Es el único vocabulario del `failureCode` de una respuesta, del `ConsultAccess.FailureCode`, del `reason` de `reply-options` y del
  `cannotCreateReason` de la pestaña de Soporte de una conversación (`no_permission`, `messaging_disabled`): un
  literal suelto en `Modules.Support.*` es un código que la pantalla no conoce.
- **El formulario público de Soporte (`/api/v1/public/support/{tenantSlug}`) no se prueba con el stub de auth** (fase 3). En modo stub
  `RequireCsrfHeaderMiddleware` ni se registra y la cookie no se valida: una prueba con stub pasa aunque la exención por prefijo
  (`SupportPublicPaths`, duplicado de `PublicSupportEndpoints.GroupPattern` y atado por `SupportPublicPathsTests`) esté rota o un handler
  lea la sesión. Lo que toca CSRF, cookie o CORS va en `SupportPublicRealAuthHost` (precedente `ShopifyApiHarness.WithRealAuthentication`;
  lo amarra `ThePublicGroupIgnoresTheSessionCookieWithRealAuth`). Ningún handler del grupo lee `IExecutionContext` ni la cookie: el
  tenant sale del slug (`SupportPublicResolver`), nunca de la sesión de quien navega.
- **`support.consents` y `support.legal_text_versions` son prueba legal: sólo se insertan** (spec §9.5, plan F16 y F17). Ningún método de
  dominio los edita ni los borra, `consents` no tiene FK (ni al ticket ni a la versión: guarda su foto del texto, que es lo que prueba) y
  el `Down` de `AddSupportPublicForm` no las borra. **Ese `Down` no se corre en producción**: el rollback es revertir el código. Una
  purga futura de tickets tiene que dejar los consentimientos fuera, y una migración nueva de Support que las toque tampoco puede
  borrarlas en su `Down`.
- **El PIN de consulta nunca sale a logs, auditoría, eventos ni trazas: sólo se guarda su hash** (`ticket_access.pin_hash`, PBKDF2).
  Viaja dos veces y nada más: en la respuesta del radicado y en el envío al destino registrado del ticket. Un log nuevo en el formulario
  público lleva el id del ticket y el tipo de la excepción, nunca el PIN, el radicado ni el destinatario; lo amarra
  `ThePinNeverReachesLogsAuditOrEvents`, que corre el host en `Trace`. **El contador de fallos vive en `ticket_access`, no en el ticket**:
  si un intento fallido tocara el ticket, le subiría la versión y le daría un 412 al asesor que lo tiene abierto. Cinco fallos bloquean
  ese radicado 15 minutos, y mirar el bloqueo y anotar el intento van con la fila tomada (`LockByNumberAsync`, `SELECT … FOR UPDATE`):
  sin el candado, intentos a la vez leen el mismo contador, se pisan y el quinto fallo no bloquea (`ConcurrentWrongPinsStillLockTheTicket`). Un camino
  nuevo que lea o escriba la clave (otra forma de consulta, un reenvío por otro canal) toma la misma fila y nunca desbloquea.
- **`uploadKey` es el único vínculo entre la subida anónima de un adjunto y quien la abrió** (plan F32). Es un `Guid.NewGuid()` (v4) que
  Soporte genera, devuelve una vez y Storage guarda como `OwnerId` del archivo `SupportTicket`; completar y radicar exigen el par
  `(fileId, uploadKey)`. No lo cambies por un v7 (`Guid.CreateVersion7`, el de los ids de Storage): se adivina por la hora y cualquiera
  reclamaría la subida de otro. Tampoco lo pongas en una URL ni en un log. La subida sin miembro (`IStorageSystemUploads`) no pasa por
  `StorageAuthorization` y no tiene endpoint en Storage: la usa sólo `SupportAttachmentStorage` (Bootstrapper). El
  `CompleteUploadHandler` de la consola sigue exigiendo `storage.file.upload` y comparte con ella sólo el núcleo (`UploadCompletion`);
  un cambio en el núcleo vale para los dos caminos, y la autorización se queda en el handler
  (`TheMemberCompletionStillRequiresThePermission`).
- **Los archivos `SupportTicket` no se ven desde la consola de Storage** (fix round 1 del Bloque D). `storage.file.*` no es permiso
  de Soporte: sin la pared, quien tiene `storage.file.read` vería las PQRSF y su `uploadKey` (el `OwnerId`), con `publish` las sacaría al
  bucket público y con `delete` borraría un adjunto reclamado. La regla vive en un solo lugar, `SystemOwnedFiles` (al lado de
  `FileOwnerType`): `ListFilesHandler` los excluye, cada handler que carga por id llama `SystemOwnedFileGuard.EnsureVisibleToMembers`
  (404 `storage.file.not_found`, el mismo de otro tenant), `CreateUploadSessionHandler` responde `422 storage.file.owner_type_invalid`, y
  los adaptadores que leen el repositorio de archivos para otro módulo (`ProductImageLookup`, `QuotationFileLookup`) los tratan como
  inexistentes. **Un handler nuevo de Storage que cargue un archivo por id, o un adaptador nuevo sobre `IFileResourceRepository`, aplica
  la misma regla**; un dueño de sistema nuevo se suma al conjunto y no a cada handler. Lo amarran
  `ASystemOwnedFileIsNotFoundWithoutAnyEffect` y `SystemOwnedFilesApiTests`.
- **El `PUT` de un adjunto del formulario público sale del navegador al bucket privado de R2**, así que ese bucket necesita CORS para
  el origen de la página (`https://soporte.lulocrm.com`, y `http://localhost:3002` para probar en local) con `PUT` y los headers
  `Content-Type` e `If-None-Match`. Sin eso el navegador corta el `PUT`, completar responde `428 support.public.attachment_not_uploaded`
  y en el backend no hay ningún error: `Support:PublicForm:AllowedOrigins` es el CORS de la API, no el del bucket.
- **Un adjunto del formulario reclamado sigue `Available` para siempre y nada en Storage lo marca**: lo retiene la sonda
  `SupportAttachmentReferenceProbe` (Support.Infrastructure). Por eso el barrido de staging sólo pregunta por los adjuntos `SupportTicket`
  de la última semana pasada la retención (`StagingCleanupProcessor.SupportAttachmentLookback`): sin el tope, cada tick le preguntaría a
  las sondas por todos los adjuntos de la historia. Un camino nuevo que vincule un archivo `SupportTicket` a algo que no sea
  `support.ticket_attachments` necesita su propia sonda, o el barrido lo borra a las 24 h. **Esa purga guarda la fila antes de borrar
  los objetos**, al revés que la de comprobantes: un guardado fallido no deja una fila `Available` sin objeto que un radicado podría
  reclamar; un borrado fallido deja un objeto huérfano y un log (`AFailedObjectDeleteLeavesTheRowPurgedNotAvailable`). No lo «alinees»
  con el orden de los comprobantes.
- **El tope de 200 subidas anónimas por tenant es blando a propósito** (`OpenPublicAttachmentHandler`, fix round 1 del Bloque F): cuenta
  y después abre, así que entran unas pocas de más bajo ráfaga simultánea. No le pongas un candado por tenant: en un endpoint anónimo,
  quien espera el candado retiene una conexión del pool y quien lo tiene necesita otra para Storage, así que unas pocas IPs vaciarían el
  pool de toda la API. `lock_timeout` tampoco sirve (sigue reteniendo conexiones) y un try-lock rechazaría las 3 subidas en paralelo de una
  sola persona.
- **Un ticket anónimo no tiene cliente ni ninguna columna `requester_*`, y la base lo exige** (`CK_tickets_anonymous`). Una columna nueva
  de la foto del solicitante entra en `SupportDbContext.RequesterColumns` y en una migración que rehaga el CHECK, o el anónimo puede
  llevarla sin error.
- **El formulario público no acepta Word ni Excel mientras `Storage:ClamAv:Enabled` esté apagado en producción** (owner 2026-11-15): un
  adjunto anónimo con macros lo abre un asesor. Sumar un tipo de Office exige prender ClamAV primero. La lista vive en
  `SupportPublicAttachmentRules` (PDF, JPEG, PNG, WebP; `image/jpg` se toma como `image/jpeg`) y viaja a la página en
  `GET /form` → `attachments`; `SupportAttachmentParityTests` sólo exige que sea un subconjunto de la de Storage.
- **`allow_attachments` tiene default `true` en la base y en el modelo, con `HasSentinel(true)`** (owner 2026-11-15). Sin el sentinel, EF
  omite un `false` del `INSERT` (es el valor CLR por defecto) y la base lo guarda en `true`: el tenant apaga los adjuntos y siguen
  prendidos. Precedente: `stores.show_prices`. Lo amarra el guardado `true` → `false` de `SupportFormSettingsApiTests`.
- **El hub global `/api/v1/hubs/app` vive en Bootstrapper y no es de ningún módulo** (`src/Bootstrapper/Realtime/`, spec 2026-11-15).
  Un módulo se suma registrando su `IRealtimeTopicAuthorizer` (contrato en `BuildingBlocks.Application.Realtime`) para su prefijo de
  tópico, como `SupportRealtimeAuthorizer`; el hub no conoce a nadie. Mensajería sigue en su `MessagingHub`. El tópico de un ticket se
  acepta sólo en la forma exacta que escribe `SupportRealtimeTopics.Ticket` (Guid `D` en minúsculas): otra forma del mismo id sería otro
  grupo y otra sala de presencia.
- **Los avisos de Soporte por el hub se llaman a mano, después del commit** (plan N13): cada handler de escritura y
  `SupportThreadFollower` llaman `ISupportRealtime.TicketChangedAsync` con `CancellationToken.None`, no el unit of work, porque varias
  escrituras confirman con `ISupportTransaction`. Una escritura nueva que no lo llame deja la pantalla sin avisar, sin error. La fusión y
  el borrado de clientes (SQL crudo en Bootstrapper) **no** avisan, a propósito (owner O5).
- **La presencia «está respondiendo» es por conexión, no por miembro** (`{memberId}:{connectionId}`): cerrar una pestaña no borra la
  otra. Una entrada que nadie renueva (pod caído) la quita `RealtimePresenceSweeper` a los ≤ 50 s. `presenceChanged` lleva el estado
  completo, por eso `RealtimeEventsListener` entrega los avisos en orden con un solo consumidor: dos en desorden dejan un fantasma.
- **Una nota interna no pasa por el agregado del ticket** (spec 2026-11-15 §5.4): no sube `version`, no escribe `ticket_events` y nada
  de ella sale por `public/support`. Sus tablas son insert-only. Su `id` es el `clientId` de la pantalla (`PK_ticket_notes`).
- **Un archivo de nota ya está reclamado**: `ticket_note_files` entra en `SupportAttachmentReferenceProbe` (o el barrido de staging lo
  borra). Su dueño en Storage es `SupportNoteUploads.OwnerKey(ticket, miembro)`, determinista: cambiar la derivación deja huérfanas
  las subidas abiertas que todavía no están en una nota. **La clave es un UUID v8** y la del formulario público
  (`SupportPublicUploads.NewKey()`) un v4: `SupportAttachmentStorage.CountPendingAsync` cuenta sólo
  `(get_byte(uuid_send(owner_id), 6) >> 4) = 4`, así que ninguna subida de nota, reclamada o abandonada, cuenta en el tope
  `attachments_busy` del formulario (revisión del Bloque B). No uses `uuid_extract_version`: exige Postgres 17+ y la versión de
  producción no está probada en el repo. Completar o radicar con un `uploadKey` que no es v4 es `support.public.attachment_invalid`
  (`SupportPublicUploads.IsKey`). Si la clave pierde el v8, un miembro que abre subidas sin adjuntarlas deja al formulario sin adjuntos.

## Verificación

Los comandos (`restore` / `format` / `build` / `test`) están en
[README § Verificación](README.md#verificación).

- **Borrar clientes escribe en muchos esquemas por nombre** (spec 2026-11-10; `src/Bootstrapper/CustomerDeletionReferences.cs`, igual que
  `CustomerMergeReferences`). Toca `messaging` (conversaciones y todo lo que cuelga: mensajes, adjuntos, mensajes programados,
  `automation_inbox`, destinatarios de campañas con `customer_id` en `NULL`, `audience_customer_ids`), `automations`
  (`conversation_runs`, `run_units`, `http_calls`), `pipeline` (`deals`, `deal_stage_history`, `deal_quotations`, `deal_orders`,
  `triage_decisions`; `origin_conversation_id` de otros negocios a `NULL`), `pos.sales`, `shopify.orders`, `billing.customer_profiles`
  y `geography.unresolved_city_occurrences`. Si una tabla nueva guarda un id de cliente, hay que agregarla ahí; lo fijan
  `CustomerDeletionAcrossModulesTests` (Customers.IntegrationTests) y los dobles de `CustomerWriteDoubles`. Bloquean el borrado
  cotizaciones, pedidos, préstamos, contratos de servicio (y sus cobros), tickets de soporte (`support.tickets.customer_id`, motivo
  `supportTickets`, en cualquier estado: también uno cerrado) y una conversación bloqueada.
- **`CustomerReferenceLock` serializa el borrado contra quien referencia al cliente** (advisory lock por cliente, `Exclusive` para el
  borrado, `Shared` para escritores). Lo toman las unidades de trabajo de Quotations, Loans, ServiceContracts y Support; en Messaging, el ingreso
  (entrante y coexistencia), el opener, el cambio de cliente y el despacho de campañas, con **un solo candado compartido antes de
  cualquier candado de fila de conversación**. El borrado además toma el candado por conversación del runner de Automations
  (`AutomationsConversationLock.Namespace`) antes del `FOR UPDATE` de `conversations`: sin ese orden hay un ciclo de espera que la base
  no detecta. Todo esto exige `READ COMMITTED`; en `REPEATABLE READ` el chequeo de bloqueadores vería una foto vieja. El POS y los
  escritores del pipeline **no** lo toman: una venta creada en plena corrida puede conservar el id borrado (su foto sigue mostrándose).
