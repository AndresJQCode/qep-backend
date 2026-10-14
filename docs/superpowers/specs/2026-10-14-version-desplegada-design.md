# Versión desplegada: saber qué build corre en producción

**Fecha:** 2026-10-14
**Repos:** `qep-backend` (endpoint anónimo + configuración del Deployment) y `qep-frontend`
(feature nueva `version`, una línea en el menú de usuario y otra en el login)
**Estado:** borrador para revisión del developer; las dos verificaciones de la sección
"Decisiones pendientes" se cierran en el primer despliegue

## Problema

Hoy no hay forma de saber, desde afuera, qué commit corre en `qep-api.qcode.co` ni cuál sirve
`qep.qcode.co`:

- El backend se construye y despliega con `azure-pipelines.yml`, que etiqueta la imagen con
  `$(Build.BuildId)` y la referencia como `index.docker.io/qcodeco/prod-qep-backend:TAG`
  (`k8s/prod-deployment.yaml:34`). Ese número vive en Azure DevOps y en el Deployment de k8s; el
  proceso no lo conoce. `/health/live` responde `{"status":"healthy"}` y nada más
  (`src/Api/Program.cs:119`), y `AddQepObservability` manda como `service.version` la versión del
  assembly (`QepObservability.cs:39-43`), que es siempre `1.0.0.0` porque ningún `.csproj` ni
  `Directory.Build.props` declara `Version`, `InformationalVersion` ni `SourceRevisionId`.
- El frontend se construye en Vercel desde `main` y `package.json` dice `"version": "0.0.0"`.
  Nada del bundle revela de qué commit salió.

Los hotfixes recientes (`hotfix/usd-con-iva` en backend, `hotfix/usd-sin-fila-iva` en frontend)
se verificaron en producción "a ojo": mirando si el comportamiento cambió. Cuando un cambio no se
ve —o el pipeline falló en silencio, o Vercel sirve una build anterior— no hay manera de
distinguir "no se desplegó" de "se desplegó y sigue mal".

Lo que se necesita es poder responder, en un segundo y sin acceso a Azure DevOps ni a Vercel,
**qué commit corre en cada lado**: desde una terminal (`curl.exe`) y desde la pantalla.

## Decisiones

