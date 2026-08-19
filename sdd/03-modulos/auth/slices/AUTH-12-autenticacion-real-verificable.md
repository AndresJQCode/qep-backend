# `AUTH-12` — Devolver a verde la suite de autenticación real

> **Estado:** **In Progress** — abierto el 2026-08-18
> **Módulo:** `auth` — `En curso`. Ficha en `qep-frontend/sdd/03-modulos/auth/`
> **Depende de:** nada. `AUTH-01`..`AUTH-11` están `Complete`
> **Repos afectados:** `qep-backend` únicamente
> **Corre el alcance del gate:** no. No agrega contrato: devuelve a verde la verificación de uno
> que ya existe
> **Primer slice de `auth` con spec en el ledger de backend** (`SDD-ADR-08`). Estrena
> `qep-backend/sdd/03-modulos/auth/slices/`

## Objetivo

Que `RealAuthenticationApiTests` vuelva a verde **y pruebe lo que dice probar**.

## De dónde sale este slice

**`SDD-CT-14`, abierta desde antes de `CAT-03`.** Cinco pruebas rojas que se arrastran hace cuatro
slices y que cada regresión verifica «por nombre, no por conteo» — un ritual que existe únicamente
porque están rotas.

## Qué está sin verificar hoy, que es lo que importa

Las cinco no son pruebas cualquiera. Son **las únicas** que ejercitan la rama de autenticación
real: el resto de la suite de integración del repositorio corre con
`Authentication:UseDevelopmentStub` en `true`, así que **ninguna otra toca**
`SessionCookieAuthenticationHandler`, el pinning del esquema `GoogleBearer`,
`RequireCsrfHeaderMiddleware` ni `SessionRevocationWorker` (lo dice el comentario de cabecera del
propio archivo).

| Prueba | Qué garantía queda sin verificar mientras está roja |
|---|---|
| `MutatingRequestWithoutCsrfHeaderIsRejected` | La **protección CSRF** |
| `LogoutRevokesTheSessionCookie` | Que cerrar sesión **cierre la sesión** |
| `SuspendingMembershipRevokesTheMembersActiveSession` | Que suspender a un miembro **lo eche** |
| `RoleDowngradeRemovesPermissionsOnTheNextRequest` | Que bajar un rol **quite los permisos** |
| `SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken` | Que la cookie autentique |

**No están «rotas»: están ciegas.** Ninguna llega a su aserción real.

## El síntoma, medido el 2026-08-18

`Superado: 1, Con error: 5, Total: 6`. Las cinco fallan con **`Unauthorized`**, en cuatro puntos
distintos del flujo:

```txt
SuspendingMembershipRevokesTheMembersActiveSession   Expected: Created   / Actual: Unauthorized
(otra)                                               Expected: Created   / Actual: Unauthorized
(otra)                                               Expected: OK        / Actual: Unauthorized
(otra)                                               Expected: NoContent / Actual: Unauthorized
MutatingRequestWithoutCsrfHeaderIsRejected           Expected: OK        / Actual: Unauthorized
```

**Una sola respuesta en cinco lugares distintos sugiere una sola causa**, no cinco defectos. La de
CSRF ni siquiera llega a probar CSRF: muere en `GetSettingsEtagAsync` (línea 330), que es
**preparación**.

> **El síntoma cambió respecto de lo documentado, y eso se corrige acá.** El `CLAUDE.md` del
> repositorio y las entradas viejas del ledger describen `SDD-CT-14` como «5 pruebas fallan con
> `Expected: Created / Actual: Unauthorized`». Hoy **sólo dos** fallan así; las otras tres fallan
> más adelante, con `OK` y `NoContent`. O sea que el registro **a veces sí funciona** y lo que
> falla es el uso posterior de la sesión. Quien haya asumido el síntoma viejo iba a diagnosticar
> el lugar equivocado.

## El falso verde, que es el hallazgo que justifica mirar esto ahora

`GoogleBearerTokenCannotAuthenticateAnOrdinaryEndpoint` es **la única de las 6 que pasa**. Y su
aserción es:

