# `ACC-03` — Preferencias de apariencia por usuario y tenant

> **Estado:** **In Progress** — abierto el 2026-08-18. Núcleo construido y verificado;
> las pruebas de integración están **bloqueadas por `SDD-CT-20`**
> **Módulo:** `account` — ficha y gate en `qep-frontend/sdd/03-modulos/account/`
> **Depende de:** `ACC-00` (gate **cerrado** el 2026-08-18, con el contrato `v1` declarado)
> **Repos afectados:** `qep-backend` únicamente
> **Habilita:** `ACC-01` en `qep-frontend`, que no puede empezar sin este contrato

## Objetivo

Que la elección de apariencia de una persona **sobreviva al navegador**: que se guarde contra
su cuenta, separada por tenant, y se pueda leer y escribir por contrato público.

## Por qué el ID es `ACC` si el trabajo es en Identity

Mismo camino que `CAT-05` y `CAT-09` con `Storage`: **la capability no lleva prefijo propio.**
`registro-de-modulos.md` clasifica a **Identity** como capability de plataforma —se consume, no
se reescribe— y `apertura-de-modulo.md` prohíbe abrirle módulo. El slice lleva el ID del módulo
de producto que lo necesita, que es `account`.

Precedente directo en este mismo producto: `AUTH-04`, `AUTH-05` y `AUTH-11` tocaron Tenancy e
Identity con prefijo `AUTH`.

`ACC-01` y `ACC-02` ya están reservados para el frontend, así que este slice toma `ACC-03`. **El
número no indica orden de ejecución:** éste va primero, porque sin contrato no hay UI que lo
consuma. Los IDs no se renumeran.

## Lo que se construye

Una entidad nueva en Identity y dos endpoints. Nada más.

### Persistencia

Tabla `identity.user_preferences`, con **clave primaria compuesta `(user_id, tenant_id)`**, que
es la resolución de `SDD-OD-17`.

| Columna       | Tipo           | Nota                                                            |
| ------------- | -------------- | --------------------------------------------------------------- |
| `user_id`     | `uuid`         | PK compuesta. FK a `identity.users` con `CASCADE`               |
| `tenant_id`   | `uuid`         | PK compuesta. **Sin FK** — ver abajo                            |
| `color_scheme`| `varchar(32)`  | Identificador del esquema. Sin catálogo cerrado en el backend   |
| `mode`        | `varchar(10)`  | `light` o `dark`, cerrado                                        |
| `updated_at`  | `timestamptz`  | Cuándo se cambió por última vez                                  |

**`tenant_id` no lleva clave foránea, y es a propósito.** `Tenancy` vive en su propio esquema y
ningún módulo referencia tablas de otro — es la regla que `ArchitectureTests` protege y que la
ficha de cada módulo repite. La integridad no se pierde: el `tenant_id` que llega siempre pasó
por la verificación de membresía descrita abajo, así que no puede escribirse una preferencia
para un tenant al que el usuario no pertenece.

### Contrato

Declarado en `ACC-00` y transcrito acá con su fuente, no de memoria.

| Método | Ruta                       | Autorización                  | Respuestas                 |
| ------ | -------------------------- | ----------------------------- | -------------------------- |
| `GET`  | `/api/v1/auth/preferences` | cookie de sesión, sin permiso | `200`, `401`, `403`        |
| `PUT`  | `/api/v1/auth/preferences` | cookie de sesión, sin permiso | `200`, `401`, `403`, `422` |

Cuerpo en los dos sentidos: `{ "colorScheme": "botanical", "mode": "light" }`.

### El tenant sale del claim, no de la ruta

`ExternalClaimsTransformation` (`src/Bootstrapper/Authentication/`) ya resuelve `X-Tenant-Id` y
**sólo agrega el claim `tenant_id` cuando `ResolvePermissionsAsync` devuelve permisos** — que
devuelve `null` cuando no hay membresía activa en ese tenant.

Dos consecuencias, y las dos son la razón del diseño:

1. **La verificación de membresía ya está construida.** Leer el claim es leer una membresía
   viva. No se reimplementa nada, y no hace falta que Identity consulte a Tenancy.
2. **Una ruta sin `tenantId` no puede desalinearse del tenant autenticado.** Este repositorio ya
   corrigió una vez ese defecto (`fix(CLI-01): valida tenant de la ruta contra el tenant
   autenticado`). Acá la clase de bug no existe porque no hay dos fuentes que comparar.

## Criterios de aceptación