| #   | Decisión                                                                                                                                                                                                                                                                                                                                                                                                                                                              | Alternativa descartada                                                                                                                                                                                                                                                                                                                                             |
| --- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 1   | **Endpoint anónimo `GET /api/v1/version`** en el backend, bajo `/api/v1` y no bajo `/health`: en local y en los previews de Vercel el navegador sólo llega a la API por el proxy de Vite y el rewrite de `vercel.json`, y los dos cubren **sólo** `/api/*` (`vite.config.ts:42`, `vercel.json:9`). Un `/version` fuera de ese prefijo no sería alcanzable desde la SPA en esos hosts.                                                                                | Extender la respuesta de `/health/live`: es la `livenessProbe` y la `startupProbe` (`prod-deployment.yaml:48-71`), y mezclar información con un chequeo que el kubelet lee cada 20 s invita a agregarle cosas que lo hagan fallar. Además queda fuera de `/api`.                                                                                                     |
| 2   | **La identidad del build llega al pod por configuración, no compilada en el binario:** tres claves nuevas en `k8s/prod-configMap.yaml` con los tokens que el pipeline ya reemplaza (`#{AUTHENTICATION_GOOGLE_CLIENT_ID}#` y compañía): `Deployment__BuildId: "#{Build.BuildId}#"`, `Deployment__BuildNumber: "#{Build.BuildNumber}#"`, `Deployment__Commit: "#{Build.SourceVersion}#"`. El `BuildId` es exactamente el tag de la imagen (`azure-pipelines.yml:41`). | Pasarlo como `--build-arg` al `docker build` y de ahí a `-p:InformationalVersion`: el build corre en `k8s/docker-build.yml@k8sTemplates`, una plantilla de otro repositorio (`devops/pipeline-templates`) que desde este repo no se ve ni se controla. Queda como plan B si los tokens no resuelven (ver "Decisiones pendientes").                                       |
| 3   | **Un token sin resolver se trata como ausente.** Si la plantilla no reemplaza `#{Build.BuildId}#`, el pod arranca con ese literal en la variable. `DeploymentInfo` devuelve `null` en cualquier campo cuyo valor empiece por `#{`, así que la respuesta dice "no sé" en vez de publicar un token. Es el mismo problema que `appsettings.example.json` documenta para `Seed:*`: lo que no se ve, se pudre.                                                               | Validar con `ValidateOnStart` y tumbar el pod: la versión es información, no una precondición. Un pod que no arranca por no saber su versión es peor que uno que la desconoce.                                                                                                                                                                                       |
| 4   | **La respuesta es BFF: lo que la pantalla dibuja y nada más.** `buildId`, `buildNumber`, `commit` (los tres `string \| null`), `environment` (`IHostEnvironment.EnvironmentName`) y `startedAt` (instante UTC en que arrancó el proceso, capturado una vez). `startedAt` distingue "desplegué hace un minuto" de "este pod lleva tres días" sin abrir `kubectl`. Sin `service`, sin versión de .NET, sin hostname: no hay pantalla que los dibuje.                 | Devolver el `InformationalVersion` del assembly como fallback: con `Deterministic` y sin SourceLink es `1.0.0` fijo, un dato que parece versión y no identifica nada. Regla del repo: no inventar.                                                                                                                                                                  |
| 5   | **Anónimo y con la política `public` del rate limiter**, como `/openapi/v1.json` y `/scalar/v1` (`Program.cs:106-114`). El comentario de `Program.cs:48-51` pide atarla a todo endpoint público de lectura que se agregue; éste lo es. Anónimo porque el caso de uso principal es verificar un despliegue cuando el login puede estar roto, y porque `curl.exe` no tiene sesión.                                                                                       | Exigir sesión: un commit SHA de un repo privado no es un secreto, y el documento OpenAPI entero ya es público (`Program.cs:100-105`). Lo que sí se evita es revelar más que eso (decisión 4).                                                                                                                                                                         |
| 6   | **El frontend conoce su commit por `VITE_VERCEL_GIT_COMMIT_SHA`**, que Vercel inyecta en el build con el prefijo del framework cuando el proyecto tiene activo "Automatically expose System Environment Variables". Se lee en una función pura `readBuildInfo(env)` con el `env` por parámetro, mismo criterio que `resolveApiProxy` y `resolveApiUrl`: una prueba cuyo veredicto depende del `.env.local` de quien la corre no prueba nada.                        | Un `define` en `vite.config.ts` con `process.env.VERCEL_GIT_COMMIT_SHA`: exige declarar un global en un `.d.ts` y repetir el `define` en `vitest.config.ts`, y depende del **mismo** interruptor de Vercel. `git rev-parse` en el build: en Vercel la variable ya lo trae, y en local "dev" es suficiente.                                                            |
| 7   | **Dónde se muestra.** (a) Una línea apagada al final del menú de usuario (`user-menu.tsx`), después de "Cerrar sesión" y con separador propio: `web a1b2c3d · api 20261014.3 (e4f5a6b)`. (b) La misma línea en el pie del login, bajo el bloque de privacidad (`login-page.tsx:144-157`). El login es el lugar que sirve cuando la sesión no se puede abrir, que es justo cuando más se necesita saber qué está desplegado.                                        | Una pantalla "Acerca de" o una sección en "Mi perfil": una ruta, un ítem de menú y una página para dos líneas de texto. El pie del sidebar: `Sidebar` no tiene pie y en modo colapsado (64 px) no cabe.                                                                                                                                                             |
| 8   | **El frontend pide la versión de la API con TanStack Query**, `staleTime: Infinity`, `retry: false`, y **ante error no dibuja la parte de la API**: queda `web a1b2c3d · api —`. La versión de la web se dibuja siempre, sin red. Nunca un toast ni un estado de error visible: es un dato de diagnóstico, no una función del producto.                                                                                                                                | Mostrar "error al consultar la versión": en el login, con el backend caído, agregaría un segundo mensaje de error junto al de sesión y confundiría más de lo que informa.                                                                                                                                                                                             |
| 9   | **Feature nueva `src/features/version/`**, plana (menos de 8 archivos): `version.api.ts`, `build-info.ts`, `use-deployed-versions.ts`, `deployed-versions.tsx`. La consumen `components/app-shell/user-menu.tsx` y `features/auth/pages/login-page.tsx`; `sidebar.tsx:9` ya importa de una feature, así que hay precedente.                                                                                                                                             | Ponerlo en `src/components/`: tiene dos consumidores, así que calificaría, pero no es una primitiva sin dominio sino una feature con API propia. Ponerlo en `features/auth`: la versión no es autenticación.                                                                                                                                                         |
| 10  | **La versión también va al recurso de OpenTelemetry**: `service.version` pasa a ser `Deployment:BuildNumber` cuando existe, con el mismo `DeploymentInfo`. Hoy manda `1.0.0.0` (`QepObservability.cs:39-43`), que en Tempo y Grafana no sirve para correlacionar una regresión con un despliegue. Es una línea y usa lo mismo que el endpoint.                                                                                                                            | Dejarlo para después: el costo es una línea y el beneficio es poder filtrar trazas por build.                                                                                                                                                                                                                                                                       |

