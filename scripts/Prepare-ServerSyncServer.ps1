param(
    [Parameter(Mandatory)] [string] $MinecraftVersion,
    [Parameter(Mandatory)] [ValidateSet('Fabric', 'Quilt', 'Forge', 'NeoForge')] [string] $Loader,
    [Parameter(Mandatory)] [string] $LoaderVersion,
    [Parameter(Mandatory)] [string] $JavaPath,
    [Parameter(Mandatory)] [string] $SharedCacheDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$cache = Join-Path $projectRoot 'out\server-sync-cache'
$template = Join-Path $cache ($Loader.ToLowerInvariant() + '-' + $MinecraftVersion + '-' + $LoaderVersion)
foreach ($value in @($MinecraftVersion, $LoaderVersion)) {
    if ($value -notmatch '^[a-zA-Z0-9._+-]+$') { throw 'Version contains invalid path characters.' }
}
New-Item -ItemType Directory -Path $cache, $template -Force | Out-Null
$argumentsPath = Join-Path $template 'mechanica-server-arguments.json'
if (Test-Path -LiteralPath $argumentsPath -PathType Leaf) { return $template }

function Get-VerifiedDownload([string] $Uri, [string] $Path, [string] $Algorithm, [string] $Hash, [long] $Size = 0) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Host ('Downloading ' + [IO.Path]::GetFileName($Path))
        Invoke-WebRequest -Uri $Uri -OutFile $Path -UseBasicParsing
    }
    if (($Size -gt 0 -and (Get-Item -LiteralPath $Path).Length -ne $Size) -or
        (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $Hash) {
        throw ('Download does not match its published hash: ' + $Path)
    }
}

function Install-Server([string[]] $Arguments) {
    $installerLog = Join-Path $template 'installation.log'
    Write-Host ('Installing ' + $Loader + ' ' + $LoaderVersion + ' / Minecraft ' + $MinecraftVersion)
    Push-Location -LiteralPath $template
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $JavaPath @Arguments *> $installerLog
        $ErrorActionPreference = $previousErrorAction
        if ($LASTEXITCODE -ne 0) { throw ('Server installation failed. See ' + $installerLog) }
    }
    finally { $ErrorActionPreference = $previousErrorAction; Pop-Location }
}

$metadataPath = Join-Path $SharedCacheDirectory ('versions\' + $MinecraftVersion + '\' + $MinecraftVersion + '.json')
if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
}
else {
    $manifest = Invoke-RestMethod -Uri 'https://piston-meta.mojang.com/mc/game/version_manifest_v2.json'
    $entry = $manifest.versions | Where-Object { $_.id -eq $MinecraftVersion } | Select-Object -First 1
    if (-not $entry) { throw ('Minecraft version is missing: ' + $MinecraftVersion) }
    $downloadedMeta = Join-Path $cache ($MinecraftVersion + '-version.json')
    Get-VerifiedDownload $entry.url $downloadedMeta SHA1 $entry.sha1
    $metadata = Get-Content -LiteralPath $downloadedMeta -Raw | ConvertFrom-Json
}
if ($metadata.id -ne $MinecraftVersion -or $metadata.downloads.server.url -notmatch '^https://(?:piston-data|launcher)\.mojang\.com/') {
    throw 'Unexpected Minecraft server metadata.'
}
$serverJar = Join-Path $cache ('minecraft-server-' + $MinecraftVersion + '.jar')
Get-VerifiedDownload $metadata.downloads.server.url $serverJar SHA1 $metadata.downloads.server.sha1 $metadata.downloads.server.size

$serverArguments = @()
switch ($Loader) {
    'Fabric' {
        $installer = Join-Path $cache 'fabric-installer-1.1.2.jar'
        Get-VerifiedDownload 'https://maven.fabricmc.net/net/fabricmc/fabric-installer/1.1.2/fabric-installer-1.1.2.jar' `
            $installer SHA256 '61e035bf7bf70153e127440ce34de47c9036f0a2d0c65d1529454bd35ceefe4f' 212123
        Install-Server @('-jar', $installer, 'server', '-mcversion', $MinecraftVersion, '-loader', $LoaderVersion, '-dir', $template)
        Copy-Item -LiteralPath $serverJar -Destination (Join-Path $template 'server.jar')
        $serverArguments = @('-jar', 'fabric-server-launch.jar', 'nogui')
    }
    'Quilt' {
        $installerVersion = '0.12.1'
        $url = 'https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/' + $installerVersion + '/quilt-installer-' + $installerVersion + '.jar'
        $hash = (Invoke-RestMethod -Uri ($url + '.sha256')).Trim().Split(' ')[0]
        $installer = Join-Path $cache ('quilt-installer-' + $installerVersion + '.jar')
        Get-VerifiedDownload $url $installer SHA256 $hash
        Install-Server @('-jar', $installer, 'install', 'server', $MinecraftVersion, $LoaderVersion, ('--install-dir=' + $template))
        Copy-Item -LiteralPath $serverJar -Destination (Join-Path $template 'server.jar')
        $serverArguments = @('-jar', 'quilt-server-launch.jar', 'nogui')
    }
    default {
        if ($Loader -eq 'Forge') {
            $fullVersion = $MinecraftVersion + '-' + $LoaderVersion
            $artifact = 'forge'
            $groupPath = 'net/minecraftforge/forge/' + $fullVersion
            $url = 'https://maven.minecraftforge.net/' + $groupPath + '/forge-' + $fullVersion + '-installer.jar'
        }
        else {
            $fullVersion = $LoaderVersion
            $artifact = 'neoforge'
            $groupPath = 'net/neoforged/neoforge/' + $fullVersion
            $url = 'https://maven.neoforged.net/releases/' + $groupPath + '/neoforge-' + $fullVersion + '-installer.jar'
        }
        $hash = (Invoke-RestMethod -Uri ($url + '.sha1')).Trim().Split(' ')[0]
        $installer = Join-Path $cache ($artifact + '-' + $fullVersion + '-installer.jar')
        Get-VerifiedDownload $url $installer SHA1 $hash
        Copy-Item -LiteralPath $serverJar -Destination (Join-Path $template ('minecraft_server.' + $MinecraftVersion + '.jar'))
        Install-Server @('-jar', $installer, '--installServer', $template)
        $argumentFile = 'libraries/' + $groupPath + '/win_args.txt'
        if (Test-Path -LiteralPath (Join-Path $template $argumentFile)) {
            $serverArguments = @(('@' + $argumentFile), 'nogui')
        }
        else {
            $launch = Get-ChildItem -LiteralPath $template -File -Filter 'forge-*.jar' | Where-Object { $_.Name -notlike '*installer*' } | Select-Object -First 1
            if (-not $launch) { throw ('Server launch artifact is missing in ' + $template) }
            $serverArguments = @('-jar', $launch.Name, 'nogui')
        }
    }
}
if ($serverArguments[0] -eq '-jar' -and -not (Test-Path -LiteralPath (Join-Path $template $serverArguments[1]) -PathType Leaf)) {
    throw ('Server launch artifact is missing: ' + $serverArguments[1])
}
ConvertTo-Json -InputObject @($serverArguments) | Set-Content -LiteralPath $argumentsPath -Encoding UTF8
return $template
