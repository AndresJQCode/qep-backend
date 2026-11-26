---
paths:
  - "global.json"
  - "Directory.Build.props"
  - "Directory.Packages.props"
  - "Dockerfile"
  - "k8s/**"
  - "src/Api/Program.cs"
  - "src/Api/appsettings*.json"
  - "**/Migrations/**"
  - "tests/ArchitectureTests/**"
---

# Build, migraciones, rollout y despliegue

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **Toda migración es expand/contract: durante el rollout, pods viejos atienden contra el esquema nuevo** (spec
  2026-10-29, D-5). Agregar va en una versión; quitar, renombrar o volver `NOT NULL` lo que el código viejo escribe, en
  otra. Una migración que rompe al código viejo se despliega con corte (`Recreate` o el backend a cero réplicas), y el
  handoff del spec dice si es aditiva; si no lo dice, no lo es. Las reglas completas:
  [README § Migraciones con pods viejos y nuevos](../../README.md#migraciones-con-pods-viejos-y-nuevos-expandcontract).
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
