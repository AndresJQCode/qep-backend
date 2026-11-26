# Roles por tenant: `admin` como único rol de sistema

Fecha: 2026-11-25. Estado: aprobado por el owner (2026-11-25).

## Intención

Hoy hay seis roles de sistema definidos en código (`SystemRoleKeys`): `admin`, `advisor`, `billing`, `cashier`,
`scheduling_manager` y `scheduling_provider`. Un tenant no puede cambiar sus permisos. El owner quiere que sólo `admin` siga
siendo de sistema, y que los otros cinco se siembren en cada tenant para que cada uno los reconfigure.

## Metas

- `admin` es el único `RoleDefinition` y la única clave reservada.
- Cada tenant tiene los otros cinco como filas de `authorization.roles`, editables y borrables con la pantalla de roles de hoy.
- Nadie pierde ni gana permisos al desplegar.
- Un tenant nuevo nace con los cinco.
- Un rol no se puede borrar mientras alguien pueda seguir usándolo.

## No son metas

- Cambiar los permisos que concede hoy cada rol.
- Crear roles nuevos, un botón de «restaurar valores de fábrica» o traducir los nombres de los roles.
- Cambiar `EnsureSomebodyKeepsManagingAsync`, que sólo mira el catálogo de sistema. Puede rechazar de más, nunca de menos.

## Antimetas

El cambio fracasa, aunque funcione, si:

- un miembro pierde acceso al desplegar;
- un arranque revive un rol que el tenant borró o pisa uno que editó;
- una función nueva le agrega permisos por su cuenta a un rol de un tenant.

## Enfoques

| Enfoque | A favor | En contra |
| --- | --- | --- |
| **1. Sembrar tras el alta y reconciliar al arrancar (elegido por el owner)** | Una sola definición de las plantillas; se repara solo; no mezcla transacciones entre módulos | Tabla nueva; si falla en un alta, el tenant queda sin los roles hasta el siguiente arranque |
| 2. Sembrar en la transacción del alta | Nunca hay un tenant sin roles | Amarra las transacciones de Tenancy y Authorization; los tenants actuales necesitan una migración que repite los permisos en SQL |
| 3. Sembrar en la primera lectura | No toca alta ni arranque | Escribe en la ruta que autoriza cada request, con carreras entre requests y pods |

## Decisiones del owner

1. Sólo `admin` es de sistema; los otros cinco se siembran y cada tenant los reconfigura.
2. Una función nueva **no** agrega permisos a los roles de tenants existentes. El administrador marca la casilla. Los tenants
   nuevos nacen con la plantilla actualizada.
3. Enfoque 1.
4. Los tenants existentes reciben los roles con la misma clave y los mismos permisos. Un rol borrado no se vuelve a crear.
5. No se puede borrar un rol que tenga cualquier miembro, incluidos suspendidos e invitados.

## Diseño

### Plantillas

- Las cinco definiciones salen de `IRoleCatalog` y pasan a ser **plantillas de siembra**: un tipo distinto de
  `RoleDefinition`, con clave, nombre, descripción y permisos, iguales a los de hoy en `QepServiceCollectionExtensions`.
  No resuelven permisos ni aparecen en ningún catálogo.
- Toda plantilla es un subconjunto de `admin`, verificado con una prueba. Motivo: `RoleCatalog.ListPermissions()` sólo trae lo
  que concede algún rol de sistema, así que un permiso que estuviera en una plantilla y no en `admin` no se podría marcar ni
  guardar (`authorization.role.permission_unknown`). Hoy las cinco ya lo cumplen según la lectura del código; la prueba lo fija.
- Se siembran los cinco en todo tenant, tenga o no el módulo. `RoleAssignability` ya impide asignar los que no conceden nada.

### Dominio

- `Role.KeyShape` admite `_` (`^[a-z0-9_-]{1,32}$`): `scheduling_manager` y `scheduling_provider` lo llevan.
- `SystemRoleKeys` reserva sólo `admin`.

### Marca de siembra

- Tabla nueva en el esquema `authorization`: una fila por tenant ya sembrado, con `tenant_id` como llave primaria y la fecha.
- Los roles y la marca se guardan en la misma transacción.
- Con marca, no se hace nada: así no revive lo borrado ni se pisa lo editado.
- Sin marca, se crea cada plantilla cuya clave el tenant no tenga y se escribe la marca.
- Dos siembras simultáneas del mismo tenant (un alta en un pod mientras otro arranca) terminan en una sola; la que pierde la
  carrera, reconocida por el nombre de la restricción, se trata como ya sembrado. La traducción va en Infrastructure.

