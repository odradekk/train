#Requires -Version 7.0
param([Parameter(Mandatory)][string] $ExportDirectory)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Reflection.Metadata
$directory = (Resolve-Path -LiteralPath $ExportDirectory).Path
$missing = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('Train.dll', 'Train.Core.dll', 'Train.Content.dll', 'Train.Scenes.dll')) {
    $null = $missing.Add($name)
}
$unoptimized = [Collections.Generic.List[string]]::new()
$managedCount = 0
$nativeCount = 0
foreach ($file in Get-ChildItem -LiteralPath $directory -Filter '*.dll' -File -Recurse) {
    $stream = [IO.File]::OpenRead($file.FullName)
    $pe = $null
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $pe.HasMetadata) { $nativeCount++; continue }
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $managedCount++
        $null = $missing.Remove($file.Name)
        $modes = 0
        foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
            $attribute = $metadata.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -eq [Reflection.Metadata.HandleKind]::MemberReference) {
                $constructor = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]::op_Explicit($attribute.Constructor))
                if ($constructor.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
                $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]::op_Explicit($constructor.Parent))
            } elseif ($attribute.Constructor.Kind -eq [Reflection.Metadata.HandleKind]::MethodDefinition) {
                $constructor = $metadata.GetMethodDefinition([Reflection.Metadata.MethodDefinitionHandle]::op_Explicit($attribute.Constructor))
                $type = $metadata.GetTypeDefinition($constructor.GetDeclaringType())
            } else { continue }
            if ($metadata.GetString($type.Namespace) -ne 'System.Diagnostics' -or
                $metadata.GetString($type.Name) -ne 'DebuggableAttribute') { continue }
            $signature = $metadata.GetBlobReader($constructor.Signature)
            $null = $signature.ReadSignatureHeader()
            $parameters = $signature.ReadCompressedInteger()
            $value = $metadata.GetBlobReader($attribute.Value)
            $null = $value.ReadUInt16()
            if ($parameters -eq 1) { $modes = $value.ReadInt32() }
            elseif ($parameters -eq 2) {
                $tracking = $value.ReadBoolean()
                $disabled = $value.ReadBoolean()
                $modes = $(if ($tracking) { 1 } else { 0 }) -bor $(if ($disabled) { 256 } else { 0 })
            } else { throw "Unknown DebuggableAttribute constructor: $($file.FullName)" }
        }
        $relative = [IO.Path]::GetRelativePath($directory, $file.FullName)
        Write-Output "$relative DebuggingModes=$modes"
        if (($modes -band [int][Diagnostics.DebuggableAttribute+DebuggingModes]::DisableOptimizations) -ne 0) {
            $unoptimized.Add($relative)
        }
    } finally {
        if ($null -ne $pe) { $pe.Dispose() }
        $stream.Dispose()
    }
}
if ($missing.Count -or $unoptimized.Count) {
    throw "Export optimization check failed. Missing: $($missing -join ', '); DisableOptimizations: $($unoptimized -join ', ')."
}
Write-Output "Export optimizations verified: $managedCount managed DLLs; $nativeCount native DLLs skipped."
