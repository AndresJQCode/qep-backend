Vamos a seguir con la serie de specs para trabajar de corrido. El flujo es el mismo de la sesión
anterior (motivo de resolución, ya en develop de los dos repos):

1. Brainstorming: clasifica el pedido, explora el código (backend y, si aplica, qep-frontend),
   hazme las preguntas que cambian el diseño, propón enfoques y presenta el diseño.
2. Con mi aprobación, escribe la spec en docs/superpowers/specs/ y espera mi visto bueno.
3. Plan de implementación con writing-plans (uno por repo si toca los dos).
4. Ejecución con subagent-driven-development: un implementador y un revisor por tarea, revisión
   final de toda la rama con el modelo más capaz, y una sola tanda de correcciones.
5. Al terminar, merge a develop. Nunca a main y nunca push.

Reglas que ya aprendimos (están en CLAUDE.md y en memoria, pero las repito):

- Otras sesiones usan el mismo checkout y le cambian la rama. Todo trabajo, incluidos los commits
  de specs y planes, va en un worktree propio creado desde la ref develop
  (git worktree add .claude/worktrees/<x> -b feature/<x> develop).
- Para cerrar: integra develop dentro de la rama en el worktree, prueba ahí, y haz
  git merge --ff-only en el árbol principal sólo si está en develop y limpio.
  Si develop se movió, repite la integración.
- Comprueba git branch --show-current justo antes de cada commit.
- Commits sin atribución de IA. Revisa los trailers de cada commit de un subagente.
- En el backend no corras la suite completa: sólo las pruebas del módulo tocado y
  ArchitectureTests. En el frontend la suite completa se corre una vez, al final.
- Mensajes en español colombiano, tuteando.

La primera spec es:
[DESCRIBE AQUÍ LA SIGUIENTE FUNCIONALIDAD]
