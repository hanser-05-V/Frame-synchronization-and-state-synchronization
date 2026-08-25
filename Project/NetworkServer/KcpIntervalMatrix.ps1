param(
    [string]$ServerPath = '',

    [string]$OutputDirectory = '',

    [switch]$TcpControlOnly,

    [ValidateRange(100, 90000)]
    [int]$RunTimeoutMs = 90000,

    [switch]$ContractOnly
)

$ErrorActionPreference = 'Stop'

function Get-RouteCKcpMatrixContract
{
    return [ordered]@{
        schema = 'route-c-kcp-interval-matrix-v1'
        intervalsMs = @(1, 5, 10, 20)
        runsPerInterval = 3
        framesPerClient = 900
        logicalCadenceMs = 33
        seed = 20260824
        clientToServerDropPercent = 2
        processIsolation = 'hidden-owned-exact-pid'
        serverLaunchMode = 'networkserver-exe-cli'
        serverPidHandoff = 'per-run-exact-pid-file'
        runTimeoutMs = $RunTimeoutMs
        tracePattern =
            'p2f-kcp-interval-{interval}ms-run{run}.jsonl'
        runSummaryPattern =
            'p2f-kcp-interval-{interval}ms-run{run}-summary.json'
        summaryName = 'p2f-kcp-interval-summary.json'
        tcpControlSummaryName = 'p2f-tcp-control-summary.json'
        tcpControlFaultSemantics =
            'tcp-recovered-loss-hol/application'
        forbiddenFields = @(
            'token',
            'nonce',
            'resumeAttemptID',
            'sessionID')
    }
}

if ($ContractOnly)
{
    Get-RouteCKcpMatrixContract | ConvertTo-Json -Depth 6
    return
}

function Assert-RouteCKcpMatrix
{
    param([bool]$Condition, [string]$Message)
    if (-not $Condition)
    {
        throw $Message
    }
}