| ID              | Criterio                                                                                                                |
| --------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `CA-ACC-03-01`  | `GET` de un usuario que nunca eligió devuelve `200` con `botanical` / `light` — el default, no `404`                      |
| `CA-ACC-03-02`  | `PUT` con un cuerpo válido devuelve `200`, y un `GET` posterior devuelve exactamente lo guardado                        |
| `CA-ACC-03-03`  | `PUT` es upsert idempotente: dos veces el mismo cuerpo devuelve `200` las dos y deja **una sola fila**                    |
| `CA-ACC-03-04`  | **Aislamiento por tenant:** el mismo usuario en dos tenants guarda dos preferencias independientes; cambiar una no toca la otra |
| `CA-ACC-03-05`  | Sin cookie de sesión, los dos endpoints devuelven `401`                                                                  |
| `CA-ACC-03-06`  | Con sesión válida pero **sin claim de tenant** —header ausente, o sin membresía activa— los dos devuelven `403` genérico, sin distinguir cuál de los dos casos es |
| `CA-ACC-03-07`  | `PUT` con `mode` distinto de `light` o `dark` devuelve `422` con código de dominio `identity.preference.mode.invalid`      |
| `CA-ACC-03-08`  | `PUT` con `colorScheme` que viola la forma `^[a-z0-9-]{1,32}$` devuelve `422` con código `identity.preference.scheme.invalid` |
| `CA-ACC-03-09`  | `PUT` con un `colorScheme` **desconocido pero de forma válida** devuelve `200`: el catálogo es del frontend, y el backend no lo duplica |
| `CA-ACC-03-10`  | La operación **no** escribe auditoría ni emite evento de outbox. Una preferencia visual no es operación sensible         |
| `CA-ACC-03-11`  | `IdentityLayerTests` sigue verde: la entidad nueva no rompe las reglas de capas                                          |

## Fuera de alcance

- **La UI.** Es `ACC-01`, en el otro repositorio.
- **El catálogo de esquemas.** La ficha de `account` declara que es suyo. Acá se valida forma,
  no pertenencia.
- **Un tercer modo "Sistema"** (`SDD-OD-20`, abierta, no bloquea). Si se agrega, amplía el
  conjunto cerrado de `mode` en un slice propio.
- **El flash previo al primer paint** (`SDD-OD-19`, abierta). Es un problema de cliente y
  bloquea a `ACC-01`, no a éste.
- **Auditoría y eventos.** Declarado explícitamente en `CA-ACC-03-10`, no omitido.

## Plan TDD

RED antes que GREEN, con evidencia literal de los dos en el ledger.

1. **RED unitario** en `Modules.Identity.UnitTests`: la entidad `UserPreference` no existe;
   pruebas de creación, cambio y validación de `mode`.
2. **RED de integración** en las pruebas de Identity: los endpoints no existen todavía →
   `404`, contra los `200`/`403`/`422` que los `CA` esperan.
3. **GREEN**: entidad, configuración de EF, migración, endpoints y registro.
4. Suite completa del backend, `ArchitectureTests` incluido.

## Corrección al propio spec, 2026-08-18

Este documento nombraba los códigos de error `account.preference.*`. **El código los emite
Identity**, y la convención verificada del repositorio es prefijo por módulo que emite:
`identity.email.invalid`, `identity.provider_link.conflict`, `tenancy.slug.taken`,
`catalog.tax_rate.in_use`. Se corrigieron a `identity.preference.*` — gana el código, y la
convención existente es código.

## Evidencia

**RED**, en `UserPreferenceTests.cs` contra una entidad que no existía:

```txt
error CS0103: El nombre 'UserPreference' no existe en el contexto actual
error CS0103: El nombre 'ThemeMode' no existe en el contexto actual
```

**GREEN unitario:**

```txt
Correctas! - Con error: 0, Superado: 19, Omitido: 0, Total: 19 - Modules.Identity.UnitTests.dll
```

Eran 13 antes de este slice.

**Suite de todo lo que compila** —8 proyectos, `201` pruebas, `0` fallos—, con
`ArchitectureTests` en `17/17`, que es `CA-ACC-03-11`. `dotnet build src/Api/Api.csproj` →
`Compilación correcta, 0 Errores`.

**Migración** `20260818153515_AddUserPreferences`, revisada a mano: crea sólo
`identity.user_preferences`, con PK compuesta, FK a `users` con `CASCADE` y **ninguna FK
hacia Tenancy**.

**Corrección hecha dentro del ciclo.** Una prueba propia daba por malformado el esquema
`"UPPER"`. La convención de este dominio —`User.NormalizeEmail`, `NormalizeProvider`— es
**normalizar** y aceptar, no rechazar. Se corrigió la prueba, no el código.

## Lo que falta para cerrar

**`SDD-CT-20` bloquea la evidencia de integración.** Ningún proyecto `*.IntegrationTests` de
este repositorio compila: `error NU1903` por `SSH.NET 2025.1.0`, transitivo de
`Testcontainers`, con el aviso de auditoría de NuGet tratado como error. Verificado ajeno a
este slice con `git stash`.

Sin eso no se pueden verificar `CA-ACC-03-01` a `-10`: todos necesitan API levantada y base
real. Por eso este slice queda `In Progress` y no `Complete` — el núcleo está construido, pero
**los criterios de aislamiento y de error no están probados todavía**, y no se declaran
cumplidos sin evidencia.

Falta también aplicar la migración contra una base real y el runtime del owner.
