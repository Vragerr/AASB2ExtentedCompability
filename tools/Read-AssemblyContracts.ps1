param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$TypePattern,
    [string]$MethodPattern = '.'
)
$ErrorActionPreference = 'Stop'
if (-not ("ContractTypeNames" -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot "ContractTypeNames.cs") -ReferencedAssemblies @(
        [System.Reflection.Metadata.MetadataReader].Assembly.Location,
        [System.Collections.Immutable.ImmutableArray].Assembly.Location,
        [System.Reflection.Assembly]::Load("System.Runtime").Location
    )
}
$provider = [ContractTypeNames]::new()
$stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $AssemblyPath).Path)
try {
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $assembly = $reader.GetAssemblyDefinition()
        foreach ($handle in $reader.TypeDefinitions) {
            $type = $reader.GetTypeDefinition($handle)
            $name = "$($reader.GetString($type.Namespace)).$($reader.GetString($type.Name))"
            if ($name -notmatch $TypePattern) { continue }
            foreach ($methodHandle in $type.GetMethods()) {
                $method = $reader.GetMethodDefinition($methodHandle)
                $methodName = $reader.GetString($method.Name)
                if ($methodName -notmatch $MethodPattern) { continue }
                $parameters = @(
                    foreach ($parameterHandle in $method.GetParameters()) {
                        $parameter = $reader.GetParameter($parameterHandle)
                        if ($parameter.SequenceNumber -gt 0) { $reader.GetString($parameter.Name) }
                    }
                )
                $signature = $method.DecodeSignature[string, object]($provider, $null)
                [pscustomobject]@{
                    Assembly = $reader.GetString($assembly.Name)
                    Version = $assembly.Version.ToString()
                    Type = $name
                    Method = $methodName
                    Parameters = ($parameters -join ', ')
                    ReturnType = $signature.ReturnType
                    ParameterTypes = ($signature.ParameterTypes -join ", ")
                    SignatureBlob = [Convert]::ToHexString($reader.GetBlobBytes($method.Signature))
                }
            }
        }
    } finally { $pe.Dispose() }
} finally { $stream.Dispose() }

