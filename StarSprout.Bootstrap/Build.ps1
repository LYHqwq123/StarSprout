param([string]$Configuration = 'Release', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $locator)) { throw '需要安装 Visual Studio Build Tools 和 .NET Framework 4.7.2 Targeting Pack。' }
$builder = & $locator -latest -products '*' -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $builder) { throw '未找到 MSBuild。' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "bin\$Configuration" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '\'
& $builder (Join-Path $PSScriptRoot 'StarSprout.Bootstrap.csproj') /t:Rebuild /p:Configuration=$Configuration /p:Platform=x64 "/p:OutputPath=$OutputDirectory" /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw '前置启动程序编译失败。' }
Write-Output (Join-Path $OutputDirectory 'StarSprout.exe')
