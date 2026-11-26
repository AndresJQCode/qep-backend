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

## Cada agente trabaja en su repo

Decisión del owner, 2026-11-26. Los agentes de este repo **no hacen tareas de `qep-frontend`** (ni de otro repo): no
escriben archivos, specs ni planes allá, no crean ramas ni worktrees, no commitean y no lanzan subagentes para hacerlo.
**Leer** el otro repo para verificar un contrato sí se puede.

Cuando un trabajo cruza repos, terminas la parte del backend y **le entregas al owner un prompt** para la sesión del otro
repo. El prompt se entiende sin esta conversación: qué se pide y por qué, el contrato (rutas, campos, permisos y códigos
de error verificados en el código), la ruta del spec del backend, la rama y el commit donde está, si ya se mergeó o
desplegó, y las decisiones pendientes.

## Tablero de trabajo

Este repo se trabaja en el tablero de Trello **Lulo CRM** (workspace QCode): <https://trello.com/b/hhjA716M/lulo-crm>.
Tus tarjetas son las que empiezan por `[backend]`. Las reglas para tomarlas, crearlas, bloquearlas y cerrarlas están
en el `CLAUDE.md` de la raíz del workspace (`../CLAUDE.md`), compartidas con `qep-frontend`.

**Tarjetas que necesitan la aprobación del owner** (decisión del owner, 2026-11-26): una tarjeta que espera una decisión o
una aprobación suya, incluida la de un spec, se mueve a la columna «Bloqueadas» con un comentario que diga qué hay que
aprobar y de qué columna venía. No se implementa ni se toma hasta que responda. Con la respuesta, se comenta en la
tarjeta y vuelve a «Por hacer», o a «En progreso» si la sigue la misma sesión. Una tarjeta que sólo espera a otra se
queda en «Por hacer».

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
- **Commits:** conventional commits. **Cada commit hecho por un agente declara sesión y
  autoría** (decisión del owner, 2026-11-26; en este repo manda sobre la regla global «sin
  atribución de IA»). El mensaje termina con estas dos líneas, juntas y tras una línea en blanco:

  ```
  Session: <nombre de la sesión>
  Co-Authored-By: Claude <modelo que escribió el cambio> <noreply@anthropic.com>
  ```

  El nombre de la sesión es el título que el owner ve en su pestaña. Se lee **justo antes de cada
  commit**, porque puede cambiar; el título puesto a mano gana sobre el automático. En Bash:

  ```bash
  t=$(cat "$HOME"/.claude/projects/*/"$CLAUDE_CODE_SESSION_ID".jsonl)
  { grep '^{"type":"custom-title"' <<<"$t" | tail -1; grep '^{"type":"ai-title"' <<<"$t" | tail -1; } |
    head -1 | sed -E 's/.*"(customTitle|aiTitle)":"(([^"\\]|\\.)*)".*/\2/'
  ```

  Si no devuelve nada, se usa el nombre de la primera línea de `ListAgents` («This session is …»).
  Un subagente no lo deduce: quien lo lanza le pasa las líneas `Session:` y `Co-Authored-By:` en el
  encargo. El autor de Git no se cambia. Los commits ya hechos no se reescriben.
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
- **Un cambio de arquitectura lo autoriza el owner antes del merge** (decisión del owner, 2026-11-25). Es cambio de
  arquitectura cualquier aviso «Requiere autorización del owner» de `.\scripts\test-feature.ps1` (una prueba de
  `tests/ArchitectureTests/` modificada o una referencia nueva entre módulos) y, aunque el script no lo vea, un contrato
  HTTP que cambia (ruta, campo, código de error o permiso) sin un spec aprobado que lo diga. Se le muestra cada
  desviación con su archivo y el motivo, y se espera su respuesta: una prueba en verde no es autorización, y aprobar
  una no aprueba las demás. La revisión final de la rama la hace un subagente de solo lectura, sin el contexto de quien
  implementó.
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

| Sí                                       | No (voseo)                              |
| ---------------------------------------- | --------------------------------------- |
| tienes, puedes, quieres                  | tenés, podés, querés                    |
| elige, revisa, intenta de nuevo          | elegí, revisá, intentá de nuevo         |
| dime, avísame, escríbenos                | decime, avisame, escribinos             |
| No tienes permiso para ver este reporte. | No tenés permiso para ver este reporte. |

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
  422 de validación lleva el mapa `errors` (`ApiExceptionHandler`, `problem.Extensions["errors"]`), el único 422 que el
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

## Gotchas generales

- **`Api.exe` corriendo bloquea todo.** `dotnet build`, `dotnet test` y los comandos `ef`
  fallan por archivo bloqueado. Detener el proceso primero. El nombre es `Api.exe` porque
  `src/Api/Api.csproj` no declara `AssemblyName`.

## Reglas por módulo

Los gotchas de cada módulo viven en [`.claude/rules/`](.claude/rules/). Cada archivo declara en su
`paths` qué archivos lo activan: Claude Code lo carga **sólo** cuando lee, escribe o edita uno de
ellos, no al arrancar. Una tarea que empieza por planear, sin abrir ningún archivo, **no** los ve:
si vas a tocar un módulo, abre su regla antes (o lee primero un archivo del módulo).

