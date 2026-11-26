# Selección de pruebas del nivel rápido. Funciones sin efectos: reciben rutas y contenidos, no leen el disco ni git.

$script:CrossCuttingWarning = "Cambio transversal: conviene la corrida completa antes de seguir"

# Declaraciones de primer nivel (columna 0: los namespaces son de archivo). El grupo opcional entre paréntesis
# salta el constructor primario, con paréntesis anidados, antes de leer la clase base.
$script:ClassPattern = '(?m)^(?:public|internal)\s+(?<mods>(?:(?:sealed|abstract|partial|static)\s+)*)class\s+(?<name>\w+)' +
    '(?:<[^>\r\n]*>)?(?:\s*\((?>[^()]+|\((?<p>)|\)(?<-p>))*(?(p)(?!))\))?(?:\s*:\s*(?<base>\w+))?'

# Todo tipo de primer nivel: marca dónde termina cada clase y qué apoyo declara un archivo.
$script:TypePattern = '(?m)^(?:public|internal)\s+(?:(?:sealed|abstract|partial|static|readonly)\s+)*' +
    '(?:record(?:\s+(?:class|struct))?|class|struct|interface|enum)\s+(?<name>\w+)'

function Get-ClassMap([hashtable]$Sources, [string]$ProjectDir) {
    $classes = @{}
    foreach ($path in $Sources.Keys) {
        $normalized = $path -replace '\\', '/'
        if (-not $normalized.StartsWith("$ProjectDir/")) { continue }
        $content = [string]$Sources[$path]
        $starts = @([regex]::Matches($content, $script:TypePattern) | ForEach-Object { $_.Index })
        foreach ($m in [regex]::Matches($content, $script:ClassPattern)) {
            $name = $m.Groups["name"].Value
            if (-not $classes.ContainsKey($name)) {
                $classes[$name] = @{ Abstract = $false; HasFacts = $false; Base = ""; Files = @() }
            }
            $class = $classes[$name]
            if ($m.Groups["mods"].Value -match '\babstract\b') { $class.Abstract = $true }
            # Sólo cuentan las pruebas dentro de esta clase: un archivo puede declarar además un doble o un host.
            $end = $content.Length
            foreach ($start in $starts) { if ($start -gt $m.Index) { $end = $start; break } }
            if ($content.Substring($m.Index, $end - $m.Index) -match '\[(Fact|Theory)\b') { $class.HasFacts = $true }
            if ($m.Groups["base"].Success) { $class.Base = $m.Groups["base"].Value }
            $class.Files += $normalized
        }
    }
    return $classes
}

# La clase y todo lo que herede de ella.
function Get-WithDescendants($Classes, [string]$Name) {
    $found = @($Name)
    for ($i = 0; $i -lt $found.Count; $i++) {
        foreach ($other in $Classes.Keys) {
            if ($Classes[$other].Base -eq $found[$i] -and $found -notcontains $other) { $found += $other }
        }
    }
    return $found
}

# Con pruebas propias o heredadas.
function Test-IsTestClass($Classes, [string]$Name) {
    $seen = @()
    while ($Classes.ContainsKey($Name) -and $seen -notcontains $Name) {
        if ($Classes[$Name].HasFacts) { return $true }
        $seen += $Name
        $Name = $Classes[$Name].Base
    }
    return $false
}

# ¿El archivo declara un tipo sin pruebas (propias ni en derivadas) que otro archivo del proyecto usa?
function Test-DeclaresSharedSupport($Classes, [hashtable]$Sources, [string]$ProjectDir, [string]$File) {
    if (-not $Sources.ContainsKey($File)) { return $false }
    foreach ($m in [regex]::Matches([string]$Sources[$File], $script:TypePattern)) {
        $name = $m.Groups["name"].Value
        if ($Classes.ContainsKey($name) -and
            (Get-WithDescendants $Classes $name | Where-Object { Test-IsTestClass $Classes $_ })) { continue }
        foreach ($other in $Sources.Keys) {
            if ($other -ne $File -and $other.StartsWith("$ProjectDir/") -and $Sources[$other] -cmatch "\b$name\b") { return $true }
        }
    }
    return $false
}