## Contrato

```http
GET /api/v1/version
```

Anónimo. `RequireRateLimiting(RateLimiterPolicies.Public)`: 120 por minuto por IP, `429` al
agotarse (`Program.cs:52-66`). Siempre `200 OK`:

```json
{
  "buildId": "4812",
  "buildNumber": "20261014.3",
  "commit": "e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3",
  "environment": "Production",
  "startedAt": "2026-10-14T13:05:41.1234567+00:00"
}
```

- `buildId`: `Build.BuildId` de Azure DevOps. Es el tag de la imagen desplegada
  (`azure-pipelines.yml:41`, `prod-deployment.yaml:34`). `null` si no está configurado.
- `buildNumber`: `Build.BuildNumber`. El pipeline no declara `name:`, así que es el formato por
  defecto `yyyyMMdd.r`: dice la fecha del build sin necesitar otro campo. `null` si no está.
- `commit`: `Build.SourceVersion`, SHA completo de 40 caracteres. El frontend lo recorta a 7.
  `null` si no está.
- `environment`: nombre del `IHostEnvironment`. `Production` en k8s
  (`prod-configMap.yaml:17`), `Development` en local.
- `startedAt`: `DateTimeOffset` UTC capturado al construir `DeploymentInfo`, que es singleton.

En local, sin la sección `Deployment`, los tres primeros son `null` y `environment` es
`Development`. El frontend dibuja eso como `api local`.

**Cómo consultarlo desde PowerShell:**

```powershell
curl.exe -s https://qep-api.qcode.co/api/v1/version
```

## Backend

### Estructura

```
src/Bootstrapper/Deployment/DeploymentOptions.cs    SectionName = "Deployment"; BuildId, BuildNumber, Commit (string?)
src/Bootstrapper/Deployment/DeploymentInfo.cs       singleton: resuelve las opciones, aplica la regla del token, fija StartedAt
src/Api/VersionEndpoints.cs                         MapVersionEndpoint: GET /api/v1/version, anónimo, rate limit public
src/Api/Program.cs                                  app.MapVersionEndpoint() junto a los health checks
src/Api/appsettings.example.json                    sección "Deployment" con los tres valores en null y el comentario de origen
k8s/prod-configMap.yaml                             las tres claves Deployment__* con tokens
src/BuildingBlocks/.../QepObservability.cs          serviceVersion desde DeploymentInfo (decisión 10)
README.md                                           fila en la tabla de "API implementada" y subsección "Versión desplegada"
```

`DeploymentOptions` declara `SectionName` a propósito: `ConfigurationExampleTests` descubre por
reflexión las clases de options con esa constante y exige que cada clave esté en
`appsettings.example.json`. Sin esa constante la prueba no la vería, y sin la sección en el
ejemplo la prueba la haría fallar.

