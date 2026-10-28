# Registro y login con contraseña — pendientes del backend

Estado al 2026-10-27: las 17 tareas del plan están implementadas y revisadas, incluida la
revisión final de toda la rama, en `feature/registro-con-contrasena` (HEAD `07dc0f2d`).
**Ninguna prueba se ejecutó**: por decisión del owner, los agentes sólo compilaron. La rama no
está mergeada ni publicada.

## 1. Decisión abierta — pre-registro de un email ajeno (I3)

**Estado: PENDIENTE DE RESOLVER. No se cambió código.**

Un atacante llama a `POST /auth/register-tenant` con el email de la víctima y una contraseña
propia. Se crea el usuario con la credencial del atacante, sin verificar. Si la víctima abre el
mail de verificación que no pidió y hace clic, `EmailVerifiedAt` queda fijado y **la contraseña
del atacante pasa a ser válida** para esa cuenta.

- El spec cierra sólo la variante con Google: vincular Google borra la contraseña
  (`GoogleLinkingClearsPasswordTests`). Esta variante no está cubierta.
- Mitigación actual: la víctima puede recuperar la cuenta con «Olvidé mi contraseña», porque el
  reset revoca las sesiones. El rate limit `Credentials` (10 por minuto por endpoint e IP) frena
  el abuso masivo, no el caso dirigido.
- Opciones:
  1. Aceptarlo como riesgo residual documentado en el spec.
  2. Invalidar o rotar la credencial existente al verificar, y obligar a crear una contraseña
     tras el clic. Cambia el contrato de `/auth/verify-email` y la pantalla del frontend.
  3. Otra que defina el owner.

## 2. Ejecutar las pruebas (bloquea el merge)

Detener `Api.exe` antes de empezar. Como mínimo:

```powershell
dotnet test tests/Modules/Identity/Modules.Identity.UnitTests --filter "FullyQualifiedName~PasswordRulesTests|FullyQualifiedName~PasswordLoginServiceTests|FullyQualifiedName~PasswordCredentialVerifierTests"
dotnet test tests/Modules/Identity/Modules.Identity.IntegrationTests --filter "FullyQualifiedName~PasswordAuthApiTests|FullyQualifiedName~InvitationPasswordApiTests"
dotnet test tests/Modules/Notifications/Modules.Notifications.UnitTests --filter "FullyQualifiedName~AccountEmailTemplatesTests|FullyQualifiedName~InvitationEmailTemplateTests"
dotnet test tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests --filter "FullyQualifiedName~AuthSessionApiTests|FullyQualifiedName~RealAuthenticationApiTests|FullyQualifiedName~InvitationApiTests"
dotnet test tests/ArchitectureTests/ArchitectureTests
```

Después, la suite completa. Fallas que ya existían antes de esta rama y no son de este trabajo:

- `OrphanUserCleanupTests`: 2 pruebas de retry (timeout de 15 s).
- `OperatorConsoleApiTests.AnOperatorListsTenantsWithTheSummaryAndSearches`: esperaba 2 tenants
  y recibe 4. Es el seed de user-secrets que se cuela en las pruebas.

Supuestos que sólo se confirman ejecutando:

- `ANonSlugFailureCreatingTheTenantLeavesNoUserBehind` espera un 500 (`server.unexpected`).
- La prueba de concurrencia de verify-email no fuerza el solapamiento de las dos requests.

## 3. Deploy

- Definir la variable `NOTIFICATIONS_APP_BASE_URL=https://qep.qcode.co` en el pipeline. Sin
  ella, la API falla la validación de URL al arrancar, porque `prod-configMap.yaml` la
  referencia.
- Desplegar el backend antes que el frontend.

## 4. Seguimientos menores (no bloquean el merge)

- El reset guarda la contraseña y revoca las sesiones en dos commits separados. Si algo falla
  entre los dos, las sesiones viejas siguen vivas. El plan exige ese orden.
- Faltan pruebas para un token de reset reutilizado o vencido, y para un body ausente en
  aceptar invitación.
- Aceptar una invitación con contraseña guarda la credencial, la verificación y la activación
  antes de aceptar la invitación, así que el flujo no es atómico. Dos aceptaciones simultáneas
  pueden terminar en un 500.
- No queda auditoría cuando una invitación crea una credencial.
- `HasPasswordAsync` carga la credencial entera sólo para saber si existe.
- Forgot-password tarda distinto para un email existente que para uno desconocido. Es una
  fuga por tiempo; el plan la acepta.
- La carrera por el slug deja un oráculo de existencia del email (aceptado por el plan, acotado
  por el rate limit).
- Si la request se cancela después de que el tenant ya se guardó, el usuario se borra y el
  tenant queda solo. Es un caso remoto.
- Si `DiscardCreatedUserAsync` falla, el error se registra en el log y el usuario queda huérfano.
- `last_error` guarda `exception.Message` del canal de envío.
- Un login fallido se audita contra el usuario víctima (lo exige el brief).
- El chequeo de secretos sobre `platform.request_failures` es una guarda de regresión: el texto
  de una excepción rara vez incluye el body de la request.
- E2E del flujo con contraseña: el backend de e2e todavía no captura mails.