function ConvertTo-RouteCProcessArgument
{
    param([string]$Value)
    if ($Value.Length -eq 0)
    {
        return '""'
    }
    if ($Value -notmatch '[\s"]')
    {
        return $Value
    }

    $routeC_builder = [Text.StringBuilder]::new()
    [void]$routeC_builder.Append('"')
    $routeC_backslashes = 0
    foreach ($routeC_character in $Value.ToCharArray())
    {
        if ($routeC_character -eq '\')
        {
            $routeC_backslashes++
            continue
        }
        if ($routeC_character -eq '"')
        {
            [void]$routeC_builder.Append(
                '\' * ($routeC_backslashes * 2 + 1))
            [void]$routeC_builder.Append('"')
            $routeC_backslashes = 0
            continue
        }
        if ($routeC_backslashes -gt 0)
        {
            [void]$routeC_builder.Append(
                '\' * $routeC_backslashes)
            $routeC_backslashes = 0
        }
        [void]$routeC_builder.Append($routeC_character)
    }
    if ($routeC_backslashes -gt 0)
    {
        [void]$routeC_builder.Append(
            '\' * ($routeC_backslashes * 2))
    }
    [void]$routeC_builder.Append('"')
    return $routeC_builder.ToString()
}

function Get-RouteCMedian
{
    param([double[]]$Values)
    if ($null -eq $Values -or $Values.Count -eq 0)
    {
        return 0.0
    }
    $routeC_sorted = @($Values | Sort-Object)
    $routeC_middle = [Math]::Floor($routeC_sorted.Count / 2)
    if (($routeC_sorted.Count % 2) -eq 1)
    {
        return [double]$routeC_sorted[$routeC_middle]
    }
    return (
        [double]$routeC_sorted[$routeC_middle - 1] +
        [double]$routeC_sorted[$routeC_middle]) / 2.0
}

function Get-RouteCPercentile
{
    param([double[]]$Values, [double]$Percentile)
    if ($null -eq $Values -or $Values.Count -eq 0)
    {
        return 0.0
    }
    $routeC_sorted = @($Values | Sort-Object)
    $routeC_index = [Math]::Max(
        0,
        [Math]::Ceiling($routeC_sorted.Count * $Percentile) - 1)
    return [double]$routeC_sorted[$routeC_index]
}

function Assert-RouteCSecretSafeJson
{
    param([string]$Json, [string]$Label)
    Assert-RouteCKcpMatrix `
        ($Json -notmatch
            '(?i)"[^"]*(?:token|nonce|attemptid|sessionid)[^"]*"\s*:') `
        "$Label contains a forbidden secret field."
}

function Wait-RouteCUdpPortReleased
{
    param([int]$Port, [int]$TimeoutMs)

    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    do
    {
        $routeC_probe = [Net.Sockets.Socket]::new(
            [Net.Sockets.AddressFamily]::InterNetwork,
            [Net.Sockets.SocketType]::Dgram,
            [Net.Sockets.ProtocolType]::Udp)
        try
        {
            $routeC_probe.ExclusiveAddressUse = $true
            $routeC_probe.Bind([Net.IPEndPoint]::new(
                [Net.IPAddress]::Any,
                $Port))
            return $true
        }
        catch [Net.Sockets.SocketException]
        {
        }
        finally
        {
            $routeC_probe.Dispose()
        }
        [Threading.Thread]::Sleep(10)
    }
    while ($routeC_watch.ElapsedMilliseconds -lt $TimeoutMs)

    return $false
}

function Stop-RouteCExactServerFromPidFile
{
    param([string]$PidPath, [string]$ExpectedExecutablePath)

    Assert-RouteCKcpMatrix `
        (Test-Path -LiteralPath $PidPath -PathType Leaf) `
        "Timed-out probe omitted its exact server PID file: $PidPath"
    $routeC_pidText = [IO.File]::ReadAllText($PidPath).Trim()
    $routeC_serverPid = 0
    Assert-RouteCKcpMatrix `
        ([int]::TryParse($routeC_pidText, [ref]$routeC_serverPid) -and
         $routeC_serverPid -gt 0) `
        "Timed-out probe wrote an invalid server PID: $routeC_pidText"

    try
    {
        $routeC_serverProcess =
            [Diagnostics.Process]::GetProcessById($routeC_serverPid)
    }
    catch [ArgumentException]
    {
        return $true
    }

    $routeC_actualExecutable =
        [IO.Path]::GetFullPath($routeC_serverProcess.MainModule.FileName)
    $routeC_expectedExecutable =
        [IO.Path]::GetFullPath($ExpectedExecutablePath)
    Assert-RouteCKcpMatrix `
        ([string]::Equals(
            $routeC_actualExecutable,
            $routeC_expectedExecutable,
            [StringComparison]::OrdinalIgnoreCase)) `
        ("Refusing to stop PID $routeC_serverPid because it is not " +
         "the owned server executable: $routeC_actualExecutable")

    if (-not $routeC_serverProcess.HasExited)
    {
        $routeC_serverProcess.Kill()
        $routeC_serverProcess.WaitForExit(2000) | Out-Null
    }
    return $routeC_serverProcess.HasExited
}

function Invoke-RouteCTcpControl
{
    param(
        [string]$ExecutablePath,
        [string]$DestinationDirectory,
        $Contract)

    $routeC_probeSocket = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Stream,
        [Net.Sockets.ProtocolType]::Tcp)
    try
    {
        $routeC_probeSocket.ExclusiveAddressUse = $true
        $routeC_probeSocket.Bind([Net.IPEndPoint]::new(
            [Net.IPAddress]::Loopback,
            8888))
    }
    finally
    {
        $routeC_probeSocket.Dispose()
    }

    $routeC_tempDirectory = Join-Path `
        $PSScriptRoot `
        'obj\p2f-task11-tcp-control'
    [IO.Directory]::CreateDirectory($routeC_tempDirectory) | Out-Null
    $routeC_stdout = Join-Path $routeC_tempDirectory 'server-stdout.log'
    $routeC_stderr = Join-Path $routeC_tempDirectory 'server-stderr.log'
    $routeC_arguments = @(
        '--transport', 'tcp',
        '--loss-percent',
            [string]$Contract.clientToServerDropPercent,
        '--loss-recovery-ms', '100',
        '--seed', [string]$Contract.seed)
    $routeC_argumentString = @(
        $routeC_arguments |
            ForEach-Object {
                ConvertTo-RouteCProcessArgument ([string]$_)
            }) -join ' '
    $routeC_serverProcess = $null
    $routeC_clients = @($null, $null)
    $routeC_buffers = @(
        [Collections.Generic.List[byte]]::new(),
        [Collections.Generic.List[byte]]::new())
    $routeC_sendTimes = @{}
    $routeC_samples = [Collections.Generic.List[object]]::new()
    $routeC_nextSend = @(0, 0)
    $routeC_nextReceive = @(0, 0)
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    try
    {
        $routeC_serverProcess = Start-Process `
            -FilePath ([IO.Path]::GetFullPath($ExecutablePath)) `
            -ArgumentList $routeC_argumentString `
            -PassThru `
            -WindowStyle Hidden `
            -RedirectStandardOutput $routeC_stdout `
            -RedirectStandardError $routeC_stderr
        $routeC_connectDeadline =
            [DateTime]::UtcNow.AddSeconds(4)
        for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
        {
            while ([DateTime]::UtcNow -lt $routeC_connectDeadline)
            {
                $routeC_candidate = [Net.Sockets.TcpClient]::new()
                try
                {
                    $routeC_candidate.NoDelay = $true
                    $routeC_candidate.ReceiveTimeout = 1000
                    $routeC_candidate.SendTimeout = 1000
                    $routeC_candidate.Connect('127.0.0.1', 8888)
                    $routeC_clients[$routeC_player] = $routeC_candidate
                    break
                }
                catch [Net.Sockets.SocketException]
                {
                    $routeC_candidate.Dispose()
                    [Threading.Thread]::Sleep(25)
                }
            }
            Assert-RouteCKcpMatrix `
                ($null -ne $routeC_clients[$routeC_player]) `
                "TCP control client $routeC_player did not connect."
        }
        $routeC_streams = @(
            $routeC_clients[0].GetStream(),
            $routeC_clients[1].GetStream())
        $routeC_streams[0].ReadTimeout = 1000
        $routeC_streams[0].WriteTimeout = 1000
        $routeC_streams[1].ReadTimeout = 1000
        $routeC_streams[1].WriteTimeout = 1000
        $routeC_playerIndices = @(
            $routeC_streams[0].ReadByte(),
            $routeC_streams[1].ReadByte())
        Assert-RouteCKcpMatrix `
            (($routeC_playerIndices -join ',') -eq '0,1') `
            'TCP control barrier returned unexpected player indices.'

        $routeC_firstSendAtMs = 100
        $routeC_runDeadlineMs =
            $routeC_firstSendAtMs +
            $Contract.framesPerClient *
                $Contract.logicalCadenceMs +
            10000
        while (($routeC_nextReceive[0] -lt $Contract.framesPerClient -or
                $routeC_nextReceive[1] -lt $Contract.framesPerClient) -and
            $routeC_watch.ElapsedMilliseconds -lt $routeC_runDeadlineMs)
        {
            $routeC_nowMs = $routeC_watch.Elapsed.TotalMilliseconds
            for ($routeC_sender = 0; $routeC_sender -lt 2; $routeC_sender++)
            {
                while ($routeC_nextSend[$routeC_sender] -lt
                        $Contract.framesPerClient -and
                    $routeC_nowMs -ge
                        $routeC_firstSendAtMs +
                        $routeC_nextSend[$routeC_sender] *
                            $Contract.logicalCadenceMs)
                {
                    $routeC_frame = $routeC_nextSend[$routeC_sender]
                    $routeC_packet = [byte[]]::new(8)
                    $routeC_raw = [uint32](
                        1 + $routeC_sender * 1000000 + $routeC_frame)
                    [BitConverter]::GetBytes($routeC_raw).CopyTo(
                        $routeC_packet,
                        0)
                    [BitConverter]::GetBytes([int32]$routeC_frame).CopyTo(
                        $routeC_packet,
                        4)
                    $routeC_sentAt =
                        [Diagnostics.Stopwatch]::GetTimestamp()
                    $routeC_sendTimes[
                        [long]$routeC_sender * 1000000L +
                        $routeC_frame] = $routeC_sentAt
                    $routeC_streams[$routeC_sender].Write(
                        $routeC_packet,
                        0,
                        $routeC_packet.Length)
                    $routeC_nextSend[$routeC_sender]++
                    $routeC_nowMs =
                        $routeC_watch.Elapsed.TotalMilliseconds
                }
            }

            for ($routeC_receiver = 0;
                 $routeC_receiver -lt 2;
                 $routeC_receiver++)
            {
                $routeC_available =
                    $routeC_clients[$routeC_receiver].Available
                if ($routeC_available -gt 0)
                {
                    $routeC_chunk = [byte[]]::new($routeC_available)
                    $routeC_read = $routeC_streams[$routeC_receiver].Read(
                        $routeC_chunk,
                        0,
                        $routeC_chunk.Length)
                    for ($routeC_index = 0;
                         $routeC_index -lt $routeC_read;
                         $routeC_index++)
                    {
                        $routeC_buffers[$routeC_receiver].Add(
                            $routeC_chunk[$routeC_index])
                    }
                }
                while ($routeC_buffers[$routeC_receiver].Count -ge 8)
                {
                    $routeC_packet =
                        $routeC_buffers[$routeC_receiver].GetRange(0, 8).ToArray()
                    $routeC_buffers[$routeC_receiver].RemoveRange(0, 8)
                    $routeC_frame = [BitConverter]::ToInt32(
                        $routeC_packet,
                        4)
                    $routeC_raw = [BitConverter]::ToUInt32(
                        $routeC_packet,
                        0)
                    Assert-RouteCKcpMatrix `
                        ($routeC_frame -eq
                            $routeC_nextReceive[$routeC_receiver]) `
                        ("TCP control receiver $routeC_receiver expected " +
                         "frame $($routeC_nextReceive[$routeC_receiver]) " +
                         "but got $routeC_frame.")
                    $routeC_sender = 1 - $routeC_receiver
                    $routeC_expectedRaw = [uint32](
                        1 + $routeC_sender * 1000000 + $routeC_frame)
                    Assert-RouteCKcpMatrix `
                        ($routeC_raw -eq $routeC_expectedRaw) `
                        "TCP control frame $routeC_frame changed raw."
                    $routeC_receivedAt =
                        [Diagnostics.Stopwatch]::GetTimestamp()
                    $routeC_sentAt = [long]$routeC_sendTimes[
                        [long]$routeC_sender * 1000000L +
                        $routeC_frame]
                    $routeC_samples.Add([ordered]@{
                        receiverPlayer = $routeC_receiver
                        frameID = $routeC_frame
                        latencyMs = [Math]::Round(
                            ($routeC_receivedAt - $routeC_sentAt) *
                            1000.0 /
                            [Diagnostics.Stopwatch]::Frequency,
                            6)
                    })
                    $routeC_nextReceive[$routeC_receiver]++
                }
            }

            [Threading.Thread]::Sleep(1)
        }

        Assert-RouteCKcpMatrix `
            ($routeC_nextSend[0] -eq $Contract.framesPerClient -and
             $routeC_nextSend[1] -eq $Contract.framesPerClient -and
             $routeC_nextReceive[0] -eq $Contract.framesPerClient -and
             $routeC_nextReceive[1] -eq $Contract.framesPerClient) `
            'TCP control did not complete all bidirectional business inputs.'
        $routeC_serverProcess.Refresh()
        $routeC_serverCpuMs =
            $routeC_serverProcess.TotalProcessorTime.TotalMilliseconds
    }
    finally
    {
        foreach ($routeC_client in $routeC_clients)
        {
            if ($null -ne $routeC_client)
            {
                $routeC_client.Dispose()
            }
        }
        if ($null -ne $routeC_serverProcess -and
            -not $routeC_serverProcess.HasExited)
        {
            $routeC_serverProcess.Kill()
            $routeC_serverProcess.WaitForExit(2000) | Out-Null
        }
    }

    $routeC_portReleased = $false
    $routeC_releaseDeadline = [DateTime]::UtcNow.AddSeconds(2)
    while (-not $routeC_portReleased -and
        [DateTime]::UtcNow -lt $routeC_releaseDeadline)
    {
        $routeC_releaseSocket = [Net.Sockets.Socket]::new(
            [Net.Sockets.AddressFamily]::InterNetwork,
            [Net.Sockets.SocketType]::Stream,
            [Net.Sockets.ProtocolType]::Tcp)
        try
        {
            $routeC_releaseSocket.ExclusiveAddressUse = $true
            $routeC_releaseSocket.Bind([Net.IPEndPoint]::new(
                [Net.IPAddress]::Loopback,
                8888))
            $routeC_portReleased = $true
        }
        catch [Net.Sockets.SocketException]
        {
            [Threading.Thread]::Sleep(25)
        }
        finally
        {
            $routeC_releaseSocket.Dispose()
        }
    }
    Assert-RouteCKcpMatrix $routeC_portReleased `
        'TCP control port 8888 was not released by the owned server.'

    $routeC_latencies = [double[]]@(
        $routeC_samples |
            ForEach-Object { [double]$_.latencyMs })
    $routeC_result = [ordered]@{
        schema = 'route-c-tcp-control-v1'
        status = 'PASS'
        transport = 'tcp'
        faultSemantics = 'tcp-recovered-loss-hol/application'
        environment = [ordered]@{
            machine = [Environment]::MachineName
            os = [Environment]::OSVersion.VersionString
            powershell = $PSVersionTable.PSVersion.ToString()
            stopwatchFrequency = [Diagnostics.Stopwatch]::Frequency
        }
        input = [ordered]@{
            framesPerClient = $Contract.framesPerClient
            businessInputBytes = 8
            logicalCadenceMs = $Contract.logicalCadenceMs
            seed = $Contract.seed
            recoveredLossPercent =
                $Contract.clientToServerDropPercent
            lossRecoveryDelayMs = 100
        }
        correctness = [ordered]@{
            sentPerClient = @($routeC_nextSend)
            receivedPerClient = @($routeC_nextReceive)
            ascending = $true
            missingBusinessDeliveries = 0
            duplicateBusinessDeliveries = 0
        }
        relayLatencyMs = [ordered]@{
            sampleCount = $routeC_latencies.Count
            p50 = [Math]::Round(
                (Get-RouteCPercentile $routeC_latencies 0.50),
                6)
            p95 = [Math]::Round(
                (Get-RouteCPercentile $routeC_latencies 0.95),
                6)
            p99 = [Math]::Round(
                (Get-RouteCPercentile $routeC_latencies 0.99),
                6)
        }
        applicationBytes = [long](
            2 * $Contract.framesPerClient * 8)
        serverProcessCpuMs = [Math]::Round($routeC_serverCpuMs, 6)
        cleanup = [ordered]@{
            ownedServerProcessId = $routeC_serverProcess.Id
            exactProcessStopped = $routeC_serverProcess.HasExited
            fixedPortReleased = $routeC_portReleased
        }
    }
    $routeC_json = $routeC_result | ConvertTo-Json -Depth 10
    Assert-RouteCSecretSafeJson $routeC_json 'tcp-control'
    $routeC_path = Join-Path `
        $DestinationDirectory `
        $Contract.tcpControlSummaryName
    $routeC_json | Set-Content -LiteralPath $routeC_path -Encoding UTF8
    Write-Output "PASS: TCP control completed: $routeC_path"
}

$routeC_contract = Get-RouteCKcpMatrixContract
$routeC_repoRoot = Split-Path -Parent (
    Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $OutputDirectory = $routeC_repoRoot
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Assert-RouteCKcpMatrix `
    (Test-Path -LiteralPath $ServerPath -PathType Leaf) `
    "Server assembly does not exist: $ServerPath"
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
if ($TcpControlOnly)
{
    Invoke-RouteCTcpControl $ServerPath $OutputDirectory $routeC_contract
    return
}
$routeC_probePath = Join-Path $PSScriptRoot 'KcpLiveSocketProbe.ps1'
Assert-RouteCKcpMatrix `
    (Test-Path -LiteralPath $routeC_probePath -PathType Leaf) `
    "Live probe does not exist: $routeC_probePath"
$routeC_pwsh = (Get-Process -Id $PID).Path
$routeC_tempDirectory = Join-Path `
    $PSScriptRoot `
    'obj\p2f-task11-matrix'
[IO.Directory]::CreateDirectory($routeC_tempDirectory) | Out-Null

$routeC_order = @(1, 5, 10, 20, 20, 10, 5, 1, 1, 5, 10, 20)
$routeC_runCounts = @{ 1 = 0; 5 = 0; 10 = 0; 20 = 0 }
$routeC_runRecords = [Collections.Generic.List[object]]::new()
$routeC_runOrder = [Collections.Generic.List[object]]::new()

foreach ($routeC_interval in $routeC_order)
{
    $routeC_runCounts[$routeC_interval]++
    $routeC_run = [int]$routeC_runCounts[$routeC_interval]
    $routeC_baseName =
        "p2f-kcp-interval-${routeC_interval}ms-run${routeC_run}"
    $routeC_tracePath = Join-Path `
        $OutputDirectory `
        ($routeC_baseName + '.jsonl')
    $routeC_runSummaryPath = Join-Path `
        $OutputDirectory `
        ($routeC_baseName + '-summary.json')
    $routeC_stdoutPath = Join-Path `
        $routeC_tempDirectory `
        ($routeC_baseName + '-stdout.log')
    $routeC_stderrPath = Join-Path `
        $routeC_tempDirectory `
        ($routeC_baseName + '-stderr.log')
    $routeC_serverPidPath = Join-Path `
        $routeC_tempDirectory `
        ($routeC_baseName + '-' +
         [Guid]::NewGuid().ToString('N') + '-server.pid')
    $routeC_arguments = @(
        '-NoProfile',
        '-File', $routeC_probePath,
        '-ServerPath', ([IO.Path]::GetFullPath($ServerPath)),
        '-Scenario', 'clean',
        '-IntervalMs', [string]$routeC_interval,
        '-FramesPerClient', [string]$routeC_contract.framesPerClient,
        '-SendIntervalMs', [string]$routeC_contract.logicalCadenceMs,
        '-Seed', [string]$routeC_contract.seed,
        '-DropPercent', [string]$routeC_contract.clientToServerDropPercent,
        '-EvidencePath', $routeC_tracePath,
        '-SummaryPath', $routeC_runSummaryPath,
        '-OwnedServerPidPath', $routeC_serverPidPath)
    $routeC_argumentString = @(
        $routeC_arguments |
            ForEach-Object {
                ConvertTo-RouteCProcessArgument ([string]$_)
            }) -join ' '
    $routeC_process = $null
    try
    {
        $routeC_process = Start-Process `
            -FilePath $routeC_pwsh `
            -ArgumentList $routeC_argumentString `
            -PassThru `
            -WindowStyle Hidden `
            -RedirectStandardOutput $routeC_stdoutPath `
            -RedirectStandardError $routeC_stderrPath
        $routeC_runOrder.Add([ordered]@{
            intervalMs = $routeC_interval
            run = $routeC_run
            ownedProcessId = $routeC_process.Id
            serverPidFileName = Split-Path -Leaf $routeC_serverPidPath
            traceName = $routeC_baseName + '.jsonl'
            runSummaryName = $routeC_baseName + '-summary.json'
        })
        $routeC_exited = $routeC_process.WaitForExit(
            $routeC_contract.runTimeoutMs)
        Assert-RouteCKcpMatrix `
            $routeC_exited `
            ("Matrix run $routeC_baseName exceeded " +
             "$($routeC_contract.runTimeoutMs)ms.")
        Assert-RouteCKcpMatrix `
            ($routeC_process.ExitCode -eq 0) `
            ("Matrix run $routeC_baseName failed with exit " +
             "$($routeC_process.ExitCode): " +
             (Get-Content -Raw -LiteralPath $routeC_stderrPath))
    }
    finally
    {
        $routeC_probeWasForced = $false
        if ($null -ne $routeC_process -and
            -not $routeC_process.HasExited)
        {
            $routeC_probeWasForced = $true
            $routeC_process.Kill()
            $routeC_probeStopped =
                $routeC_process.WaitForExit(2000)
            Assert-RouteCKcpMatrix `
                $routeC_probeStopped `
                "Timed-out probe $($routeC_process.Id) did not stop."
        }
        if (Test-Path -LiteralPath $routeC_serverPidPath -PathType Leaf)
        {
            $routeC_serverStopped =
                Stop-RouteCExactServerFromPidFile `
                    $routeC_serverPidPath `
                    $ServerPath
            Assert-RouteCKcpMatrix `
                $routeC_serverStopped `
                'Timed-out probe left its exact server process running.'
            Assert-RouteCKcpMatrix `
                (Wait-RouteCUdpPortReleased 8888 2000) `
                'Timed-out probe left UDP port 8888 bound.'
        }
        elseif ($routeC_probeWasForced)
        {
            Assert-RouteCKcpMatrix `
                $false `
                'Timed-out probe omitted the exact server PID handoff.'
        }
    }

    Assert-RouteCKcpMatrix `
        (Test-Path -LiteralPath $routeC_runSummaryPath -PathType Leaf) `
        "Matrix run $routeC_baseName omitted its summary JSON."
    Assert-RouteCKcpMatrix `
        (Test-Path -LiteralPath $routeC_tracePath -PathType Leaf) `
        "Matrix run $routeC_baseName omitted its JSONL trace."
    $routeC_runJson = Get-Content -Raw -LiteralPath $routeC_runSummaryPath
    Assert-RouteCSecretSafeJson $routeC_runJson $routeC_baseName
    $routeC_summary = $routeC_runJson | ConvertFrom-Json
    Assert-RouteCKcpMatrix `
        (Test-Path -LiteralPath $routeC_serverPidPath -PathType Leaf) `
        "Matrix run $routeC_baseName omitted its server PID handoff."
    Assert-RouteCKcpMatrix `
        ([int]([IO.File]::ReadAllText($routeC_serverPidPath).Trim()) -eq
            [int]$routeC_summary.evidenceProvenance.serverProcessId) `
        "Matrix run $routeC_baseName server PID handoff mismatched."
    Assert-RouteCKcpMatrix `
        ($routeC_summary.status -eq 'PASS') `
        "Matrix run $routeC_baseName did not pass."
    Assert-RouteCKcpMatrix `
        ($routeC_summary.evidenceProvenance.serverLaunchMode -eq
            'networkserver-exe-cli') `
        "Matrix run $routeC_baseName bypassed the real server CLI."
    Assert-RouteCKcpMatrix `
        ($routeC_summary.correctness.sentPerClient[0] -eq 900 -and
         $routeC_summary.correctness.sentPerClient[1] -eq 900 -and
         $routeC_summary.correctness.receivedPerClient[0] -eq 900 -and
         $routeC_summary.correctness.receivedPerClient[1] -eq 900 -and
         $routeC_summary.correctness.missingBusinessDeliveries -eq 0 -and
         $routeC_summary.correctness.duplicateBusinessDeliveries -eq 0) `
        "Matrix run $routeC_baseName failed business correctness."
    Assert-RouteCKcpMatrix `
        ($routeC_summary.kcpUpdate.requestedIntervalMs -eq
            $routeC_interval -and
         $routeC_summary.kcpUpdate.coreEffectiveIntervalMs -eq
            $routeC_interval) `
        "Matrix run $routeC_baseName changed requested/effective interval."
    if ($routeC_interval -eq 1 -or $routeC_interval -eq 5)
    {
        Assert-RouteCKcpMatrix `
            (@($routeC_summary.kcpUpdate.sessions |
                Where-Object {
                    $_.sampleCount -le 0 -or $_.sub10msCount -le 0
                }).Count -eq 0) `
            ("Matrix run $routeC_baseName lacks per-session " +
             'sub-10ms Update evidence.')
    }
    Assert-RouteCKcpMatrix `
        ($routeC_summary.kcpUpdate.actualGapMs.sampleCount -gt 0 -and
         $routeC_summary.kcpUpdate.wakeupErrorMs.sampleCount -gt 0 -and
         $routeC_summary.relayLatencyMs.sampleCount -eq 1800) `
        "Matrix run $routeC_baseName omitted timing samples."
    Assert-RouteCKcpMatrix `
        ($routeC_summary.transportMetrics.intentionallyDroppedDatagrams -gt
            0) `
        "Matrix run $routeC_baseName did not exercise UDP datagram loss."
    Assert-RouteCKcpMatrix `
        ($routeC_summary.cleanup.serverStopped -and
         $routeC_summary.cleanup.exactOwnedServerProcessStopped -and
         $routeC_summary.cleanup.clientWorkersCompleted -eq 2 -and
         $routeC_summary.cleanup.portReleased) `
        "Matrix run $routeC_baseName failed bounded cleanup."
    $routeC_traceLines = Get-Content -LiteralPath $routeC_tracePath
    Assert-RouteCKcpMatrix `
        ($routeC_traceLines.Count -eq 1800) `
        ("Matrix run $routeC_baseName trace has " +
         "$($routeC_traceLines.Count) events instead of 1800.")
    foreach ($routeC_line in $routeC_traceLines)
    {
        Assert-RouteCSecretSafeJson $routeC_line $routeC_baseName
        $null = $routeC_line | ConvertFrom-Json
    }
    $routeC_runRecords.Add($routeC_summary)
    Write-Output (
        "PASS: $routeC_baseName pid=$($routeC_process.Id) " +
        "latencyP99=$($routeC_summary.relayLatencyMs.p99)ms " +
        "cpu=$($routeC_summary.workerCpu.aggregatePercentOfOneCore)%")
}

$routeC_candidates = @()
foreach ($routeC_interval in $routeC_contract.intervalsMs)
{
    $routeC_runs = @(
        $routeC_runRecords |
            Where-Object {
                $_.kcpUpdate.requestedIntervalMs -eq $routeC_interval
            })
    Assert-RouteCKcpMatrix `
        ($routeC_runs.Count -eq 3) `
        "Interval ${routeC_interval}ms does not have three summaries."
    $routeC_requestedValues = @(
        $routeC_runs.kcpUpdate.requestedIntervalMs |
            Sort-Object -Unique)
    $routeC_coreValues = @(
        $routeC_runs.kcpUpdate.coreEffectiveIntervalMs |
            Sort-Object -Unique)
    Assert-RouteCKcpMatrix `
        ($routeC_requestedValues.Count -eq 1 -and
         $routeC_requestedValues[0] -eq $routeC_interval -and
         $routeC_coreValues.Count -eq 1 -and
         $routeC_coreValues[0] -eq $routeC_interval) `
        "Interval ${routeC_interval}ms lacks direct core interval proof."
    $routeC_candidates += [ordered]@{
        intervalMs = $routeC_interval
        requestedIntervalMs = [int]$routeC_requestedValues[0]
        coreEffectiveIntervalMs = [int]$routeC_coreValues[0]
        passedRuns = 3
        correctnessPassed = $true
        relayLatencyMs = [ordered]@{
            p50Median = [Math]::Round((Get-RouteCMedian `
                ([double[]]@($routeC_runs.relayLatencyMs.p50))), 6)
            p95Median = [Math]::Round((Get-RouteCMedian `
                ([double[]]@($routeC_runs.relayLatencyMs.p95))), 6)
            p99Median = [Math]::Round((Get-RouteCMedian `
                ([double[]]@($routeC_runs.relayLatencyMs.p99))), 6)
        }
        totalDatagramsMedian = [Math]::Round((Get-RouteCMedian `
            ([double[]]@(
                $routeC_runs.transportMetrics.totalDatagrams))), 3)
        totalBytesMedian = [Math]::Round((Get-RouteCMedian `
            ([double[]]@(
                $routeC_runs.transportMetrics.totalBytes))), 3)
        kcpOutputDatagramsMedian = [Math]::Round((Get-RouteCMedian `
            ([double[]]@(
                $routeC_runs.transportMetrics.kcpOutputDatagrams))), 3)
        kcpOutputPayloadBytesMedian = [Math]::Round((Get-RouteCMedian `
            ([double[]]@(
                $routeC_runs.transportMetrics.kcpOutputPayloadBytes))), 3)
        kcpUpdateCallsMedian = [Math]::Round((Get-RouteCMedian `
            ([double[]]@(
                $routeC_runs.transportMetrics.kcpUpdateCalls))), 3)
        retransmittedSegmentsMedian = [Math]::Round((Get-RouteCMedian `
            ([double[]]@(
                $routeC_runs.transportMetrics.retransmittedSegments))), 3)
        waitSndHighWaterMaximum = [int](
            ($routeC_runs.transportMetrics.waitSndHighWater |
                Measure-Object -Maximum).Maximum)
        workerCpuPercentMedian = [Math]::Round((Get-RouteCMedian `
            ([double[]]@(
                $routeC_runs.workerCpu.aggregatePercentOfOneCore))), 6)
        actualUpdateGapMs = [ordered]@{
            p50Median = [Math]::Round((Get-RouteCMedian `
                ([double[]]@($routeC_runs.kcpUpdate.actualGapMs.p50))), 6)
            p95Median = [Math]::Round((Get-RouteCMedian `
                ([double[]]@($routeC_runs.kcpUpdate.actualGapMs.p95))), 6)
            p99Median = [Math]::Round((Get-RouteCMedian `
                ([double[]]@($routeC_runs.kcpUpdate.actualGapMs.p99))), 6)
        }
        wakeupErrorMs = [ordered]@{
            p50Median = [Math]::Round((Get-RouteCMedian `
                ([double[]]@($routeC_runs.kcpUpdate.wakeupErrorMs.p50))), 6)
            p95Median = [Math]::Round((Get-RouteCMedian `
                ([double[]]@($routeC_runs.kcpUpdate.wakeupErrorMs.p95))), 6)
            p99Median = [Math]::Round((Get-RouteCMedian `
                ([double[]]@($routeC_runs.kcpUpdate.wakeupErrorMs.p99))), 6)
            maximumAcrossRuns = [double](
                ($routeC_runs.kcpUpdate.wakeupErrorMs.maximum |
                    Measure-Object -Maximum).Maximum)
        }
        sub10msUpdateObservedInEverySessionEveryRun = [bool](
            @($routeC_runs |
                Where-Object {
                    @($_.kcpUpdate.sessions |
                        Where-Object {
                            $_.sampleCount -le 0 -or
                            $_.sub10msCount -le 0
                        }).Count -gt 0
                }).Count -eq 0)
    }
}

$routeC_summaryResult = [ordered]@{
    schema = 'route-c-kcp-interval-matrix-v1'
    status = 'PASS'
    transport = 'kcp-udp'
    faultSemantics = 'udp-datagram-drop'
    environment = $routeC_runRecords[0].environment
    evidenceProvenance = [ordered]@{
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        serverAssemblySha256 =
            (Get-FileHash -Algorithm SHA256 -LiteralPath $ServerPath).Hash
        probeScriptSha256 =
            (Get-FileHash -Algorithm SHA256 -LiteralPath $routeC_probePath).Hash
        matrixScriptSha256 =
            (Get-FileHash -Algorithm SHA256 -LiteralPath $PSCommandPath).Hash
    }
    conditions = [ordered]@{
        framesPerClient = $routeC_contract.framesPerClient
        businessInputBytes = 8
        logicalCadenceMs = $routeC_contract.logicalCadenceMs
        seed = $routeC_contract.seed
        clientToServerDropPercent =
            $routeC_contract.clientToServerDropPercent
        runsPerInterval = $routeC_contract.runsPerInterval
        sameMachine = [bool](
            @($routeC_runRecords.environment.machine |
                Sort-Object -Unique).Count -eq 1)
    }
    runOrder = @($routeC_runOrder)
    candidates = $routeC_candidates
    rawRunCount = $routeC_runRecords.Count
}
$routeC_summaryJson = $routeC_summaryResult | ConvertTo-Json -Depth 12
Assert-RouteCSecretSafeJson $routeC_summaryJson 'matrix-summary'
$routeC_summaryPath = Join-Path `
    $OutputDirectory `
    $routeC_contract.summaryName
$routeC_summaryJson | Set-Content `
    -LiteralPath $routeC_summaryPath `
    -Encoding UTF8
Write-Output "PASS: 12-run KCP interval matrix completed: $routeC_summaryPath"
