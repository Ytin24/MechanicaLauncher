param(
    [string]$Source = (Join-Path $env:USERPROFILE 'source\repos\CSharpUiRenderer'),
    [string]$Version = '0.1.0-local.20260922.1'
)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).ProviderPath.TrimEnd('\', '/')
$repo = Split-Path $PSScriptRoot -Parent
$feed = Join-Path $repo 'vendor\nitidus'
New-Item -ItemType Directory -Force -Path $feed | Out-Null
foreach ($id in @('Nitidus.Core', 'Nitidus.Native', 'Ordo.Build')) {
    if (Test-Path -LiteralPath (Join-Path $feed "$id.$Version.nupkg")) { throw "Snapshot $Version already exists. Choose a new version." }
}
foreach ($project in @('src/Nitidus.Native/Nitidus.Native.csproj', 'ordo/Ordo.Compiler/Ordo.Compiler.csproj')) {
    & dotnet build (Join-Path $Source $project) -c Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Nitidus build failed: $project" }
}
Add-Type -AssemblyName System.IO.Compression
function Pack([string]$id, [hashtable]$files, [string]$dependencies = '') {
    $path = Join-Path $feed "$id.$Version.nupkg"
    $stream = [IO.File]::Open($path, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $files["$id.nuspec"] = "<?xml version=`"1.0`"?><package><metadata><id>$id</id><version>$Version</version><authors>Nitidus</authors><description>Local snapshot of Nitidus and Ordo for Mechanica Launcher.</description>$dependencies</metadata></package>"
        foreach ($name in $files.Keys) {
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $out = $entry.Open()
            try {
                if ($files[$name] -is [IO.FileInfo]) {
                    $input = $files[$name].OpenRead()
                    try { $input.CopyTo($out) } finally { $input.Dispose() }
                } else {
                    $data = [Text.Encoding]::UTF8.GetBytes([string]$files[$name])
                    $out.Write($data, 0, $data.Length)
                }
            } finally { $out.Dispose() }
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    Get-Item -LiteralPath $path | Select-Object Name, Length
}
Pack 'Nitidus.Core' @{'lib/net10.0/Nitidus.Core.dll' = Get-Item (Join-Path $Source 'src/Nitidus.Core/bin/Release/net10.0/Nitidus.Core.dll')}
Pack 'Nitidus.Native' @{
    'lib/net10.0/Nitidus.Native.dll' = Get-Item (Join-Path $Source 'src/Nitidus.Native/bin/Release/net10.0/Nitidus.Native.dll')
    'runtimes/win-x64/native/NativeRenderer.dll' = Get-Item (Join-Path $Source 'artifacts/cpp-build/bin/Release/NativeRenderer.dll')
} "<dependencies><group targetFramework=`"net10.0`"><dependency id=`"Nitidus.Core`" version=`"[$Version]`" /></group></dependencies>"
$compiler = @{}
Get-ChildItem (Join-Path $Source 'ordo/Ordo.Compiler/bin/Release/net10.0') -File | Where-Object Extension -in '.dll', '.json' | ForEach-Object { $compiler['tools/net10.0/' + $_.Name] = $_ }
$compiler['buildTransitive/Ordo.Build.targets'] = @'
<Project>
  <ItemGroup><Ordo Include="Views/**/*.ordo" /></ItemGroup>
  <PropertyGroup><OrdoCompilerPath>$(MSBuildThisFileDirectory)../tools/net10.0/Ordo.Compiler.dll</OrdoCompilerPath></PropertyGroup>
  <Target Name="OrdoInputs">
    <PropertyGroup><OrdoGenerated>$(IntermediateOutputPath)Ordo.g.cs</OrdoGenerated><OrdoManifest>$(IntermediateOutputPath)Ordo.inputs</OrdoManifest></PropertyGroup>
    <MakeDir Directories="$(IntermediateOutputPath)" />
    <WriteLinesToFile File="$(OrdoManifest)" Lines="@(Ordo->'%(FullPath)')" Overwrite="true" WriteOnlyWhenDifferent="true" Encoding="UTF-8" />
  </Target>
  <Target Name="OrdoGenerate" DependsOnTargets="OrdoInputs" Inputs="@(Ordo);$(OrdoManifest);$(OrdoCompilerPath);$(MSBuildThisFileFullPath)" Outputs="$(OrdoGenerated)">
    <Exec Command="dotnet &amp;quot;$(OrdoCompilerPath)&amp;quot; --manifest &amp;quot;$(OrdoManifest)&amp;quot; --output &amp;quot;$(OrdoGenerated)&amp;quot; --dependencies &amp;quot;$(OrdoGenerated).dependencies&amp;quot;" />
  </Target>
  <Target Name="OrdoCompile" BeforeTargets="CoreCompile" DependsOnTargets="OrdoGenerate">
    <ItemGroup><Compile Include="$(OrdoGenerated)" /><FileWrites Include="$(OrdoGenerated);$(OrdoManifest);$(OrdoGenerated).dependencies" /></ItemGroup>
  </Target>
</Project>
'@
$compiler['buildTransitive/Ordo.Build.targets'] = $compiler['buildTransitive/Ordo.Build.targets'].Replace('&amp;quot;', '&quot;')
Pack 'Ordo.Build' $compiler
$hashes = Get-ChildItem $feed -Filter "*.$Version.nupkg" | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $([IO.Path]::GetFileName($_.Path))" }
Set-Content -LiteralPath (Join-Path $feed 'SHA256SUMS') -Value $hashes
$sourceFiles = & rg --files $Source -g '*.cs' -g '*.cpp' -g '*.h' -g '*.csproj' -g '*.props' -g '*.targets' -g 'CMakeLists.txt' -g '!**/bin/**' -g '!**/obj/**' -g '!**/artifacts/**'
$sourceHashes = $sourceFiles | Sort-Object | ForEach-Object { $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256; "$($hash.Hash)  $($_.Substring($Source.Length + 1))" }
Set-Content -LiteralPath (Join-Path $feed 'SOURCE-SHA256SUMS') -Value $sourceHashes
