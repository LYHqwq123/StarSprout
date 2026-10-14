param([string]$Packages = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = Split-Path $projectRoot -Parent
$dist = Join-Path $projectRoot 'dist'
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'StarSprout.csproj') -Raw)).Project.PropertyGroup.Version
$releaseName = "StarSprout-$version-Windows"
$release = Join-Path $dist $releaseName
$stage = Join-Path $dist ('.stage-' + [guid]::NewGuid().ToString('N'))
$sourceStage = Join-Path $dist ('.source-' + [guid]::NewGuid().ToString('N'))
function Remove-GeneratedDirectory([string]$Target) {
    $absolute = [IO.Path]::GetFullPath($Target)
    $allowed = [IO.Path]::GetFullPath($dist).TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw '构建输出路径超出 dist。' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
if (-not $Packages) {
    $workspacePackages = Join-Path $sourceRoot 'PCL\.packages'
    $Packages = if (Test-Path -LiteralPath $workspacePackages) { $workspacePackages } else { Join-Path $sourceRoot '.packages' }
}
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Path $stage,$sourceStage -Force | Out-Null
try {
    $bootstrapOutput = Join-Path $stage 'bootstrap'
    & (Join-Path $sourceRoot 'StarSprout.Bootstrap\Build.ps1') -OutputDirectory $bootstrapOutput
    & dotnet publish (Join-Path $projectRoot 'StarSprout.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false --packages $Packages -o (Join-Path $stage 'app') --disable-build-servers -v quiet
    if ($LASTEXITCODE -ne 0) { throw '启动器发布失败。' }
    Rename-Item -LiteralPath (Join-Path $stage 'app\StarSprout.exe') -NewName 'StarSprout.App.exe'
    Get-ChildItem -LiteralPath (Join-Path $stage 'app') -File | Where-Object { $_.Extension -in '.pdb','.xml' } | Remove-Item -Force
    Copy-Item -LiteralPath (Join-Path $bootstrapOutput 'StarSprout.exe') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $bootstrapOutput 'StarSprout.exe.config') -Destination $stage
    Remove-GeneratedDirectory $bootstrapOutput
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $stage
    foreach ($notice in @('LICENSE','THIRD-PARTY.md','CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $notice) -Destination $stage }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Resources\Licenses') -Destination $stage -Recurse
    New-Item -ItemType Directory -Path (Join-Path $stage 'runtimes') | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Resources\Runtime-Offline.md') -Destination (Join-Path $stage 'runtimes\README.md')
    Remove-GeneratedDirectory $release
    if (-not [IO.Path]::GetFullPath($stage).StartsWith([IO.Path]::GetFullPath($dist).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '暂存目录超出 dist。' }
    Move-Item -LiteralPath $stage -Destination $release
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $runtimeZip = Join-Path $dist "$releaseName.zip"
    if (Test-Path -LiteralPath $runtimeZip) { Remove-Item -LiteralPath $runtimeZip -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($release, $runtimeZip, [IO.Compression.CompressionLevel]::Optimal, $true)
    foreach ($folder in @('StarSprout','StarSprout.Bootstrap')) {
        $origin = Join-Path $sourceRoot $folder
        $destination = Join-Path $sourceStage $folder
        New-Item -ItemType Directory -Path $destination | Out-Null
        Get-ChildItem -LiteralPath $origin -File | Where-Object { $_.Extension -in '.cs','.csproj','.ico','.md','.config','.manifest','.ps1' } | Copy-Item -Destination $destination
        if ($folder -eq 'StarSprout') {
            foreach ($subfolder in @('Services','Resources','wwwroot','tools')) { Copy-Item -LiteralPath (Join-Path $origin $subfolder) -Destination $destination -Recurse }
            $checks = Join-Path $destination 'verification'
            New-Item -ItemType Directory -Path $checks | Out-Null
            Copy-Item -LiteralPath (Join-Path $origin 'verification\frontend-checks.cjs') -Destination $checks
            foreach ($suite in @('AnalysisChecks','ContentChecks','NexusChecks','AppearanceChecks','ModStateChecks','CommunityChecks','InstallChecks','HomeChecks','SmapiChecks')) {
                if (-not (Test-Path -LiteralPath (Join-Path $origin "verification\$suite"))) { continue }
                $suiteTarget = Join-Path $checks $suite
                New-Item -ItemType Directory -Path $suiteTarget | Out-Null
                Get-ChildItem -LiteralPath (Join-Path $origin "verification\$suite") -File | Where-Object { $_.Extension -in '.cs','.csproj','.txt' } | Copy-Item -Destination $suiteTarget
            }
        }
    }
    foreach ($metadata in @('README.md','LICENSE','THIRD-PARTY.md','CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $metadata) -Destination $sourceStage }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Repository\.gitignore') -Destination $sourceStage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Repository\.github') -Destination $sourceStage -Recurse
    $sourceZip = Join-Path $dist "StarSprout-$version-Source.zip"
    if (Test-Path -LiteralPath $sourceZip) { Remove-Item -LiteralPath $sourceZip -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($sourceStage, $sourceZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $manifest = @($runtimeZip,$sourceZip) | ForEach-Object { $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256; "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))" }
    $manifest | Set-Content -LiteralPath (Join-Path $dist "StarSprout-$version-SHA256.txt") -Encoding Ascii
    Write-Output $runtimeZip
    Write-Output $sourceZip
}
finally { Remove-GeneratedDirectory $stage; Remove-GeneratedDirectory $sourceStage }
