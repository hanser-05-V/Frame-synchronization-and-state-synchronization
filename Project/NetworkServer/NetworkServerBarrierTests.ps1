param(
    [string]$ServerPath = (Join-Path $PSScriptRoot 'NetworkServer.exe')
)

$ErrorActionPreference = 'Stop'
$routeC_serverProcess = $null
$routeC_client0 = $null
$routeC_client1 = $null

function Connect-RouteCClient
{
    $routeC_deadline = [DateTime]::UtcNow.AddSeconds(2)
    while ([DateTime]::UtcNow -lt $routeC_deadline)
    {
        $routeC_candidate = [System.Net.Sockets.TcpClient]::new()
        try
        {
            $routeC_candidate.Connect('127.0.0.1', 8888)
            return $routeC_candidate
        }
        catch [System.Net.Sockets.SocketException]
        {
            $routeC_candidate.Close()
            Start-Sleep -Milliseconds 50
        }
    }

    throw 'Server did not start listening on port 8888 within two seconds.'
}

function Read-RouteCExactly
{
    param(
        [System.IO.Stream]$Stream,
        [int]$Count
    )

    $routeC_result = [byte[]]::new($Count)
    $routeC_offset = 0
    while ($routeC_offset -lt $Count)
    {
        $routeC_read = $Stream.Read($routeC_result, $routeC_offset, $Count - $routeC_offset)
        if ($routeC_read -le 0) { throw 'Stream ended before a full packet arrived.' }
        $routeC_offset += $routeC_read
    }

    return $routeC_result
}

try
{
    $routeC_serverAssembly = [Reflection.Assembly]::LoadFrom($ServerPath)
    $routeC_tcpServerType = $routeC_serverAssembly.GetType(
        'FrameSyncServer.TcpRelayServer',
        $true)
    $routeC_configureMethod = $routeC_tcpServerType.GetMethod(
        'ConfigureLowLatency',
        [Reflection.BindingFlags]'Static, NonPublic')
    if (-not $routeC_configureMethod)
    {
        throw 'Server low-latency socket configuration is missing.'
    }

    $routeC_probeClient = [System.Net.Sockets.TcpClient]::new()
    try
    {
        $routeC_probeClient.NoDelay = $false
        $routeC_configureMethod.Invoke($null, @($routeC_probeClient)) | Out-Null
        if (-not $routeC_probeClient.NoDelay)
        {
            throw 'Server low-latency socket configuration did not enable NoDelay.'
        }
    }
    finally
    {
        $routeC_probeClient.Close()
    }

    $routeC_startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $routeC_startInfo.FileName = $ServerPath
    $routeC_startInfo.UseShellExecute = $false
    $routeC_startInfo.RedirectStandardInput = $true
    $routeC_startInfo.RedirectStandardOutput = $true
    $routeC_startInfo.RedirectStandardError = $true
    $routeC_startInfo.CreateNoWindow = $true
    $routeC_startInfo.Arguments = '--transport tcp'

    $routeC_serverProcess = [System.Diagnostics.Process]::Start($routeC_startInfo)

    $routeC_client0 = Connect-RouteCClient
    $routeC_stream0 = $routeC_client0.GetStream()
    $routeC_stream0.ReadTimeout = 300

    $routeC_firstByte = -1
    try
    {
        $routeC_firstByte = $routeC_stream0.ReadByte()
    }
    catch [System.IO.IOException]
    {
        # Timeout is expected until the second client connects.
    }

    if ($routeC_firstByte -ge 0)
    {
        throw "First client was released early with player index $routeC_firstByte."
    }

    $routeC_client1 = Connect-RouteCClient
    $routeC_stream1 = $routeC_client1.GetStream()
    $routeC_stream0.ReadTimeout = 2000
    $routeC_stream1.ReadTimeout = 2000

    $routeC_player0 = $routeC_stream0.ReadByte()
    $routeC_player1 = $routeC_stream1.ReadByte()
    if ($routeC_player0 -ne 0 -or $routeC_player1 -ne 1)
    {
        throw "Unexpected player indices: P0=$routeC_player0, P1=$routeC_player1."
    }

    $routeC_packet = [byte[]]::new(8)
    [BitConverter]::GetBytes([uint32]0x12345678).CopyTo($routeC_packet, 0)
    [BitConverter]::GetBytes([int32]0).CopyTo($routeC_packet, 4)
    $routeC_stream0.Write($routeC_packet, 0, 3)
    $routeC_stream0.Flush()
    Start-Sleep -Milliseconds 50
    $routeC_stream0.Write($routeC_packet, 3, 5)
    $routeC_stream0.Flush()

    $routeC_forwarded = Read-RouteCExactly -Stream $routeC_stream1 -Count 8
    $routeC_forwardedRaw = [BitConverter]::ToUInt32($routeC_forwarded, 0)
    $routeC_forwardedFrame = [BitConverter]::ToInt32($routeC_forwarded, 4)
    if ($routeC_forwardedRaw -ne [uint32]0x12345678 -or $routeC_forwardedFrame -ne 0)
    {
        throw "Fragmented packet was corrupted: raw=$routeC_forwardedRaw frame=$routeC_forwardedFrame."
    }

    [BitConverter]::GetBytes([uint32]2309737967).CopyTo($routeC_packet, 0)
    [BitConverter]::GetBytes([int32]0).CopyTo($routeC_packet, 4)
    $routeC_stream1.Write($routeC_packet, 0, 7)
    $routeC_stream1.Flush()
    Start-Sleep -Milliseconds 50
    $routeC_stream1.Write($routeC_packet, 7, 1)
    $routeC_stream1.Flush()

    $routeC_reverse = Read-RouteCExactly -Stream $routeC_stream0 -Count 8
    $routeC_reverseRaw = [BitConverter]::ToUInt32($routeC_reverse, 0)
    $routeC_reverseFrame = [BitConverter]::ToInt32($routeC_reverse, 4)
    if ($routeC_reverseRaw -ne [uint32]2309737967 -or $routeC_reverseFrame -ne 0)
    {
        throw "Reverse fragmented packet was corrupted: raw=$routeC_reverseRaw frame=$routeC_reverseFrame."
    }

    Write-Output 'PASS: barrier and bidirectional fragmented frame-0 forwarding are correct.'
}
catch
{
    if ($routeC_serverProcess -and -not $routeC_serverProcess.HasExited)
    {
        $routeC_serverProcess.Kill()
        $routeC_serverProcess.WaitForExit(2000) | Out-Null
    }
    if ($routeC_serverProcess)
    {
        $routeC_serverOutput = $routeC_serverProcess.StandardOutput.ReadToEnd()
        $routeC_serverError = $routeC_serverProcess.StandardError.ReadToEnd()
        Write-Output "SERVER_STDOUT:`n$routeC_serverOutput"
        Write-Output "SERVER_STDERR:`n$routeC_serverError"
    }
    throw
}
finally
{
    if ($routeC_client1) { $routeC_client1.Close() }
    if ($routeC_client0) { $routeC_client0.Close() }
    if ($routeC_serverProcess -and -not $routeC_serverProcess.HasExited)
    {
        $routeC_serverProcess.Kill()
        $routeC_serverProcess.WaitForExit(2000) | Out-Null
    }
}
