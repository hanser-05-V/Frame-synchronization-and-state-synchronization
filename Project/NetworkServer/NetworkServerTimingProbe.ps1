param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(0, 1, 2)]
    [int]$Mode,

    [string]$ServerPath = '',

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [int]$PacketCount = 12,

    [int]$SendIntervalMs = 33,

    [int]$JitterMs = 0,

    [int]$ApplicationReorderPercent = 0,

    [int]$ReorderDelayMs = 0,

    [int]$DuplicatePercent = 0,

    [int]$RecoveredLossPercent = 0,

    [int]$LossRecoveryMs = 0,

    [uint32]$Seed = 1,

    [string]$DecisionTracePath = '',

    [string]$TimingTracePath = '',

    [switch]$RequirePerPacketCadence
)

$ErrorActionPreference = 'Stop'
$routeC_serverProcess = $null
$routeC_client0 = $null
$routeC_client1 = $null

if ([string]::IsNullOrWhiteSpace($ServerPath))
{
    $routeC_scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ServerPath = Join-Path $routeC_scriptDirectory 'NetworkServer.exe'
}

function Connect-RouteCProbeClient
{
    $routeC_deadline = [DateTime]::UtcNow.AddSeconds(4)
    while ([DateTime]::UtcNow -lt $routeC_deadline)
    {
        $routeC_candidate = [System.Net.Sockets.TcpClient]::new()
        try
        {
            $routeC_candidate.NoDelay = $true
            $routeC_candidate.Connect('127.0.0.1', 8888)
            return $routeC_candidate
        }
        catch [System.Net.Sockets.SocketException]
        {
            $routeC_candidate.Close()
            Start-Sleep -Milliseconds 25
        }
    }

    throw 'Server did not start listening on port 8888 within four seconds.'
}

function Read-RouteCProbePacket
{
    param([System.IO.Stream]$Stream)

    $routeC_buffer = [byte[]]::new(8)
    $routeC_offset = 0
    while ($routeC_offset -lt $routeC_buffer.Length)
    {
        $routeC_read = $Stream.Read(
            $routeC_buffer,
            $routeC_offset,
            $routeC_buffer.Length - $routeC_offset)
        if ($routeC_read -le 0)
        {
            throw 'Stream ended before a complete 8-byte frame arrived.'
        }
        $routeC_offset += $routeC_read
    }

    return $routeC_buffer
}

function Get-RouteCPercentile
{
    param(
        [double[]]$Values,
        [double]$Percentile
    )

    if (-not $Values -or $Values.Count -eq 0)
    {
        return 0.0
    }

    $routeC_sorted = @($Values | Sort-Object)
    $routeC_index = [Math]::Ceiling($Percentile * $routeC_sorted.Count) - 1
    $routeC_index = [Math]::Max(0, [Math]::Min($routeC_index, $routeC_sorted.Count - 1))
    return [double]$routeC_sorted[$routeC_index]
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
            [void]$routeC_builder.Append('\' * ($routeC_backslashes * 2 + 1))
            [void]$routeC_builder.Append('"')
            $routeC_backslashes = 0
            continue
        }

        if ($routeC_backslashes -gt 0)
        {
            [void]$routeC_builder.Append('\' * $routeC_backslashes)
            $routeC_backslashes = 0
        }
        [void]$routeC_builder.Append($routeC_character)
    }

    if ($routeC_backslashes -gt 0)
    {
        [void]$routeC_builder.Append('\' * ($routeC_backslashes * 2))
    }
    [void]$routeC_builder.Append('"')
    return $routeC_builder.ToString()
}

if ($PacketCount -lt 3)
{
    throw 'PacketCount must be at least three.'
}
if ($SendIntervalMs -le 0)
{
    throw 'SendIntervalMs must be positive.'
}
if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf))
{
    throw "Server executable does not exist: $ServerPath"
}

$routeC_outputDirectory = Split-Path -Parent $OutputPath
if ($routeC_outputDirectory -and -not (Test-Path -LiteralPath $routeC_outputDirectory))
{
    New-Item -ItemType Directory -Path $routeC_outputDirectory | Out-Null
}