```csharp
Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
```

**Pasa porque espera exactamente lo que está roto.** Si todo el flujo responde `Unauthorized`, esa
prueba queda verde sin verificar nada — y lo que debería verificar es un bypass de seguridad: que
un bearer de Google **no** pueda autenticar un endpoint ordinario, que es la razón de ser de la
separación de esquemas `GoogleBearer`/`QepSession`.

Mientras las otras cinco estén rojas, **esa prueba no vale como evidencia de nada**. Es la clase de
prueba que este proyecto ya cazó una vez: en `CAT-05`, dos pruebas pasaban por accidente porque un
`UPDATE ... SET status = 3` sobre una columna `varchar` guardaba `'3'` y `Enum.Parse` lo aceptaba.

## Fuera de alcance

- **Cambiar el contrato de autenticación.** Este slice no agrega ni quita garantías: hace
  verificable las que ya se declararon en `AUTH-01`..`AUTH-11`.
- **Migrar el resto de la suite a autenticación real.** Que las demás pruebas usen el stub es una
  decisión tomada y no se revisa acá.
- **`SDD-OD-23`** —el filtro `status` silencioso— y **`SDD-CT-07`** —registro que deja usuario sin
  membresía—. Son otros defectos, con su propia fila.

## Criterios de aceptación

| ID | Criterio |
|---|---|
| `CA-AUTH-12-01` | **La causa está diagnosticada y escrita** en la evidencia de cierre: archivo, línea y por qué la sesión no autentica. Sin esto el resto es adivinar |
| `CA-AUTH-12-02` | `RealAuthenticationApiTests` cierra en **6 de 6**, sin `[Skip]`, sin bajar una aserción y sin tocar lo que las pruebas afirman |
| `CA-AUTH-12-03` | **`GoogleBearerTokenCannotAuthenticateAnOrdinaryEndpoint` deja de ser un falso verde:** con las demás en verde, se verifica que sigue pasando **por su razón** — un cliente autenticado por cookie sí entra al mismo endpoint, y con bearer de Google no |
| `CA-AUTH-12-04` | Cada una de las cinco llega a **su** aserción: se comprueba que ninguna sigue muriendo en un helper de preparación |
| `CA-AUTH-12-05` | La regresión de toda la solución queda **sin fallos**, y `SDD-CT-14` se cierra en el ledger |
| `CA-AUTH-12-06` | Si la causa está en el código de producción y no en las pruebas, la corrección lleva **su propia prueba** que falla sin el arreglo |

`CA-AUTH-12-01` va primero a propósito: el modo de falla ya cambió una vez sin que nadie lo
notara, así que el diagnóstico es entregable, no preámbulo.

`CA-AUTH-12-03` es el que impide «arreglar» esto dejando el falso verde en su lugar.

## Pruebas requeridas

| Nivel | Qué verifica |
|---|---|
| Integración | Las 6 de `RealAuthenticationApiTests`, en verde y por su razón |
| Regresión | Toda la solución, **sin fallos conocidos** por primera vez |
| Runtime | Sólo si la causa resulta estar en producción. Si es de las pruebas, no aplica y se declara |

**TDD:** este slice arranca con el RED ya escrito —son las cinco rojas—. Lo que hay que producir es
el GREEN sin tocar lo que afirman. **Bajar una aserción para que pase es exactamente lo que este
slice no puede hacer**, y `CA-AUTH-12-02` lo dice.

## Riesgos

| Riesgo | Cómo se detecta |
|---|---|
| **«Arreglarlo» relajando una aserción o marcando `[Skip]`** | `CA-AUTH-12-02` y la revisión. Una suite verde que no prueba nada es peor que una roja: deja de avisar |
| Dejar el falso verde intacto | `CA-AUTH-12-03` |
| Diagnosticar sobre el síntoma viejo del `CLAUDE.md`, que ya no es el real | El síntoma medido está arriba, con fecha |
| Que la causa esté en producción y se parchee en las pruebas | `CA-AUTH-12-06` |

## Evidencia de cierre

_Pendiente — el slice está abierto._