### Cuándo se siembra

- **Alta de tenant** (signup e invitación): por `INewTenantProvisioning`, que hoy tiene una sola implementación
  (`QuotationsNewTenantProvisioning`) y tiene que admitir las dos. Un fallo de la siembra de roles se registra como `Error` y
  no hace fallar el alta.
- **Arranque:** se siembra todo tenant sin marca, en cualquier estado. Va dentro de `RunUnderStartupLockAsync`, después de las
  migraciones y de la semilla (`QepSeedRunner` crea tenants). Si falla, **el arranque falla**: atender sin los roles dejaría
  miembros sin permisos y sin error.

### Borrado de roles

Hoy `DeleteRoleHandler` sólo mira las membresías **activas**. Con roles borrables, una persona suspendida o invitada podría
quedar nombrando un rol que ya no existe: la pantalla de miembros no lo dibuja pero lo reenvía al guardar
(`member-row-actions.tsx`), y el `PUT` responde 422 `tenancy.membership.role_unknown`, sin salida salvo quitar y reinvitar.

- El borrado se rechaza con `authorization.role.in_use` si lo tiene una membresía `Active`, `Suspended` o `Invited`
  (también una invitación cuya ventana ya venció y sigue en `Invited`). `Removed` y `Expired` no cuentan: al reinvitar,
  `Membership.Reinvite` reemplaza los roles.
- Aplica a todo rol borrable, sembrado o creado por el tenant.
- El administrador sale del bloqueo cambiando los roles primero: `ChangeRoles` lo permite en esos tres estados y la pantalla
  ya ofrece «Cambiar roles» ahí. El mensaje del frontend para ese código sirve tal cual.
- `ActiveRoleSetsAsync` sigue contando sólo activas para las guardas de «alguien conserva la administración»: la consulta
  del borrado es otra.

### Efectos visibles

- En la pantalla de roles los cinco pasan de «sistema» a editables; su categoría es `Custom` y el nivel de riesgo se calcula
  por el permiso más alto, así que puede cambiar respecto de hoy.
- El frontend no necesita cambios: decide por `isSystem` (`roles-page.tsx`) y no encontré claves de rol quemadas.
- `CatalogVersion` cambia.

## Despliegue

- La migración es aditiva (una tabla).
- Durante el rollout, un pod viejo lee las filas nuevas y las prefiere a sus definiciones por clave
  (`TenantRoleCatalog`), con los mismos permisos.
- Un tenant registrado en un pod viejo después de que el nuevo sembró queda sin los cinco roles hasta el siguiente arranque.
  Se acepta.
- Volver a la versión anterior conserva lo editado: las filas siguen ganando por clave. Un rol borrado vuelve a verse como rol
  de sistema.

## Restricciones

- Capas verificadas por `ArchitectureTests`; Authorization no lee tablas de Tenancy por SQL salvo que pase por Bootstrapper.
- Muchas pruebas nombran estos roles (127 archivos de `tests/` contienen alguna de las claves, contando usos que no son roles).
  Las que afirman permisos sobre `IRoleCatalog` (`CompositionRootTests`) pasan a afirmarlos sobre las plantillas. Un harness
  que cree tenants después del arranque y sin pasar por el alta tiene que sembrar los roles.
- TDD: RED antes que GREEN.

## Documentación

- `.claude/rules/tenancy-y-servicios.md`: un permiso nuevo en una plantilla sólo llega a tenants nuevos; renombrar o partir un
  permiso exige una migración de datos sobre `authorization.roles` (precedente: `RenamePermissionsToOrders`).
- README y comentarios que describan estos cinco como roles de sistema, incluida la decisión D-13 del spec de Agenda
  (2026-11-21), que este spec reemplaza.

## Queda a criterio de quien implemente

Nombres de tipos y de la tabla, el puerto por el que se listan los tenants al arrancar y el reparto en tareas.

## Decisiones tomadas al redactar (revisar)

- Sembrar los cinco en todo tenant, con o sin módulo.
- El fallo en el alta no rompe el alta; el fallo al arrancar sí detiene el arranque.
- Una invitación en `Invited` con la ventana vencida también bloquea el borrado.
- El bloqueo del borrado aplica también a los roles que ya creó cada tenant.
- `EnsureSomebodyKeepsManagingAsync` queda como está.
- La ventana del rollout se acepta.