try
{
    $routeC_startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $routeC_startInfo.FileName = $ServerPath
    $routeC_startInfo.UseShellExecute = $false
    $routeC_startInfo.RedirectStandardInput = $true
    $routeC_startInfo.RedirectStandardOutput = $true
    $routeC_startInfo.RedirectStandardError = $true
    $routeC_startInfo.CreateNoWindow = $true
    $routeC_useExplicitProfile =
        $JitterMs -ne 0 -or
        $ApplicationReorderPercent -ne 0 -or
        $ReorderDelayMs -ne 0 -or
        $DuplicatePercent -ne 0 -or
        $RecoveredLossPercent -ne 0 -or
        $LossRecoveryMs -ne 0 -or
        -not [string]::IsNullOrWhiteSpace($DecisionTracePath) -or
        -not [string]::IsNullOrWhiteSpace($TimingTracePath)
    if ($routeC_useExplicitProfile)
    {
        $routeC_baseDelayMs = if ($Mode -eq 1) { 100 } elseif ($Mode -eq 2) { 200 } else { 0 }
        $routeC_arguments = @(
            '--transport', 'tcp',
            '--delay-ms', $routeC_baseDelayMs,
            '--jitter-ms', $JitterMs,
            '--reorder-percent', $ApplicationReorderPercent,
            '--reorder-delay-ms', $ReorderDelayMs,
            '--duplicate-percent', $DuplicatePercent,
            '--loss-percent', $RecoveredLossPercent,
            '--loss-recovery-ms', $LossRecoveryMs,
            '--seed', $Seed)
        if (-not [string]::IsNullOrWhiteSpace($DecisionTracePath))
        {
            $routeC_arguments += @('--decision-trace', $DecisionTracePath)
        }
        if (-not [string]::IsNullOrWhiteSpace($TimingTracePath))
        {
            $routeC_arguments += @('--timing-trace', $TimingTracePath)
        }
        $routeC_startInfo.Arguments = @(
            $routeC_arguments |
                ForEach-Object { ConvertTo-RouteCProcessArgument ([string]$_) }
        ) -join ' '
    }
    $routeC_serverProcess = [System.Diagnostics.Process]::Start($routeC_startInfo)
    if (-not $routeC_useExplicitProfile)
    {
        $routeC_serverProcess.StandardInput.WriteLine($Mode)
        $routeC_serverProcess.StandardInput.Flush()
    }

    $routeC_client0 = Connect-RouteCProbeClient
    $routeC_client1 = Connect-RouteCProbeClient
    $routeC_stream0 = $routeC_client0.GetStream()
    $routeC_stream1 = $routeC_client1.GetStream()
    $routeC_stream0.ReadTimeout = 2000
    $routeC_stream1.ReadTimeout = 2000

    $routeC_player0 = $routeC_stream0.ReadByte()
    $routeC_player1 = $routeC_stream1.ReadByte()
    if ($routeC_player0 -ne 0 -or $routeC_player1 -ne 1)
    {
        throw "Unexpected player indices: P0=$routeC_player0, P1=$routeC_player1."
    }

    $routeC_packets = [System.Collections.Generic.List[byte[]]]::new()
    for ($routeC_frameIndex = 0; $routeC_frameIndex -lt $PacketCount; $routeC_frameIndex++)
    {
        $routeC_packet = [byte[]]::new(8)
        $routeC_raw = [uint32](0x1000 + $routeC_frameIndex)
        [BitConverter]::GetBytes($routeC_raw).CopyTo($routeC_packet, 0)
        [BitConverter]::GetBytes([int32]$routeC_frameIndex).CopyTo($routeC_packet, 4)
        $routeC_packets.Add($routeC_packet)
    }

    $routeC_stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $routeC_sendTimes = @{}
    $routeC_arrivals = [System.Collections.Generic.List[object]]::new()
    $routeC_receivedFrames = [System.Collections.Generic.HashSet[int]]::new()
    $routeC_nextFrame = 0
    $routeC_firstSendAtMs = 200
    $routeC_deadlineMs = $routeC_firstSendAtMs +
        $PacketCount * $SendIntervalMs + 2500

    while ($routeC_receivedFrames.Count -lt $PacketCount -and
        $routeC_stopwatch.Elapsed.TotalMilliseconds -lt $routeC_deadlineMs)
    {
        $routeC_nowMs = $routeC_stopwatch.Elapsed.TotalMilliseconds
        while ($routeC_nextFrame -lt $PacketCount -and
            $routeC_nowMs -ge $routeC_firstSendAtMs +
                $routeC_nextFrame * $SendIntervalMs)
        {
            $routeC_packet = $routeC_packets[$routeC_nextFrame]
            $routeC_sentAtMs = $routeC_stopwatch.Elapsed.TotalMilliseconds
            $routeC_stream0.Write($routeC_packet, 0, $routeC_packet.Length)
            $routeC_stream0.Flush()
            $routeC_sendTimes[$routeC_nextFrame] = $routeC_sentAtMs
            $routeC_nextFrame++
            $routeC_nowMs = $routeC_stopwatch.Elapsed.TotalMilliseconds
        }

        while ($routeC_stream1.DataAvailable)
        {
            $routeC_received = Read-RouteCProbePacket -Stream $routeC_stream1
            $routeC_receivedAtMs = $routeC_stopwatch.Elapsed.TotalMilliseconds
            $routeC_frame = [BitConverter]::ToInt32($routeC_received, 4)
            $routeC_raw = [BitConverter]::ToUInt32($routeC_received, 0)
            if ($routeC_frame -lt 0 -or $routeC_frame -ge $PacketCount)
            {
                throw "Received out-of-range frame $routeC_frame."
            }
            $routeC_sentAtMs = [double]$routeC_sendTimes[$routeC_frame]
            $routeC_arrivals.Add([pscustomobject]@{
                frame = $routeC_frame
                raw = ('0x{0:X8}' -f $routeC_raw)
                sentAtMs = [Math]::Round($routeC_sentAtMs, 3)
                receivedAtMs = [Math]::Round($routeC_receivedAtMs, 3)
                latencyMs = [Math]::Round($routeC_receivedAtMs - $routeC_sentAtMs, 3)
            })
            $routeC_receivedFrames.Add($routeC_frame) | Out-Null
        }

        [System.Threading.Thread]::Sleep(0)
    }

    if ($routeC_receivedFrames.Count -ne $PacketCount)
    {
        throw (
            "Expected $PacketCount unique forwarded frames but received " +
            "$($routeC_receivedFrames.Count).")
    }

    $routeC_ordered = @($routeC_arrivals | Sort-Object receivedAtMs, frame)
    $routeC_receiveGaps = [System.Collections.Generic.List[double]]::new()
    $routeC_burstSizes = [System.Collections.Generic.List[int]]::new()
    $routeC_currentBurst = 1
    for ($routeC_index = 1; $routeC_index -lt $routeC_ordered.Count; $routeC_index++)
    {
        $routeC_gap = [double]$routeC_ordered[$routeC_index].receivedAtMs -
            [double]$routeC_ordered[$routeC_index - 1].receivedAtMs
        $routeC_receiveGaps.Add([Math]::Round($routeC_gap, 3))
        if ($routeC_gap -le 10.0)
        {
            $routeC_currentBurst++
        }
        else
        {
            $routeC_burstSizes.Add($routeC_currentBurst)
            $routeC_currentBurst = 1
        }
    }
    $routeC_burstSizes.Add($routeC_currentBurst)

    $routeC_latencies = [double[]]@($routeC_ordered | ForEach-Object { $_.latencyMs })
    $routeC_gaps = [double[]]@($routeC_receiveGaps)
    $routeC_maxBurst = ($routeC_burstSizes | Measure-Object -Maximum).Maximum
    $routeC_result = [ordered]@{
        transport = 'tcp'
        faultSemantics = [ordered]@{
            recoveredLoss = 'tcp-recovered-loss-hol'
            applicationReorder = 'tcp-application-reorder'
            duplicate = 'tcp-application-duplicate'
        }
        mode = $Mode
        packetCount = $PacketCount
        uniqueFrameCount = $routeC_receivedFrames.Count
        duplicateArrivalCount = $routeC_arrivals.Count - $routeC_receivedFrames.Count
        sendIntervalMs = $SendIntervalMs
        samples = $routeC_ordered
        receiveGapsMs = @($routeC_receiveGaps)
        burstSizes = @($routeC_burstSizes)
        summary = [ordered]@{
            latencyP50Ms = [Math]::Round((Get-RouteCPercentile $routeC_latencies 0.50), 3)
            latencyP95Ms = [Math]::Round((Get-RouteCPercentile $routeC_latencies 0.95), 3)
            receiveGapP50Ms = [Math]::Round((Get-RouteCPercentile $routeC_gaps 0.50), 3)
            receiveGapP95Ms = [Math]::Round((Get-RouteCPercentile $routeC_gaps 0.95), 3)
            maximumBurstSize = [int]$routeC_maxBurst
            frameOrder = @($routeC_ordered | ForEach-Object { $_.frame })
        }
    }

    $routeC_result | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $OutputPath -Encoding UTF8

    $routeC_summary = $routeC_result.summary
    Write-Output (
        "transport=tcp fault=tcp-recovered-loss-hol/application-fault " +
        "mode=$Mode latencyP50=$($routeC_summary.latencyP50Ms)ms " +
        "gapP50=$($routeC_summary.receiveGapP50Ms)ms " +
        "maxBurst=$($routeC_summary.maximumBurstSize)")

    if ($RequirePerPacketCadence -and
        ($routeC_summary.maximumBurstSize -gt 1 -or
         $routeC_summary.receiveGapP50Ms -lt 20.0))
    {
        throw (
            "Per-packet cadence requirement failed: " +
            "gapP50=$($routeC_summary.receiveGapP50Ms)ms " +
            "maxBurst=$($routeC_summary.maximumBurstSize).")
    }
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