| Regla | Qué cubre |
| ----- | --------- |
| [`build-y-despliegue`](.claude/rules/build-y-despliegue.md) | Build, migraciones, rollout y despliegue: SDK, lock files, expand/contract, candado de arranque, réplicas, ImageSharp, tokens del pipeline |
| [`pruebas-y-entorno-local`](.claude/rules/pruebas-y-entorno-local.md) | Pruebas de integración y entorno local: harness, hosts compartidos, user-secrets, stub de auth, semillas, cadena de conexión |
| [`sql-entre-modulos`](.claude/rules/sql-entre-modulos.md) | SQL crudo entre módulos (columnas leídas por nombre): adaptadores de Bootstrapper que leen o escriben tablas de otros módulos: renombrar una columna compila y rompe |
| [`messaging`](.claude/rules/messaging.md) | Mensajería (WhatsApp, Instagram, bandeja, campañas, plantillas): BSUID, coexistencia, webhooks de Meta, Instagram, entrada del bot, plantillas del sistema |
| [`automatizaciones`](.claude/rules/automatizaciones.md) | Automatizaciones (motor de chatbots): bloque HTTP, flujos, consentimientos, traspasos, simulador |
| [`shopify`](.claude/rules/shopify.md) | Shopify e Integraciones: token y refresh, ingesta de pedidos, ediciones, envíos, sincronización de catálogo, asistente |
| [`geografia-ciudades`](.claude/rules/geografia-ciudades.md) | Geografía y sinónimos de ciudades: sinónimos, trabajos de corrección, eventos de Geography |
| [`quotations-pos-catalog`](.claude/rules/quotations-pos-catalog.md) | Cotizaciones, pedidos, POS y catálogo: pedidos propios, variantes, contratos con Pipeline, PDF de cotización, redondeo, formato regional |
| [`customers`](.claude/rules/customers.md) | Clientes: incompletos, fusión, identificación, importación por Excel, candado de referencias |
| [`pipeline`](.claude/rules/pipeline.md) | Pipeline (embudo de ventas): embudo, dueños de negocio, historial, triage |
| [`soporte-tickets`](.claude/rules/soporte-tickets.md) | Soporte: tickets, respuestas, plazos y vínculos al hilo: política de lectura, estados, prórroga, PIN, retomado de respuestas, seguimiento del hilo |
| [`soporte-formulario-publico`](.claude/rules/soporte-formulario-publico.md) | Soporte: formulario público, adjuntos y documentos legales: PIN de consulta, radicado, adjuntos anónimos, CORS de R2, pruebas legales |
| [`soporte-hub-notas-encuestas`](.claude/rules/soporte-hub-notas-encuestas.md) | Soporte: hub en tiempo real, notas, respuesta formal y encuestas: avisos por el hub, presencia, notas internas, PDF formal, encuesta y cierre automático |
| [`ai-copiloto`](.claude/rules/ai-copiloto.md) | Agentes de IA y copiloto: proveedores, claves, sesiones y trabajos, traspasos, delegación a chatbots, herramientas del copiloto |
| [`mayorista-compras`](.claude/rules/mayorista-compras.md) | Mayorista, Compras y Proveedores: invitaciones, órdenes vinculadas, eventos de vuelta, productos creados por Compras |
| [`buyers-store`](.claude/rules/buyers-store.md) | Compradores y tienda pública: sesión del comprador, enlace pedido-cuenta, nota del comprador, anulación, paridad con Store |
| [`infra-valkey-sesiones`](.claude/rules/infra-valkey-sesiones.md) | Valkey, sesiones, permisos, timbres y tiempo real: limitadores, ACL, cachés de sesión y permisos, colas con timbre, listener y presencia |
| [`tenancy-y-servicios`](.claude/rules/tenancy-y-servicios.md) | Tenancy, Identidad, contratos de servicio y API externa: registro de tenant, roles asignables, módulo billing, claves de API |
| [`siigo`](.claude/rules/siigo.md) | Siigo (facturación de pedidos): registro legal de facturas, DIAN apagado por defecto, retenciones, `error_detail`, bloqueo de marca manual y anulación |
| [`wms`](.claude/rules/wms.md) | WMS (bodegas, ledger y saldos): ledger inmutable, clave de `stock_balances`, particiones, ventana de `occurredAt`, idempotencia |

Una regla nueva va al archivo de su módulo, no acá. Si lee o escribe columnas o tablas de otro
módulo por nombre, va en `sql-entre-modulos`: sus paths (Bootstrapper y migraciones) la cargan
justo cuando alguien renombra una columna o toca el adaptador.

## Verificación

Los comandos (`restore` / `format` / `build` / `test`) están en
[README § Verificación](README.md#verificación). Las pruebas van en dos niveles (decisión del owner, 2026-11-24):

- **Para mergear a `develop`** basta `.\scripts\test-feature.ps1` en verde, **leyendo sus avisos**. No se corre el
  proyecto de integración completo de cada módulo tocado.
- **La suite completa** (`.\scripts\run-all-tests.ps1`) corre de noche o antes de llevar `develop` a `main`.
- **`main` sólo recibe un commit con reporte completo en verde** y el árbol limpio, según el encabezado de
  `TestResults\report.md`. `develop` puede quedar roto hasta esa corrida; lo que ahí falle se corrige antes de `main`.