Vive en `Bootstrapper` y no en `Api` por el mismo motivo que `QepHealthChecks`: es
composición de infraestructura, no un módulo de negocio, y `QepObservability` la necesita
(decisión 10) sin referenciar `Api`. `BuildingBlocks.Observability` no referencia
`Bootstrapper` —es al revés—, así que `AddQepObservability` recibe el `serviceVersion` por
parámetro (`string?`) y `AddQepPlatform` (`QepServiceCollectionExtensions.cs:45`) se lo pasa
después de construir `DeploymentInfo`. Sin valor cae al assembly, como hoy.

### `DeploymentInfo`

```csharp
public sealed class DeploymentInfo
{
    public string? BuildId { get; }
    public string? BuildNumber { get; }
    public string? Commit { get; }
    public string Environment { get; }
    public DateTimeOffset StartedAt { get; }
}
```

Regla de normalización, una función estática y pura para poder probarla sin host:
`Normalize(string? raw)` devuelve `null` si `raw` es nulo, vacío, sólo espacios, o empieza por
`#{` después de `Trim()` (decisión 3). En cualquier otro caso devuelve `raw.Trim()`.

`StartedAt = DateTimeOffset.UtcNow` en el constructor; el singleton se construye una vez en
`AddQepPlatform`, así que refleja el arranque del proceso y no la primera llamada.

### Endpoint

`src/Api/VersionEndpoints.cs`, mismo estilo que `AuthSessionEndpoints.cs`: una clase estática con
`MapVersionEndpoint(this IEndpointRouteBuilder)`, `.WithTags("Platform")`, `.AllowAnonymous()`,
`.RequireRateLimiting(RateLimiterPolicies.Public)`, `.Produces<VersionResponse>()`. El handler
recibe `DeploymentInfo` por inyección y devuelve `Results.Ok(new VersionResponse(...))`. El
record `VersionResponse` lleva el comentario de por qué esos campos y no otros (decisión 4): desde
el backend no se ve la pantalla que lo obligó.

Va en `Program.cs` justo después de `app.MapQepReadiness()` y antes de los módulos: es
infraestructura, como los health checks, y se lee junto a ellos.

### ConfigMap

Después de `ASPNETCORE_ENVIRONMENT`, con comentario en el mismo tono que el resto del archivo:

```yaml
  # Identidad del build que corre en el pod, para GET /api/v1/version. Los tres son variables
  # predefinidas del pipeline: Build.BuildId es el tag de la imagen (azure-pipelines.yml), así
  # que si lo que responde el endpoint no coincide con `kubectl get deploy -o jsonpath=...image`
  # alguien aplicó manifiestos por fuera del pipeline. Un token que la plantilla no resuelva
  # llega literal y el backend lo trata como ausente (DeploymentInfo.Normalize).
  Deployment__BuildId: "#{Build.BuildId}#"
  Deployment__BuildNumber: "#{Build.BuildNumber}#"
  Deployment__Commit: "#{Build.SourceVersion}#"
```

No hay secreto: un SHA y dos números de build.

## Frontend

### Estructura

```
src/features/version/
  build-info.ts               readBuildInfo(env): { commit: string | null } — VITE_VERCEL_GIT_COMMIT_SHA recortado a 7
  version.api.ts              VersionResponse, fetchApiVersion (GET /api/v1/version), versionQueryKey
  use-deployed-versions.ts    useQuery con staleTime Infinity, retry false; devuelve { web, api: 'loading' | 'error' | VersionResponse }
  deployed-versions.tsx       la línea: <DeployedVersions /> — texto apagado, sin interacción
src/components/app-shell/user-menu.tsx     separador + <DeployedVersions /> al final
src/features/auth/pages/login-page.tsx     <DeployedVersions /> bajo el bloque de privacidad
.env.example                               VITE_VERCEL_GIT_COMMIT_SHA documentado como "lo pone Vercel; no se fija a mano"
README.md                                  fila en la tabla de variables y nota del interruptor de Vercel
```

