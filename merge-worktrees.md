Corre esto desde el checkout principal, en PowerShell:

Set-Location "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
git branch --show-current # debe decir: develop
git merge --no-ff docs/claude-md-por-reglas -m "Merge branch 'docs/claude-md-por-reglas' into develop"
Revisa que el merge salió limpio y borra el worktree y la rama:

git log --oneline -3
git worktree remove "..\qep-backend-worktrees\claude-md-rules"
git branch -d docs/claude-md-por-reglas
La rama puede haber cambiado: si git branch --show-current no dice develop, haz git switch develop antes de mergear.

Cambios sin commitear: tu checkout tiene ClaudeModelClient.cs modificado. El merge no lo toca, así que no estorba.

Conflicto en CLAUDE.md: si otra sesión lo editó en develop desde que salí de 6b81d0ef, el merge se frena. En ese caso no lo resuelvas a mano: pásame el conflicto, porque la viñeta nueva hay que moverla al archivo de .claude/rules/ que le corresponde.

Filename too long al borrar el worktree: usa

Remove-Item -LiteralPath "\\?\C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\claude-md-rules" -Recurse -Force
git worktree prune