# Referencias de proyecto que un diff unificado agrega entre módulos. Bootstrapper, Api y las pruebas componen módulos
# a propósito: sólo cuentan los proyectos de src/Modules. Una referencia que se quita y se vuelve a poner en la misma
# capa del mismo módulo sólo se movió: así renombrar un proyecto no pide autorizar lo que ya existía.
function Get-NewModuleReferences([string[]]$Diff) {
    $added = @(); $removed = @{}; $path = ""
    foreach ($line in $Diff) {
        if ($line -match '^diff --git a/.+ b/(?<path>.+)$') { $path = $Matches.path; continue }
        if ($path -notmatch '^src/Modules/(?<module>[^/]+)/') { continue }
        $source = $Matches.module
        if ($line -notmatch '^(?<sign>[+-])\s*<ProjectReference\s+Include="[^"]*?Modules\.(?<target>\w+)\.(?<layer>\w+)\.csproj"') { continue }
        if ($Matches.target -eq $source) { continue }
        # La capa es el último segmento del nombre del .csproj que declara la referencia.
        $sourceLayer = ($path -replace '\.csproj$', '') -replace '^.*[./]', ''
        $key = "$source.$sourceLayer|$($Matches.target).$($Matches.layer)"
        if ($Matches.sign -eq "-") { $removed[$key] = $true }
        else { $added += [pscustomobject]@{ Key = $key; Source = $source; Target = $Matches.target; Path = $path } }
    }
    return @($added | Where-Object { -not $removed.ContainsKey($_.Key) })
}

function Get-TestPlan {
    param(
        [string[]]$ChangedFiles = @(),
        [string[]]$Projects = @(),
        [hashtable]$TestSources = @{},
        [string[]]$ProjectDiff = @()
    )

    $files = @($ChangedFiles | Where-Object { $_ } | ForEach-Object { $_ -replace '\\', '/' } | Sort-Object -Unique)
    $all = @($Projects | ForEach-Object { $_ -replace '\\', '/' })
    $steps = New-Object System.Collections.ArrayList
    $warnings = New-Object System.Collections.ArrayList

    function Add-Step([string]$Project, [string]$Filter = "") {
        [void]$steps.Add([pscustomobject]@{ Project = $Project; Filter = $Filter })
    }

    $all | Where-Object { $_ -like "*/ArchitectureTests.csproj" } | ForEach-Object { Add-Step $_ }

    $touched = @($files | ForEach-Object { if ($_ -match '^(?:src|tests)/Modules/([^/]+)/') { $Matches[1] } } | Sort-Object -Unique)
    foreach ($module in $touched) {
        $all | Where-Object { $_ -like "*/Modules.$module.UnitTests.csproj" } | ForEach-Object { Add-Step $_ }
    }

    if ($files -match '^(src/Bootstrapper|src/Api|tests/Bootstrapper)/') {
        $all | Where-Object { $_ -like "*/Bootstrapper.UnitTests.csproj" } | ForEach-Object { Add-Step $_ }
    }

    $integrationModules = @()
    foreach ($project in $all | Where-Object { $_ -like "*.IntegrationTests.csproj" } | Sort-Object) {
        $dir = $project.Substring(0, $project.LastIndexOf('/'))
        $changed = @($files | Where-Object { $_.StartsWith("$dir/") })
        if (-not $changed) { continue }

        $classes = Get-ClassMap $TestSources $dir
        $selected = @()
        foreach ($file in $changed) {
            $declared = @($classes.Keys | Where-Object { $classes[$_].Files -contains $file })
            $targets = @($declared | ForEach-Object { Get-WithDescendants $classes $_ } |
                Where-Object { -not $classes[$_].Abstract -and (Test-IsTestClass $classes $_) })
            $selected += $targets
            # Un .cs que ya no está en TestSources se borró: no hay nada que inferir ni que avisar.
            $unresolved = -not $targets -and $file -notlike "*/packages.lock.json" -and
                ($file -notlike "*.cs" -or $TestSources.ContainsKey($file))
            if ($unresolved -or (Test-DeclaresSharedSupport $classes $TestSources $dir $file)) {
                [void]$warnings.Add("Apoyo de pruebas cambiado, sus clases no se infieren: $file")
            }
        }

        if ($selected) {
            $filter = ($selected | Sort-Object -Unique | ForEach-Object { "FullyQualifiedName~.$_." }) -join "|"
            Add-Step $project $filter
            if ($dir -match '^tests/Modules/([^/]+)/') { $integrationModules += $Matches[1] }
        }
    }

    $production = @($files | ForEach-Object { if ($_ -match '^src/Modules/([^/]+)/') { $Matches[1] } } | Sort-Object -Unique)
    foreach ($module in $production | Where-Object { $integrationModules -notcontains $_ }) {
        [void]$warnings.Add("${module}: cambió producción y no corre ninguna prueba de integración")
    }

    $migrated = @($files | ForEach-Object { if ($_ -match '^src/Modules/([^/]+)/.*/Migrations/') { $Matches[1] } } | Sort-Object -Unique)
    if (($files -match '^(src/(BuildingBlocks|Bootstrapper|Api)/|Directory\.[^/]*\.props$)') -or $migrated.Count -gt 1) {
        [void]$warnings.Add($script:CrossCuttingWarning)
    }

    # Lo que ninguna prueba puede decidir sola: aflojar una regla de arquitectura o acoplar dos módulos lo autoriza el owner.
    foreach ($file in $files | Where-Object { $_ -like "tests/ArchitectureTests/*" -and $_ -notlike "*/packages.lock.json" }) {
        [void]$warnings.Add("Requiere autorización del owner: cambió una prueba de arquitectura: $file")
    }
    foreach ($ref in Get-NewModuleReferences $ProjectDiff) {
        [void]$warnings.Add("Requiere autorización del owner: referencia nueva entre módulos, $($ref.Source) -> $($ref.Target): $($ref.Path)")
    }

    return [pscustomobject]@{ Steps = @($steps); Warnings = [string[]]@($warnings) }
}

