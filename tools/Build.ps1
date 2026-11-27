param(
    [Parameter(Mandatory)][string]$RimWorldDir,
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$HarmonyDll
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$game = (Resolve-Path -LiteralPath $RimWorldDir).Path
if ((Get-Content -Raw -LiteralPath (Join-Path $game 'Version.txt')).Trim() -notmatch '^1\.7\.') {
    throw 'Для этой сборки требуется RimWorld 1.7.'
}
$references = @('System.Private.CoreLib.dll','System.Runtime.dll','System.Private.Uri.dll','System.Collections.dll','System.Linq.dll','System.Private.Xml.dll','System.Xml.XmlDocument.dll','System.Xml.ReaderWriter.dll','netstandard.dll','Assembly-CSharp.dll','UnityStubs.dll','System.Reflection.Primitives.dll','System.Reflection.Emit.ILGeneration.dll')
$arguments = foreach ($name in $references) {
    '/reference:' + (Resolve-Path -LiteralPath (Join-Path $game $name)).Path
}
$arguments += '/reference:' + (Resolve-Path -LiteralPath $HarmonyDll).Path
$outDir = Join-Path $root 'mod/1.7/Assemblies'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$buildDir = Join-Path $root 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$env:DOTNET_CLI_HOME = $buildDir
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$work = Join-Path $buildDir 'AASB2ExtendedCompatibility.dll'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root "src") -Filter "*.cs" | Select-Object -ExpandProperty FullName)
& dotnet (Resolve-Path -LiteralPath $Compiler).Path /nologo /noconfig /nostdlib+ /deterministic+ /target:library /optimize+ "/out:$work" @arguments @sources
if ($LASTEXITCODE -ne 0) { throw "Компилятор завершился с кодом $LASTEXITCODE." }
Move-Item -LiteralPath $work -Destination (Join-Path $outDir 'AASB2ExtendedCompatibility.dll') -Force



