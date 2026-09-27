param(
    [string[]] $Targets,
    [string] $JavaHome = $env:JAVA_HOME,
    [string] $Java25Home,
    [switch] $Clean,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$projectDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'mods/server-sync'
& (Join-Path $projectDirectory 'scripts/Build.ps1') @PSBoundParameters