# Lee el .trx de una carpeta de resultados; $null si no hay ninguno.
function Read-TrxResult {
    param([Parameter(Mandatory)][string]$Directory)

    $trx = Get-ChildItem -LiteralPath $Directory -Filter *.trx -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $trx) { return $null }
    [xml]$x = Get-Content -LiteralPath $trx.FullName -Raw -Encoding UTF8
    $c = $x.TestRun.ResultSummary.Counters
    $failures = @($x.TestRun.Results.UnitTestResult | Where-Object outcome -eq "Failed" | ForEach-Object {
        [pscustomobject]@{ Test = $_.testName; Message = ("$($_.Output.ErrorInfo.Message)".Trim() -split "`r?`n")[0] }
    })
    return [pscustomobject]@{ Total = [int]$c.total; Passed = [int]$c.passed; Failed = [int]$c.failed; Failures = $failures }
}

# Un paso sólo está verde si ejecutó pruebas: un filtro que no coincide con nada no prueba nada.
function Test-StepPassed {
    param($Result, [int]$ExitCode)

    return [bool]($null -ne $Result -and $Result.Total -gt 0 -and $Result.Failed -eq 0 -and $ExitCode -eq 0)
}

# El parámetro de hilos pisa el [assembly: CollectionBehavior(MaxParallelThreads = n)] del proyecto: sólo se manda si
# el proyecto no declara el suyo o si quien corre lo pidió.
function Get-ThreadArguments {
    param([string[]]$Sources, [int]$MaxThreads, [bool]$Explicit)

    $declares = @($Sources) -match '(?m)^\s*\[assembly:\s*(Xunit\.)?CollectionBehavior\([^\]]*MaxParallelThreads\s*='
    if ($declares -and -not $Explicit) { return @() }
    return @("--", "xUnit.MaxParallelThreads=$MaxThreads")
}

# Sobre qué se corrió: un reporte en verde sólo vale para ese commit.
function Get-GitStamp {
    $branch = git rev-parse --abbrev-ref HEAD
    $sha = git rev-parse --short HEAD
    if (git status --porcelain) { $tree = "con cambios sin commitear" } else { $tree = "árbol limpio" }
    return "Rama $branch · commit $sha · $tree"
}

Export-ModuleMember -Function Get-TestPlan, Read-TrxResult, Test-StepPassed, Get-ThreadArguments, Get-GitStamp