### `readBuildInfo`

```ts
export function readBuildInfo(
  env: Record<string, string | undefined>,
): BuildInfo {
  const sha = env.VITE_VERCEL_GIT_COMMIT_SHA?.trim()
  return { commit: sha ? sha.slice(0, 7) : null }
}
```

`DeployedVersions` la llama con `import.meta.env`. Sin SHA —`bun dev`, una build fuera de
Vercel— dibuja `web local`. Vite sólo expone al bundle variables con prefijo `VITE_`
(`README.md:153-154`), y Vercel se lo pone solo a sus variables de sistema cuando el proyecto
tiene activo el interruptor de la decisión 6.

### `fetchApiVersion`

`apiRequest<VersionResponse>('/api/v1/version')` desde `version.api.ts`, único punto de contacto
con el backend de esta feature. `GET`, así que `apiRequest` no agrega `X-Qep-Client`
(`api-client.ts:77`), y `X-Tenant-Id` no aplica: el endpoint no lleva tenant. En producción sale
directo a `qep-api.qcode.co` por `VITE_API_BASE_URL`; la CORS del backend admite `GET` desde
`qep.qcode.co` con credenciales (`prod-configMap.yaml:39`), y la cookie viaja aunque no haga
falta.

El tipo refleja el contrato y cita la fuente, como `SessionResponse` (`auth.api.ts:10`):

```ts
/** Mirrors qep-backend/src/Api/VersionEndpoints.cs. Los tres primeros son null cuando el pod
 *  no recibió la sección Deployment (local, o tokens sin resolver). */
export interface VersionResponse {
  buildId: string | null
  buildNumber: string | null
  commit: string | null
  environment: string
  startedAt: string
}
```

### `DeployedVersions`

Un `<p>` con `text-xs text-muted-foreground`, `aria-label="Versiones desplegadas"`, y el
texto:

| Estado de la API | Texto                                     |
| ---------------- | ----------------------------------------- |
| cargando         | `web a1b2c3d · api …`                     |
| error            | `web a1b2c3d · api —`                     |
| con datos        | `web a1b2c3d · api 20261014.3 (e4f5a6b)`  |
| datos en `null`  | `web a1b2c3d · api local`                 |

`web local` cuando `readBuildInfo` devuelve `null`. El SHA completo de la API va en `title` del
`<span>` para copiarlo. Es un `<p>`, no un ítem del menú: dentro de `DropdownMenuContent` no se
enfoca con las flechas ni dispara `onSelect`, y el bloque de "Cerrar sesión" sigue siendo la
última acción (`user-menu.tsx:110-116`, cuyo comentario explica por qué va solo).

En el menú va después del ítem de cerrar sesión, con `DropdownMenuSeparator` antes y `px-2
py-1.5`. En el login va al final de la `Card`, después del bloque de privacidad
(`login-page.tsx:144-157`), con `mt-6 text-center`.

## Pruebas

TDD: RED antes que GREEN, con evidencia literal de ambos.

### Backend

- **`tests/Bootstrapper/Bootstrapper.UnitTests/DeploymentInfoTests.cs`** — `Normalize`: `null`,
  `""`, `"   "` → `null`; `"#{Build.BuildId}#"` → `null`; `" 4812 "` → `"4812"`. El
  constructor con opciones vacías deja los tres campos en `null` y `StartedAt` dentro del
  segundo anterior.
- **`tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/VersionApiTests.cs`** — donde viven
  las pruebas transversales de la API (`CorsApiTests`, `ForwardedHeadersApiTests`,
  `ReadinessApiTests`), con la misma factoría que `ReadinessApiTests.cs:61-79` más
  `builder.UseSetting("Deployment:BuildId", "4812")` y los otros dos. Casos: (1) sin sesión ni
  `X-*` responde `200` con los tres valores y `environment = "Development"`; (2) con
  `Deployment:Commit = "#{Build.SourceVersion}#"` el cuerpo trae `commit: null`; (3) el `429`
  después de agotar la ventana `public`, como `ForwardedHeadersApiTests.ExhaustAsync` pero
  contra `/api/v1/version` — prueba que la política está puesta, no sólo el `AllowAnonymous`.
