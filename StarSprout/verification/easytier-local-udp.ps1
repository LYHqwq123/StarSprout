param([string]$CoreDirectory = '')
$ErrorActionPreference = 'Stop'
if (-not $CoreDirectory) { $CoreDirectory = Join-Path $PSScriptRoot '..\..\reference\easytier-v2.6.4' }
$core = [IO.Path]::GetFullPath((Join-Path $CoreDirectory 'easytier-core.exe'))
$cli = [IO.Path]::GetFullPath((Join-Path $CoreDirectory 'easytier-cli.exe'))
$output = Join-Path $PSScriptRoot ('easytier-local-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
function FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
function FreeUdpPort {
    $socket = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
    try { $socket.Client.LocalEndPoint.Port } finally { $socket.Dispose() }
}
$hostPort = FreeTcpPort
$hostRpc = FreeTcpPort
$guestPort = FreeTcpPort
$guestRpc = FreeTcpPort
$forwardPort = FreeUdpPort
$room = 'StarSprout-Local-Test-' + [guid]::NewGuid().ToString('N')
$secret = [guid]::NewGuid().ToString('N')
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
function Start-Core([string]$Name,[string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($core)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $output
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['ET_NETWORK_SECRET'] = $secret
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $processes.Add($process)
    # Consume bounded test logs; they contain only this test's random room identity.
    $script:logTasks[$Name] = @($process.StandardOutput.ReadToEndAsync(),$process.StandardError.ReadToEndAsync())
    return $process
}
$script:logTasks = @{}
$server = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
$gamePort = $server.Client.LocalEndPoint.Port
$client = [Net.Sockets.UdpClient]::new()
try {
    $common = @('--network-name',$room,'--no-tun','true','--bind-device','false','--disable-upnp','true','--private-mode','true','--disable-encryption','false','--encryption-algorithm','aes-gcm','--rpc-portal-whitelist','127.0.0.1/32','--tcp-whitelist','0','--stun-servers','--stun-servers-v6','--disable-udp-hole-punching','true','--disable-tcp-hole-punching','true')
    $hostCore = Start-Core 'host' ($common + @('--udp-whitelist',"$gamePort",'--ipv4','10.126.0.1','--hostname','StarSprout-Test-Host','--listeners',"tcp://127.0.0.1:$hostPort",'--rpc-portal',"127.0.0.1:$hostRpc"))
    $guestCore = Start-Core 'guest' ($common + @('--udp-whitelist','0','--ipv4','10.126.1.2','--hostname','StarSprout-Test-Guest','--listeners',"tcp://127.0.0.1:$guestPort",'--rpc-portal',"127.0.0.1:$guestRpc",'--peers',"tcp://127.0.0.1:$hostPort",'--port-forward',"udp://127.0.0.1:$forwardPort/10.126.0.1:$gamePort"))
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 200
        if ($hostCore.HasExited -or $guestCore.HasExited) { throw 'Local EasyTier test core exited before connection.' }
    }
    $hostInfo = & $cli -p "127.0.0.1:$hostRpc" -o json node info
    if ($LASTEXITCODE -ne 0) { throw 'Host RPC failed.' }
    $hostInfo -replace [regex]::Escape($secret),'[test-secret]' | Set-Content (Join-Path $output 'host-node.json') -Encoding utf8
    $guestPeers = & $cli -p "127.0.0.1:$guestRpc" -o json peer list
    if ($LASTEXITCODE -ne 0) { throw 'Guest RPC failed.' }
    $guestPeers | Set-Content (Join-Path $output 'guest-peers.json') -Encoding utf8
    $incomingTask = $server.ReceiveAsync()
    $payload = [Text.Encoding]::UTF8.GetBytes('StarSprout UDP no-TUN roundtrip')
    $null = $client.Send($payload,$payload.Length,[Net.IPEndPoint]::new([Net.IPAddress]::Loopback,$forwardPort))
    if (-not $incomingTask.Wait(12000)) { throw 'UDP did not reach the loopback game server.' }
    $incoming = $incomingTask.Result
    if ([Text.Encoding]::UTF8.GetString($incoming.Buffer) -ne 'StarSprout UDP no-TUN roundtrip') { throw 'UDP payload changed.' }
    $null = $server.Send($incoming.Buffer,$incoming.Buffer.Length,$incoming.RemoteEndPoint)
    $replyTask = $client.ReceiveAsync()
    if (-not $replyTask.Wait(12000)) { throw 'UDP reply did not return through the overlay.' }
    if ([Text.Encoding]::UTF8.GetString($replyTask.Result.Buffer) -ne 'StarSprout UDP no-TUN roundtrip') { throw 'UDP reply changed.' }
    'PASS: two real EasyTier cores, loopback-only peers, no TUN, UDP request/reply and native JSON status.' | Set-Content (Join-Path $output 'passed.txt')
    Write-Output (Join-Path $output 'passed.txt')
}
finally {
    $server.Dispose()
    $client.Dispose()
    foreach ($process in $processes) {
        if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }
        $process.Dispose()
    }
    foreach ($name in $script:logTasks.Keys) {
        $logs = $script:logTasks[$name]
        $logs[0].GetAwaiter().GetResult().Replace($secret,'[test-secret]') | Set-Content (Join-Path $output ($name+'-stdout.txt'))
        $logs[1].GetAwaiter().GetResult().Replace($secret,'[test-secret]') | Set-Content (Join-Path $output ($name+'-stderr.txt'))
    }
}
