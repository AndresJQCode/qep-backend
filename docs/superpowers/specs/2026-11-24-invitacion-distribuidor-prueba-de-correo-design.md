# Invitación de distribuidor: el link del correo prueba el buzón — diseño (backend)

Fecha: 2026-11-24. Estado: aprobado por el owner (2026-11-24). Spec hermano en `qep-frontend`, mismo nombre.
**Reemplaza en parte** `2026-11-19-onboarding-distribuidor-por-link-design.md`: la decisión «cuenta sin contraseña o sin usuario
siempre exige el código» y la regla W1 «el link que se copia es el mismo del correo».

## Intención

Quien llega desde el correo de la invitación ya demostró que controla ese buzón. Pedirle además un código de 6 dígitos al mismo
correo es un paso de más. El link del correo lleva un secreto propio que permite saberlo.

## Objetivos

- Crear la cuenta desde el link del correo, con el correo invitado, **sin código**.
- El link de «Copiar» y el de WhatsApp siguen pidiendo código (decisión del owner, 2026-11-24).

## No objetivos

- Cambiar `accept`, `session` (Google), `email-code`, las invitaciones de miembros o la vigencia de 72 h.
- Mostrar al mayorista por qué canal abrió el distribuidor.

## Anti-objetivos

- Que el mayorista, o quien tenga el link copiado o de WhatsApp, pueda fijar una contraseña a la cuenta de un correo que no
  controla.
- Que el secreto aparezca en logs, auditoría, mensajes de Mensajería o respuestas de la API.

## Enfoques

- **A. Marca en la URL (`?source=email`).** Descartado: cualquiera la escribe a mano, incluido el mayorista, que tiene el link.
- **B. Secreto aleatorio sólo en el correo (elegido).** No se puede adivinar ni deducir del link copiado. Cuesta una columna,
  una migración y un campo en el payload del outbox.
- **C. Quitar el código para todos los links.** Descartado por el owner: quien tenga el link crearía una cuenta con cualquier
  correo.

## Restricciones verificadas en el código

- Correo, WhatsApp y «Copiar» comparten hoy un token; en base sólo vive su SHA-256 (`DistributorInvitation.TokenHash`).
- Invitar y «Reenviar» publican `wholesale.distributor-invited.v1` (correo); `WholesaleInvitationDeliveryWorker` arma el link
  con `AccountLinks.WholesaleInvitation`. El aviso de WhatsApp es otro evento (`DistributorWhatsAppNotice.QueueAsync`).
- Editar el correo del distribuidor **no** rota el token (`Distributor.ChangeProfile`).
- `WholesaleInvitationPasswordSession.OpenAsync` exige código y términos a toda cuenta sin contraseña; sin código válido
  responde el mismo 401 `identity.credentials.invalid` que una contraseña errada.

## Diseño

### Contrato

- Link del correo: `{AppBaseUrl}/wholesale-invitations/{token}?ev={prueba}`. «Copiar», la respuesta de invitar
  (`invitationUrl`) y WhatsApp siguen sin `ev`.
- `POST …/wholesale-invitations/{token}/session-with-password` acepta `emailProof` (opcional).
- El `GET` de la invitación no cambia.

### La prueba

- Se genera con `InvitationTokens.Generate()`, distinta del token, al invitar y al reenviar. En base sólo su hash:
  `distributor_invitations.email_proof_hash char(64)`, nulo (migración).
- Viaja en claro sólo en el payload de `wholesale.distributor-invited.v1`, como campo opcional `emailProof`. Sin él, el
  consumidor arma el link como hoy.
- Se anula cuando deja de corresponder al buzón: «Copiar» (rota el token) la deja nula; «Reenviar» pone una nueva; **cambiar el
  correo** del distribuidor la deja nula (sin esto, quien tiene el correo viejo podría crear la cuenta del nuevo).

### Cuándo se salta el código

Sólo si se cumplen todas: invitación pendiente, `emailProof` coincide con el hash vigente y el correo del cuerpo, normalizado
como `Distributor.Email`, es el invitado. Entonces la cuenta se crea igual que con un código válido (correo verificado,
contraseña, términos). Los términos se siguen exigiendo.

En cualquier otro caso la prueba se ignora y rige el flujo actual: sin código, 401 `identity.credentials.invalid`. La respuesta
no distingue prueba errada de código errado. Una cuenta con contraseña sigue siendo un inicio de sesión: la prueba no la toca.

### Riesgo aceptado

Quien reenvíe el correo de invitación entrega el acceso a ese paso: el destinatario puede crear la cuenta del correo invitado
mientras la invitación siga pendiente y la cuenta no tenga contraseña. Lo acotan las 72 h, el uso único de la invitación y la
rotación con «Copiar» o «Reenviar».

## Despliegue

En cualquier orden. Frontend viejo ignora `ev` y pide código; frontend nuevo contra backend viejo recibe 401 y cae al código.
Las invitaciones ya enviadas no tienen prueba y piden código.

## Pruebas

- Con prueba válida y correo invitado: crea la cuenta sin código y registra los términos.
- Con prueba válida y otro correo, prueba errada, o anulada por «Copiar», «Reenviar» o cambio de correo: 401, sin usuario.
- Con cuenta que ya tiene contraseña: la prueba no permite cambiarla ni entrar con otra.
- Términos ausentes o viejos con prueba válida: 422, sin usuario.
- El link del correo lleva `ev`; `invitationUrl` y el aviso de WhatsApp no. La prueba no aparece en auditoría ni en logs.

## Queda al constructor

Nombres internos, dónde se compara la prueba (Wholesale la verifica; `WholesaleInvitationPasswordSession` sólo recibe el
resultado) y la forma de la migración.
