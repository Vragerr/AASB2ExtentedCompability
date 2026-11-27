param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [string]$CallerPattern = '.',
    [string]$TargetPattern = '.',
    [switch]$Flow
)
$ErrorActionPreference = 'Stop'
if (-not ('MetadataCalls' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'MetadataCalls.cs') -ReferencedAssemblies (Get-ChildItem -LiteralPath (Join-Path $PSHOME "ref") -Filter "*.dll" | Select-Object -ExpandProperty FullName) -CompilerOptions '/unsafe'
}
[MetadataCalls]::Scan((Resolve-Path -LiteralPath $AssemblyPath).Path, $CallerPattern, $TargetPattern, $Flow.IsPresent)