- **`tests/ArchitectureTests/.../ConfigurationExampleTests`** — se pone roja sola si la sección
  `Deployment` falta en `appsettings.example.json`. Es la evidencia RED de ese archivo: se agrega
  `DeploymentOptions` primero, se corre, se documenta la sección.
- **`tests/ArchitectureTests/.../ObservabilityConfigurationTests`** — un caso nuevo: con
  `Deployment:BuildNumber = "20261014.3"` el recurso lleva `service.version = "20261014.3"`; sin
  la sección sigue siendo la versión del assembly.
- **Regresión**: `dotnet restore --locked-mode`, `dotnet format --verify-no-changes`,
  `dotnet build --no-restore`, `dotnet test --no-build` (README § Verificación).

### Frontend

Runner: `bun run test <archivo>`; `fetch` stubeado por `setup.ts:64`.

- **`src/features/version/build-info.test.ts`** — sin variable → `commit: null`; vacía o sólo
  espacios → `null`; SHA de 40 → los primeros 7; ya corto → tal cual.
- **`src/features/version/version.api.test.ts`** — `fetchApiVersion` pega a `/api/v1/version`
  con `GET`, sin `X-Qep-Client` ni `X-Tenant-Id`, y devuelve el cuerpo tipado.
- **`src/features/version/deployed-versions.test.tsx`** — los cuatro estados de la tabla:
  cargando, error (`fetch` rechaza con `NetworkError`; no hay toast ni texto de error), con
  datos, y datos en `null` → `api local`. `web local` cuando `readBuildInfo` recibe `{}`.
- **`src/components/app-shell/app-shell.test.tsx`** — junto a los `describe('UserMenu — …')`
  (`:641`, `:734`): abrir el menú muestra la línea de versiones, está después de "Cerrar sesión",
  y no es un `menuitem` (no aparece en `getAllByRole('menuitem')`). Los casos existentes del menú
  siguen verdes con `GET /api/v1/version` respondiendo `200`.
- **`src/features/auth/pages/login-route.test.tsx`** — con `renderRoute('/login')` (`:124`): la línea se dibuja en el login; con el
  backend caído dibuja `api —` y el formulario sigue intacto.
- **Regresión**: `bun run test`, `bun run build`, `bun run lint`, `bun run format:check`.

## Orden de trabajo

### Backend (`qep-backend`)

1. Verificar la rama en ese momento (`git branch --show-current`); al escribir esto el árbol
   está en `main`. Rama `feature/version-desplegada` desde `main`. Baseline: los cuatro
   comandos de Verificación.
2. `DeploymentOptions` + `DeploymentInfo` con `DeploymentInfoTests` RED → GREEN.
   `ConfigurationExampleTests` roja → sección en `appsettings.example.json` → verde.
3. `VersionEndpoints` + `Program.cs` con `VersionApiTests` RED → GREEN.
4. `QepObservability` con el caso nuevo de `ObservabilityConfigurationTests`.
5. `prod-configMap.yaml`, README (tabla de API implementada + subsección).
6. Suite completa, format, build.

Commits: `feat(platform): exponer la versión desplegada en GET /api/v1/version`,
`feat(k8s): pasar el build al pod por el ConfigMap`, `docs(platform): …`. Conventional commits,
sin atribución de IA.

### Frontend (`qep-frontend`)

1. Verificar la rama en ese momento; al escribir esto el árbol está en `main` (**nunca se
   commitea sobre `main`**). Rama `feat/version-desplegada` desde `main`. Baseline: test, build,
   lint.
2. `build-info.ts` + test. `version.api.ts` + test.
3. `use-deployed-versions.ts` y `deployed-versions.tsx` con sus tests.
4. `user-menu.tsx` y `login-page.tsx`, con los tests de `app-shell.test.tsx` y
   `login-route.test.tsx`. **Al tocar `login-page.tsx` se corrige el voseo que ya está ahí**
   ("¿No tenés cuenta?", "aceptás", `:138`, `:151`): regla de los dos CLAUDE.md, se arregla
   cuando se toca el archivo.
