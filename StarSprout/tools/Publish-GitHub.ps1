param(
    [string]$Owner = 'LYHqwq123',
    [string]$Repository = 'StarSprout',
    [switch]$Login,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $projectRoot 'dist'
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'StarSprout.csproj') -Raw)).Project.PropertyGroup.Version
$runtimeZip = Join-Path $dist "StarSprout-$version-Windows.zip"
$sourceZip = Join-Path $dist "StarSprout-$version-Source.zip"
$hashFile = Join-Path $dist "StarSprout-$version-SHA256.txt"
$notes = Join-Path $projectRoot 'CHANGELOG.md'
foreach ($file in @($runtimeZip,$sourceZip,$hashFile,$notes)) {
    if (-not (Test-Path -LiteralPath $file)) { throw "缺少发布文件：$file。请先运行 Build-Release.ps1。" }
}
foreach ($line in Get-Content -LiteralPath $hashFile) {
    if ($line -notmatch '^([a-f0-9]{64})  ([A-Za-z0-9._-]+)$') { throw 'SHA256 文件格式无效。' }
    $expectedHash=$Matches[1]; $artifact=Join-Path $dist $Matches[2]
    if ((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) { throw '发布文件 SHA256 不一致。' }
}
if ($Owner -notmatch '^[A-Za-z0-9-]+$' -or $Repository -notmatch '^[A-Za-z0-9._-]+$') { throw 'GitHub 仓库名称无效。' }
$publishDirectory = Join-Path $dist ('.publish-' + [guid]::NewGuid().ToString('N'))
if ($PrepareOnly) {
    Write-Output "已校验发布包。目标：https://github.com/$Owner/$Repository；版本 v$version。"
    exit 0
}
if ($Login) {
    & git credential-manager github login --device --username $Owner --no-ui
    if ($LASTEXITCODE -ne 0) { throw 'GitHub 登录未完成。' }
}
$env:GIT_TERMINAL_PROMPT='0'
$env:GCM_INTERACTIVE='never'
$credentialLines=@('protocol=https','host=github.com',"username=$Owner",'') | git credential fill 2>$null
$credentials=@{}
foreach ($line in $credentialLines) { if ($line -match '^([^=]+)=(.*)$') { $credentials[$Matches[1]]=$Matches[2] } }
if (-not $credentials['password']) { throw '本机尚未登录 GitHub。运行 Publish-GitHub.ps1 -Login，在 GitHub 官方页面完成登录即可自动继续发布；不需要发送密钥。' }
$headers=@{Authorization='Bearer '+$credentials['password']; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28'; 'User-Agent'='StarSprout-Publisher'}
function Api([string]$Method,[string]$Path,$Body=$null) {
    $request=@{Method=$Method;Uri=('https://api.github.com'+$Path);Headers=$headers}
    if ($null -ne $Body) { $request.Body=ConvertTo-Json -InputObject $Body -Depth 10 -Compress; $request.ContentType='application/json; charset=utf-8' }
    Invoke-RestMethod @request
}
$account=Api 'GET' '/user'
if ($account.login -cne $Owner) { throw "登录账号是 $($account.login)，应为 $Owner。未改动 GitHub。" }
$repo=$null
try { $repo=Api 'GET' "/repos/$Owner/$Repository" }
catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
if (-not $repo) {
    $repo=Api 'POST' '/user/repos' @{name=$Repository;description='星芽启动器 · StarSprout — 星露谷物语与 SMAPI Mod 管理器';private=$false;has_issues=$true;auto_init=$false}
    $repo=Api 'GET' "/repos/$Owner/$Repository"
}
if ($repo.owner.login -cne $Owner -or -not $repo.permissions.push) { throw '目标仓库不属于当前账号或没有写权限。' }
$remote="https://$Owner@github.com/$Owner/$Repository.git"
if ($repo.size -gt 0) {
    & git clone --depth 1 $remote $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw '拉取目标仓库失败。' }
} else {
    New-Item -ItemType Directory -Path $publishDirectory | Out-Null
    & git init -b main $publishDirectory | Out-Null
    & git -C $publishDirectory remote add origin $remote
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($sourceZip,$publishDirectory,$true)
& git -C $publishDirectory config user.name $Owner
& git -C $publishDirectory config user.email "$($account.id)+$Owner@users.noreply.github.com"
& git -C $publishDirectory add --all
& git -C $publishDirectory diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
    & git -C $publishDirectory commit -m "Release StarSprout $version"
    if ($LASTEXITCODE -ne 0) { throw '创建发布提交失败。' }
}
& git -C $publishDirectory push -u origin HEAD:main
if ($LASTEXITCODE -ne 0) { throw '源码推送失败，未创建 Release。' }
$releaseBody=@{
    tag_name="v$version";target_commitish='main';name="星芽启动器 StarSprout $version"
    body=(Get-Content -LiteralPath $notes -Raw -Encoding UTF8);draft=$true;prerelease=$false
}
$release=$null
try { $release=Api 'GET' "/repos/$Owner/$Repository/releases/tags/v$version" }
catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
if ($release -and -not $release.draft) { throw "v$version 已正式发布，未覆盖已有版本：$($release.html_url)" }
if (-not $release) { $release=Api 'POST' "/repos/$Owner/$Repository/releases" $releaseBody }
$http=[Net.Http.HttpClient]::new()
$http.Timeout=[TimeSpan]::FromMinutes(20)
try {
    $http.DefaultRequestHeaders.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$credentials['password'])
    $http.DefaultRequestHeaders.UserAgent.ParseAdd('StarSprout-Publisher')
    foreach ($file in @($runtimeZip,$sourceZip,$hashFile)) {
        $existing=@($release.assets | Where-Object name -eq ([IO.Path]::GetFileName($file)))
        if ($existing.Count) {
            $hash='sha256:'+(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($existing[0].digest -eq $hash -and $existing[0].size -eq (Get-Item -LiteralPath $file).Length) { continue }
            throw '草稿版本中已存在不同的同名文件，未覆盖。请删除该草稿中的冲突文件后重试。'
        }
        $url="https://uploads.github.com/repos/$Owner/$Repository/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString([IO.Path]::GetFileName($file)))"
        $content=[Net.Http.ByteArrayContent]::new([IO.File]::ReadAllBytes($file))
        $content.Headers.ContentType=[Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
        try {
            $response=$http.PostAsync($url,$content).GetAwaiter().GetResult()
            try { $response.EnsureSuccessStatusCode() | Out-Null } finally { $response.Dispose() }
        } finally { $content.Dispose() }
    }
    $published=Api 'PATCH' "/repos/$Owner/$Repository/releases/$($release.id)" @{draft=$false;prerelease=$false;make_latest='true'}
    @{repository=$repo.html_url;release=$published.html_url;version=$version;assets=3} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'github-published.json') -Encoding UTF8
    Write-Output $published.html_url
} finally {
    $http.Dispose();$headers.Clear();$credentials.Clear()
}