5. `.env.example`, README. Suite completa, build, lint, format.

Commits: `feat(version): mostrar el commit de la web y el build de la API`, referenciando este
documento. Sin atribución de IA.

## Entrega

Los dos lados son independientes: **ningún orden rompe nada**.

- Frontend nuevo contra backend viejo: `GET /api/v1/version` responde `404`, `apiRequest` lo
  convierte en `ApiError`, la línea dice `api —`. Nada más cambia.
- Backend nuevo contra frontend viejo: el endpoint existe y nadie lo llama.

**Verificación en el primer despliegue del backend** (cierra la decisión pendiente 1):

```powershell
curl.exe -s https://qep-api.qcode.co/api/v1/version
```

Si `buildId` coincide con el tag de la imagen del Deployment, los tokens resolvieron. El tag se
lee sin tocar ningún Secret:

```powershell
kubectl -n prod-qep-backend get deploy qep-backend -o jsonpath='{.spec.template.spec.containers[0].image}'
```

Si los tres vienen en `null` con el pod recién desplegado, la plantilla no resolvió los tokens
y entra el plan B de la decisión 2.

**Verificación en el primer despliegue del frontend** (cierra la decisión pendiente 2): abrir
`qep.qcode.co/login` y comprobar que la línea dice `web <7 caracteres>` y no `web local`. Si dice
`local`, el interruptor de Vercel está apagado: Settings → Environment Variables →
"Automatically expose System Environment Variables", y redeploy.

## Decisiones pendientes

1. **Si `k8s/deploy.yml@k8sTemplates` resuelve `#{Build.BuildId}#`, `#{Build.BuildNumber}#` y
   `#{Build.SourceVersion}#`.** La plantilla vive en `devops/pipeline-templates` (Azure DevOps)
   y no se ve desde este repo. Las variables predefinidas existen en todo job como
   `BUILD_BUILDID`, `BUILD_BUILDNUMBER` y `BUILD_SOURCEVERSION`; que el paso de reemplazo de
   tokens las vea depende de cómo esté escrito. Se cierra con la verificación de "Entrega".
   **Plan B** si no resuelven: agregar las tres como variables del pipeline en
   `azure-pipelines.yml` (`- name: BUILD_ID  value: $(Build.BuildId)`, etc.) y apuntar los
   tokens a esos nombres; las variables declaradas en el YAML sí las reemplaza cualquier paso de
   tokens que ya reemplaza las del grupo `Backend-prod`. Sólo si eso tampoco sirve, el
   `--build-arg` en la plantilla compartida, que exige tocar el otro repo.
2. **Si el proyecto de Vercel tiene activo "Automatically expose System Environment
   Variables".** Es el default en proyectos nuevos pero no se puede confirmar desde el repo. Se
   cierra con la verificación de "Entrega"; el costo de que esté apagado es la línea `web local`
   en producción, nunca una build rota.
3. **Branch en la respuesta.** `Build.SourceBranchName` está disponible y costaría una clave
   más, pero el pipeline sólo despliega `main` a `prod` (`azure-pipelines.yml:1-4`, `:15`) y
   no hay ambiente `test` desplegado hoy. Se agrega el día que exista un segundo ambiente.

## Fuera de alcance

- Fecha del build del frontend: Vercel no la expone como variable; la tiene el dashboard. El
  SHA identifica la build sin ambigüedad.
- Mostrar la versión del tenant, de la base o de las migraciones aplicadas.
- Avisar en la pantalla cuando hay una versión nueva de la web ("recarga para actualizar"): es
  otra feature, con su propio polling y su propia UX.
- Versionar con semver (`package.json` sigue en `0.0.0`; ningún `.csproj` declara `Version`).
  No hay releases etiquetados que lo justifiquen; el commit es la versión.
- Un ambiente `test`: el pipeline lo contempla por nombre de rama pero no hay manifests
  `k8s/test-*.yaml`.
