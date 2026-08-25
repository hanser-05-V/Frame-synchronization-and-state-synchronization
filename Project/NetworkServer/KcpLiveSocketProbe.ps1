param(
    [string]$ServerPath = '',

    [ValidateSet('clean', 'reconnect', 'grace-expired', 'history-expired')]
    [string]$Scenario = 'clean',

    [ValidateSet(1, 5, 10, 20)]
    [int]$IntervalMs = 10,

    [ValidateRange(900, 100000)]
    [int]$FramesPerClient = 900,

    [ValidateRange(1, 1000)]
    [int]$SendIntervalMs = 33,

    [uint32]$Seed = 20260824,

    [ValidateRange(0, 30)]
    [int]$DropPercent = 0,

    [string]$EvidencePath = '',

    [string]$SummaryPath = '',

    [string]$OwnedServerPidPath = '',

    [switch]$LifecycleSmoke,

    [switch]$ContractOnly,

    [switch]$ClientWorker,

    [int]$ServerPort = 0,

    [ValidateRange(0, 1)]
    [int]$ClientOrdinal = 0,

    [object]$SharedSendTimes
)

$ErrorActionPreference = 'Stop'

function Get-RouteCLiveProbeContract
{
    return [ordered]@{
        schema = 'route-c-kcp-live-socket-v1'
        allowedIntervalsMs = @(1, 5, 10, 20)
        allowedScenarios = @(
            'clean',
            'reconnect',
            'grace-expired',
            'history-expired')
        minimumFramesPerClient = 900
        businessInputBytes = 8
        logicalCadenceMs = 33
        timeouts = [ordered]@{
            startupMs = 2000
            readyMs = 3000
            runMs = 60000
            stopMs = 500
            cleanupMs = 2000
        }
        forbiddenFields = @(
            'token',
            'nonce',
            'resumeAttemptID',
            'sessionID')
        processCleanup = 'owned-exact-pid-only'
        portStrategy = 'dynamic-loopback'
        cleanServerLaunchMode = 'networkserver-exe-cli'
        cleanServerPort = 8888
    }
}

function Write-RouteCLiveResult
{
    param($Result, [string]$OutputPath)
    $routeC_json = $Result | ConvertTo-Json -Depth 12
    if ($routeC_json -match
        '(?i)"[^"]*(?:token|nonce|attemptid|sessionid)[^"]*"\s*:')
    {
        throw 'Live probe result contains a forbidden secret-like field.'
    }
    if (-not [string]::IsNullOrWhiteSpace($OutputPath))
    {
        $routeC_directory = Split-Path -Parent $OutputPath
        if (-not [string]::IsNullOrWhiteSpace($routeC_directory))
        {
            [IO.Directory]::CreateDirectory($routeC_directory) | Out-Null
        }
        $routeC_json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    }
    Write-Output $routeC_json
}

function Assert-RouteCLiveProbe
{
    param([bool]$Condition, [string]$Stage, [string]$Message)
    if (-not $Condition)
    {
        throw "STAGE ${Stage}: $Message"
    }
}

function Get-RouteCCoreIntervalMs
{
    param($KcpSession)
    $routeC_binding = [Reflection.BindingFlags]'Instance, NonPublic'
    $routeC_core = $KcpSession.GetType().GetField(
        '_kcp',
        $routeC_binding).GetValue($KcpSession)
    $routeC_intervalField = $routeC_core.GetType().GetField(
        'interval',
        $routeC_binding)
    Assert-RouteCLiveProbe `
        ($null -ne $routeC_intervalField) `
        'kcp-core-interval' `
        'kcp2k internal interval field is unavailable.'
    return [int]$routeC_intervalField.GetValue($routeC_core)
}

function Get-RouteCServerCoreIntervals
{
    param($Server)
    $routeC_binding = [Reflection.BindingFlags]'Instance, NonPublic'
    $routeC_router = $Server.GetType().GetField(
        '_router',
        $routeC_binding).GetValue($Server)
    $routeC_sessions = $routeC_router.GetType().GetField(
        '_sessions',
        $routeC_binding).GetValue($routeC_router)
    return [int[]]@(
        $routeC_sessions |
            Where-Object { $null -ne $_ -and $null -ne $_.KcpSession } |
            ForEach-Object {
                Get-RouteCCoreIntervalMs $_.KcpSession
            })
}

function New-RouteCLiveNonce
{
    param([byte]$First)
    $routeC_nonce = [byte[]]::new(16)
    for ($routeC_index = 0; $routeC_index -lt $routeC_nonce.Length; $routeC_index++)
    {
        $routeC_nonce[$routeC_index] = [byte]($First + $routeC_index)
    }
    return ,$routeC_nonce
}

function New-RouteCLiveSocket
{
    $routeC_socket = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_socket.Bind([Net.IPEndPoint]::new(
        [Net.IPAddress]::Loopback,
        0))
    return $routeC_socket
}

function Send-RouteCLiveEnvelope
{
    param(
        $Types,
        [Net.Sockets.Socket]$Socket,
        [string]$TypeName,
        $SessionId,
        [uint32]$Generation,
        [byte[]]$Payload)
    $routeC_type = [Enum]::Parse($Types.MessageType, $TypeName)
    $routeC_datagram = [byte[]]$Types.Codec.GetMethod('Encode').Invoke(
        $null,
        @($routeC_type, $SessionId, $Generation, $Payload))
    $Socket.Send($routeC_datagram) | Out-Null
}

function Receive-RouteCLiveMessage
{
    param(
        $Types,
        [Net.Sockets.Socket]$Socket,
        [string]$ExpectedType,
        [int]$TimeoutMs)
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    while ($routeC_watch.ElapsedMilliseconds -lt $TimeoutMs)
    {
        if (-not $Socket.Poll(10000, [Net.Sockets.SelectMode]::SelectRead))
        {
            continue
        }
        $routeC_buffer = [byte[]]::new(1200)
        $routeC_count = $Socket.Receive($routeC_buffer)
        $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
        $routeC_arguments = @($routeC_buffer, $routeC_count, $null, $routeC_reason)
        if (-not $Types.Codec.GetMethod('TryDecode').Invoke(
                $null,
                $routeC_arguments))
        {
            continue
        }
        $routeC_message = $routeC_arguments[2]
        if ($routeC_message.MessageType.ToString() -eq $ExpectedType)
        {
            return $routeC_message
        }
    }
    throw "Timed out waiting for live $ExpectedType."
}

function Get-RouteCLiveWelcomePlayer
{
    param($Types, $Message)
    $routeC_arguments = @(
        $Message.Payload,
        $null,
        [byte]0,
        [uint32]0,
        $null,
        0,
        0,
        $false)
    Assert-RouteCLiveProbe `
        $Types.Codec.GetMethod('TryDecodeWelcome').Invoke(
            $null,
            $routeC_arguments) `
        'welcome' `
        'server returned an invalid Welcome payload.'
    return [byte]$routeC_arguments[2]
}

function ConvertFrom-RouteCLiveWelcome
{
    param($Types, $Message)
    $routeC_arguments = @(
        $Message.Payload,
        $null,
        [byte]0,
        [uint32]0,
        $null,
        0,
        0,
        $false)
    Assert-RouteCLiveProbe `
        $Types.Codec.GetMethod('TryDecodeWelcome').Invoke(
            $null,
            $routeC_arguments) `
        'welcome' `
        'server returned an invalid Welcome payload.'
    return [pscustomobject]@{
        PlayerIndex = [byte]$routeC_arguments[2]
        Conversation = [uint32]$routeC_arguments[3]
        ReconnectToken = [byte[]]$routeC_arguments[4]
        ResumeRequired = [bool]$routeC_arguments[7]
    }
}

function Get-RouteCPercentile
{
    param([double[]]$Values, [double]$Percentile)
    if ($null -eq $Values -or $Values.Count -eq 0)
    {
        return 0.0
    }
    $routeC_sorted = @($Values | Sort-Object)
    $routeC_index = [Math]::Ceiling($Percentile * $routeC_sorted.Count) - 1
    $routeC_index = [Math]::Max(
        0,
        [Math]::Min($routeC_index, $routeC_sorted.Count - 1))
    return [double]$routeC_sorted[$routeC_index]
}

function ConvertTo-RouteCMilliseconds
{
    param([long[]]$Ticks)
    return [double[]]@(
        $Ticks |
            ForEach-Object {
                [double]$_ * 1000.0 / [Diagnostics.Stopwatch]::Frequency
            })
}

function New-RouteCTimingSummary
{
    param(
        [string]$Role,
        [int]$PlayerIndex,
        [long[]]$ActualGapTicks,
        [long[]]$WakeupErrorTicks)

    $routeC_gaps = ConvertTo-RouteCMilliseconds $ActualGapTicks
    $routeC_wakeups = ConvertTo-RouteCMilliseconds $WakeupErrorTicks
    Assert-RouteCLiveProbe `
        ($routeC_gaps.Count -gt 0 -and
         $routeC_gaps.Count -eq $routeC_wakeups.Count) `
        'timing-summary' `
        "$Role player $PlayerIndex has incomplete timing samples."
    $routeC_sub10Count = @(
        $routeC_gaps | Where-Object { $_ -lt 10.0 }).Count
    return [ordered]@{
        role = $Role
        playerIndex = $PlayerIndex
        sampleCount = $routeC_gaps.Count
        sub10msCount = $routeC_sub10Count
        sub10msFraction = [Math]::Round(
            $routeC_sub10Count / [double]$routeC_gaps.Count,
            8)
        actualGapMs = [ordered]@{
            p50 = [Math]::Round(
                (Get-RouteCPercentile $routeC_gaps 0.50), 6)
            p95 = [Math]::Round(
                (Get-RouteCPercentile $routeC_gaps 0.95), 6)
            p99 = [Math]::Round(
                (Get-RouteCPercentile $routeC_gaps 0.99), 6)
            maximum = [Math]::Round(
                (($routeC_gaps | Measure-Object -Maximum).Maximum), 6)
        }
        wakeupErrorMs = [ordered]@{
            p50 = [Math]::Round(
                (Get-RouteCPercentile $routeC_wakeups 0.50), 6)
            p95 = [Math]::Round(
                (Get-RouteCPercentile $routeC_wakeups 0.95), 6)
            p99 = [Math]::Round(
                (Get-RouteCPercentile $routeC_wakeups 0.99), 6)
            maximum = [Math]::Round(
                (($routeC_wakeups | Measure-Object -Maximum).Maximum), 6)
        }
    }
}

function Initialize-RouteCThreadCpuClock
{
    if ('RouteCThreadCpuClock' -as [type])
    {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class RouteCThreadCpuClock
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public long Ticks { get { return unchecked(((long)High << 32) | Low); } }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(
        IntPtr thread,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    public static long GetCurrentTicks()
    {
        try
        {
            FileTime creation;
            FileTime exit;
            FileTime kernel;
            FileTime user;
            if (!GetThreadTimes(
                    GetCurrentThread(),
                    out creation,
                    out exit,
                    out kernel,
                    out user))
            {
                return -1L;
            }
            return kernel.Ticks + user.Ticks;
        }
        catch (DllNotFoundException)
        {
            return -1L;
        }
        catch (EntryPointNotFoundException)
        {
            return -1L;
        }
    }
}
'@
}

function Add-RouteCKcpPushSequences
{
    param(
        [byte[]]$Payload,
        [int]$Count,
        [Collections.Generic.HashSet[string]]$Seen,
        $Metrics,
        [string]$Direction)
    $routeC_offset = 0
    while ($routeC_offset + 24 -le $Count)
    {
        $routeC_command = $Payload[$routeC_offset + 4]
        $routeC_sequence = [BitConverter]::ToUInt32(
            $Payload,
            $routeC_offset + 12)
        $routeC_length = [BitConverter]::ToUInt32(
            $Payload,
            $routeC_offset + 20)
        if ($routeC_length -gt [uint32]($Count - $routeC_offset - 24))
        {
            break
        }
        if ($routeC_command -eq 81)
        {
            $routeC_key = "${Direction}:$routeC_sequence"
            if (-not $Seen.Add($routeC_key))
            {
                $Metrics.RetransmittedSegments++
            }
        }
        $routeC_offset += 24 + [int]$routeC_length
    }
}

function Invoke-RouteCLiveClientWorker
{
    param(
        [string]$AssemblyPath,
        [int]$Port,
        [int]$RequestedIntervalMs,
        [int]$FrameCount,
        [int]$CadenceMs,
        [uint32]$FaultSeed,
        [int]$ClientIndex,
        [int]$ClientDropPercent,
        [object]$SendTimes)

    Initialize-RouteCThreadCpuClock
    Assert-RouteCLiveProbe `
        ($null -ne $SendTimes) `
        'worker-start' `
        'shared send timestamp map is missing.'
    $routeC_cpuStart = [RouteCThreadCpuClock]::GetCurrentTicks()
    $routeC_assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $routeC_types = [pscustomobject]@{
        SessionId = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCSessionId',
            $true)
        MessageType = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCMessageType',
            $true)
        DropReason = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCProtocolDropReason',
            $true)
        Codec = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCProtocolCodec',
            $true)
        Settings = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCKcpSettings',
            $true)
        KcpSession = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCKcpSession',
            $true)
        Clock = $routeC_assembly.GetType(
            'FrameSyncDemo.StopwatchMonotonicClock',
            $true)
    }
    $routeC_socket = New-RouteCLiveSocket
    $routeC_metrics = [pscustomobject]@{
        OutputDatagrams = 0L
        OutputBytes = 0L
        SentDatagrams = 0L
        SentBytes = 0L
        DroppedDatagrams = 0L
        InputDatagrams = 0L
        InputBytes = 0L
        RetransmittedSegments = 0L
        OutputSequence = 0L
    }
    $routeC_seenPush = [Collections.Generic.HashSet[string]]::new()
    $routeC_actualGapTicks = [Collections.Generic.List[long]]::new()
    $routeC_wakeupErrorTicks = [Collections.Generic.List[long]]::new()
    $routeC_effectiveIntervals = [Collections.Generic.List[int]]::new()
    $routeC_events = [Collections.Generic.List[object]]::new()
    $routeC_kcp = $null
    $routeC_playerIndex = -1
    $routeC_runWatch = [Diagnostics.Stopwatch]::StartNew()
    try
    {
        $routeC_socket.Connect([Net.IPAddress]::Loopback, $Port)
        $routeC_zeroSession = $routeC_types.SessionId.GetField(
            'Zero').GetValue($null)
        $routeC_nonce = New-RouteCLiveNonce ([byte](80 + 40 * $ClientIndex))
        $routeC_helloPayload = [byte[]]$routeC_types.Codec.GetMethod(
            'EncodeInitialHello').Invoke(
                $null,
                @(,$routeC_nonce))
        Send-RouteCLiveEnvelope `
            $routeC_types `
            $routeC_socket `
            'Hello' `
            $routeC_zeroSession `
            0 `
            $routeC_helloPayload
        $routeC_welcomeMessage = Receive-RouteCLiveMessage `
            $routeC_types $routeC_socket 'Welcome' 3000
        $routeC_welcome = ConvertFrom-RouteCLiveWelcome `
            $routeC_types $routeC_welcomeMessage
        Assert-RouteCLiveProbe `
            (-not $routeC_welcome.ResumeRequired) `
            'worker-welcome' `
            'initial Welcome unexpectedly requires resume.'
        $routeC_playerIndex = [int]$routeC_welcome.PlayerIndex
        $routeC_readyPayload = [byte[]]$routeC_types.Codec.GetMethod(
            'EncodeReady').Invoke(
                $null,
                @(-1, 0, -1))
        Send-RouteCLiveEnvelope `
            $routeC_types `
            $routeC_socket `
            'Ready' `
            $routeC_welcomeMessage.SessionId `
            $routeC_welcomeMessage.Generation `
            $routeC_readyPayload
        $null = Receive-RouteCLiveMessage `
            $routeC_types $routeC_socket 'Start' 3000

        $routeC_outputActionType = [Action``2].MakeGenericType(
            @([byte[]], [int]))
        $routeC_outputScript = {
            param([byte[]]$Bytes, [int]$Count)
            $routeC_segmentOffset = 0
            while ($routeC_segmentOffset + 24 -le $Count)
            {
                $routeC_command = $Bytes[$routeC_segmentOffset + 4]
                $routeC_segmentSequence = [BitConverter]::ToUInt32(
                    $Bytes,
                    $routeC_segmentOffset + 12)
                $routeC_segmentLength = [BitConverter]::ToUInt32(
                    $Bytes,
                    $routeC_segmentOffset + 20)
                if ($routeC_segmentLength -gt
                    [uint32]($Count - $routeC_segmentOffset - 24))
                {
                    break
                }
                if ($routeC_command -eq 81)
                {
                    $routeC_segmentKey =
                        "client-output:$routeC_segmentSequence"
                    if (-not $routeC_seenPush.Add($routeC_segmentKey))
                    {
                        $routeC_metrics.RetransmittedSegments++
                    }
                }
                $routeC_segmentOffset += 24 + [int]$routeC_segmentLength
            }
            $routeC_metrics.OutputDatagrams++
            $routeC_metrics.OutputBytes += $Count
            $routeC_roll = [uint64](
                ([uint64]$routeC_metrics.OutputSequence * 37UL +
                 [uint64]$FaultSeed +
                 [uint64]($ClientIndex + 1) * 53UL) % 100UL)
            $routeC_metrics.OutputSequence++
            if ($ClientDropPercent -gt 0 -and
                $routeC_roll -lt [uint64]$ClientDropPercent)
            {
                $routeC_metrics.DroppedDatagrams++
                return
            }
            $routeC_copy = [byte[]]::new($Count)
            [Buffer]::BlockCopy($Bytes, 0, $routeC_copy, 0, $Count)
            $routeC_datagram = [byte[]]$routeC_types.Codec.GetMethod(
                'Encode').Invoke(
                    $null,
                    @(
                        [Enum]::Parse(
                            $routeC_types.MessageType,
                            'KcpData'),
                        $routeC_welcomeMessage.SessionId,
                        $routeC_welcomeMessage.Generation,
                        $routeC_copy))
            $routeC_socket.Send($routeC_datagram) | Out-Null
            $routeC_metrics.SentDatagrams++
            $routeC_metrics.SentBytes += $routeC_datagram.Length
        }.GetNewClosure()
        $routeC_outputAction =
            [Management.Automation.LanguagePrimitives]::ConvertTo(
                $routeC_outputScript,
                $routeC_outputActionType)
        $routeC_settings = [Activator]::CreateInstance(
            $routeC_types.Settings,
            @($RequestedIntervalMs))
        $routeC_kcp = [Activator]::CreateInstance(
            $routeC_types.KcpSession,
            @(
                $routeC_welcome.Conversation,
                $routeC_settings,
                $routeC_outputAction))
        $routeC_clock = [Activator]::CreateInstance($routeC_types.Clock)
        $routeC_firstSendAtMs = 100
        $routeC_nextFrame = 0
        $routeC_nextRemoteFrame = 0
        $routeC_previousUpdateTimestamp = 0L
        $routeC_plannedUpdateTimestamp = 0L
        $routeC_hasPlannedUpdate = $false
        $routeC_deadlineMs =
            $routeC_firstSendAtMs +
            $FrameCount * $CadenceMs +
            30000
        $routeC_buffer = [byte[]]::new(1200)
        [uint32]$routeC_remoteRaw = 0
        [int]$routeC_remoteFrame = 0

        while (($routeC_nextFrame -lt $FrameCount -or
                $routeC_nextRemoteFrame -lt $FrameCount -or
                $routeC_kcp.PendingSendCount -gt 0) -and
            $routeC_runWatch.ElapsedMilliseconds -lt $routeC_deadlineMs)
        {
            $routeC_nowMs = [uint32]$routeC_clock.Milliseconds
            while ($routeC_nextFrame -lt $FrameCount -and
                $routeC_nowMs -ge
                    $routeC_firstSendAtMs +
                    $routeC_nextFrame * $CadenceMs)
            {
                $routeC_raw = [uint32](
                    1 + $routeC_playerIndex * 1000000 + $routeC_nextFrame)
                $routeC_sentAt = [Diagnostics.Stopwatch]::GetTimestamp()
                $routeC_key =
                    [long]$routeC_playerIndex * 1000000L +
                    [long]$routeC_nextFrame
                $SendTimes[$routeC_key] = $routeC_sentAt
                Assert-RouteCLiveProbe `
                    ($routeC_kcp.SendBusinessInput(
                        $routeC_raw,
                        $routeC_nextFrame) -eq 0) `
                    'worker-send' `
                    "KCP rejected frame $routeC_nextFrame."
                $routeC_nextFrame++
                $routeC_nowMs = [uint32]$routeC_clock.Milliseconds
            }

            for ($routeC_drain = 0;
                 $routeC_drain -lt 64 -and
                 $routeC_socket.Poll(
                    0,
                    [Net.Sockets.SelectMode]::SelectRead);
                 $routeC_drain++)
            {
                $routeC_count = $routeC_socket.Receive($routeC_buffer)
                $routeC_metrics.InputDatagrams++
                $routeC_metrics.InputBytes += $routeC_count
                $routeC_reason = [Enum]::ToObject(
                    $routeC_types.DropReason,
                    0)
                $routeC_decodeArguments = @(
                    $routeC_buffer,
                    $routeC_count,
                    $null,
                    $routeC_reason)
                if (-not $routeC_types.Codec.GetMethod(
                        'TryDecode').Invoke(
                            $null,
                            $routeC_decodeArguments))
                {
                    continue
                }
                $routeC_message = $routeC_decodeArguments[2]
                if ($routeC_message.MessageType.ToString() -ne 'KcpData')
                {
                    continue
                }
                Add-RouteCKcpPushSequences `
                    $routeC_message.Payload `
                    $routeC_message.Payload.Length `
                    $routeC_seenPush `
                    $routeC_metrics `
                    'server-output'
                $routeC_kcp.InputDatagram(
                    $routeC_message.Payload,
                    0,
                    $routeC_message.Payload.Length) | Out-Null
            }

            $routeC_nowMs = [uint32]$routeC_clock.Milliseconds
            $routeC_nextUpdateAt = $routeC_kcp.NextUpdateAt($routeC_nowMs)
            if ([int32]($routeC_nowMs - $routeC_nextUpdateAt) -ge 0)
            {
                $routeC_updateTimestamp =
                    [Diagnostics.Stopwatch]::GetTimestamp()
                if ($routeC_previousUpdateTimestamp -ne 0L)
                {
                    $routeC_actualGapTicks.Add(
                        $routeC_updateTimestamp -
                        $routeC_previousUpdateTimestamp)
                }
                if ($routeC_hasPlannedUpdate)
                {
                    $routeC_wakeupErrorTicks.Add(
                        $routeC_updateTimestamp -
                        $routeC_plannedUpdateTimestamp)
                }
                $routeC_kcp.Update(
                    $routeC_nowMs,
                    $routeC_updateTimestamp)
                $routeC_previousUpdateTimestamp = $routeC_updateTimestamp
                $routeC_snapshot = $routeC_kcp.SnapshotDiagnostics()
                $routeC_effectiveIntervals.Add(
                    [int]$routeC_snapshot.EffectiveCheckDeltaMs)
            }

            while ($routeC_kcp.TryReceiveBusinessInput(
                [ref]$routeC_remoteRaw,
                [ref]$routeC_remoteFrame))
            {
                Assert-RouteCLiveProbe `
                    ($routeC_remoteFrame -eq $routeC_nextRemoteFrame) `
                    'worker-order' `
                    ("expected remote frame $routeC_nextRemoteFrame " +
                     "but received $routeC_remoteFrame.")
                $routeC_expectedRemotePlayer = 1 - $routeC_playerIndex
                $routeC_expectedRaw = [uint32](
                    1 +
                    $routeC_expectedRemotePlayer * 1000000 +
                    $routeC_remoteFrame)
                Assert-RouteCLiveProbe `
                    ([uint32]$routeC_remoteRaw -eq $routeC_expectedRaw) `
                    'worker-content' `
                    "remote frame $routeC_remoteFrame changed its 8-byte raw."
                $routeC_receivedAt =
                    [Diagnostics.Stopwatch]::GetTimestamp()
                $routeC_remoteKey =
                    [long]$routeC_expectedRemotePlayer * 1000000L +
                    [long]$routeC_remoteFrame
                $routeC_sentAt = 0L
                Assert-RouteCLiveProbe `
                    $SendTimes.TryGetValue(
                        $routeC_remoteKey,
                        [ref]$routeC_sentAt) `
                    'worker-latency' `
                    "send timestamp is missing for frame $routeC_remoteFrame."
                $routeC_latencyMs =
                    ($routeC_receivedAt - $routeC_sentAt) *
                    1000.0 /
                    [Diagnostics.Stopwatch]::Frequency
                $routeC_events.Add([ordered]@{
                    schema = 'route-c-kcp-live-event-v1'
                    event = 'relay-delivery'
                    transport = 'kcp-udp'
                    faultSemantics = 'udp-datagram-drop'
                    scenario = 'clean'
                    requestedIntervalMs = $RequestedIntervalMs
                    receiverPlayer = $routeC_playerIndex
                    frameID = [int]$routeC_remoteFrame
                    raw = ('0x{0:X8}' -f [uint32]$routeC_remoteRaw)
                    sentAtStopwatchTicks = $routeC_sentAt
                    receivedAtStopwatchTicks = $routeC_receivedAt
                    latencyMs = [Math]::Round($routeC_latencyMs, 6)
                })
                $routeC_nextRemoteFrame++
            }

            $routeC_nowMs = [uint32]$routeC_clock.Milliseconds
            $routeC_nextUpdateAt = $routeC_kcp.NextUpdateAt($routeC_nowMs)
            $routeC_updateDelayMs = [Math]::Max(
                0L,
                [long]$routeC_nextUpdateAt - [long]$routeC_nowMs)
            $routeC_nextSendAt = if ($routeC_nextFrame -lt $FrameCount)
            {
                [uint32](
                    $routeC_firstSendAtMs +
                    $routeC_nextFrame * $CadenceMs)
            }
            else
            {
                [uint32]::MaxValue
            }
            $routeC_sendDelayMs = if (
                $routeC_nextSendAt -eq [uint32]::MaxValue)
            {
                [long]::MaxValue
            }
            else
            {
                [Math]::Max(
                    0L,
                    [long]$routeC_nextSendAt - [long]$routeC_nowMs)
            }
            $routeC_delayMs = [Math]::Min(
                $routeC_updateDelayMs,
                $routeC_sendDelayMs)
            $routeC_plannedUpdateTimestamp =
                [Diagnostics.Stopwatch]::GetTimestamp() +
                $routeC_updateDelayMs *
                [Diagnostics.Stopwatch]::Frequency /
                1000L
            $routeC_hasPlannedUpdate = $true
            $routeC_socket.Poll(
                [Math]::Min(1000000, $routeC_delayMs * 1000),
                [Net.Sockets.SelectMode]::SelectRead) | Out-Null
        }

        Assert-RouteCLiveProbe `
            ($routeC_nextFrame -eq $FrameCount) `
            'worker-complete' `
            "sent $routeC_nextFrame of $FrameCount frames."
        Assert-RouteCLiveProbe `
            ($routeC_nextRemoteFrame -eq $FrameCount) `
            'worker-complete' `
            "received $routeC_nextRemoteFrame of $FrameCount frames."
        Assert-RouteCLiveProbe `
            ($routeC_kcp.PendingSendCount -eq 0) `
            'worker-complete' `
            'KCP send queue did not drain.'

        $routeC_finalDiagnostics = $routeC_kcp.SnapshotDiagnostics()
        $routeC_cpuEnd = [RouteCThreadCpuClock]::GetCurrentTicks()
        return [ordered]@{
            status = 'PASS'
            playerIndex = $routeC_playerIndex
            framesSent = $routeC_nextFrame
            framesReceived = $routeC_nextRemoteFrame
            events = @($routeC_events)
            actualUpdateGapTicks = @($routeC_actualGapTicks)
            wakeupErrorTicks = @($routeC_wakeupErrorTicks)
            effectiveIntervalMsSamples = @($routeC_effectiveIntervals)
            requestedIntervalMs =
                [int]$routeC_finalDiagnostics.RequestedIntervalMs
            coreIntervalMs = Get-RouteCCoreIntervalMs $routeC_kcp
            kcpUpdateCalls = [long]$routeC_finalDiagnostics.UpdateCount
            outputDatagrams = $routeC_metrics.OutputDatagrams
            outputBytes = $routeC_metrics.OutputBytes
            sentDatagrams = $routeC_metrics.SentDatagrams
            sentBytes = $routeC_metrics.SentBytes
            droppedDatagrams = $routeC_metrics.DroppedDatagrams
            inputDatagrams = $routeC_metrics.InputDatagrams
            inputBytes = $routeC_metrics.InputBytes
            retransmittedSegments =
                $routeC_metrics.RetransmittedSegments
            waitSndHighWater = $routeC_finalDiagnostics.WaitSndHighWater
            workerCpuTimeTicks = if (
                $routeC_cpuStart -ge 0 -and $routeC_cpuEnd -ge 0)
            {
                [Math]::Max(0L, $routeC_cpuEnd - $routeC_cpuStart)
            }
            else
            {
                0L
            }
            durationMs = $routeC_runWatch.ElapsedMilliseconds
        }
    }
    finally
    {
        $routeC_socket.Dispose()
    }
}

function Invoke-RouteCCleanLiveRun
{
    param(
        [string]$AssemblyPath,
        [int]$RequestedIntervalMs,
        [int]$FrameCount,
        [int]$CadenceMs,
        [uint32]$FaultSeed,
        [int]$ClientDropPercent,
        [string]$TracePath)

    Initialize-RouteCThreadCpuClock
    Assert-RouteCLiveProbe `
        (Test-Path -LiteralPath $AssemblyPath -PathType Leaf) `
        'assembly' `
        "server assembly does not exist: $AssemblyPath"
    $routeC_startedAtUtc = [DateTime]::UtcNow
    $routeC_assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $routeC_jobs = @()
    $routeC_sharedSendTimes =
        [Collections.Concurrent.ConcurrentDictionary[long,long]]::new()
    $routeC_serverStopped = $false
    $routeC_boundPort = 8888
    $routeC_serverProcess = $null
    $routeC_runDirectory = Join-Path `
        $PSScriptRoot `
        ("obj\p2f-task11-cli-" + $PID + "-" +
         [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($routeC_runDirectory) | Out-Null
    $routeC_stopFile = Join-Path $routeC_runDirectory 'server.stop'
    $routeC_diagnosticsFile = Join-Path `
        $routeC_runDirectory `
        'server-diagnostics.json'
    $routeC_serverStdout = Join-Path $routeC_runDirectory 'server-stdout.log'
    $routeC_serverStderr = Join-Path $routeC_runDirectory 'server-stderr.log'
    $routeC_wallWatch = [Diagnostics.Stopwatch]::StartNew()
    try
    {
        Assert-RouteCLiveProbe `
            (Test-RouteCLivePortReleased $routeC_boundPort) `
            'startup' `
            'fixed KCP CLI port 8888 is already occupied.'
        $routeC_serverProcess = Start-Process `
            -FilePath ([IO.Path]::GetFullPath($AssemblyPath)) `
            -ArgumentList @(
                '--transport', 'kcp',
                '--kcp-interval-ms', [string]$RequestedIntervalMs,
                '--kcp-stop-file', $routeC_stopFile,
                '--kcp-diagnostics-output', $routeC_diagnosticsFile) `
            -PassThru `
            -WindowStyle Hidden `
            -RedirectStandardOutput $routeC_serverStdout `
            -RedirectStandardError $routeC_serverStderr
        if (-not [string]::IsNullOrWhiteSpace($OwnedServerPidPath))
        {
            $routeC_pidDirectory = Split-Path -Parent $OwnedServerPidPath
            if (-not [string]::IsNullOrWhiteSpace($routeC_pidDirectory))
            {
                [IO.Directory]::CreateDirectory(
                    $routeC_pidDirectory) | Out-Null
            }
            [IO.File]::WriteAllText(
                [IO.Path]::GetFullPath($OwnedServerPidPath),
                $routeC_serverProcess.Id.ToString(
                    [Globalization.CultureInfo]::InvariantCulture),
                [Text.Encoding]::ASCII)
        }
        $routeC_bindWatch = [Diagnostics.Stopwatch]::StartNew()
        $routeC_started = $false
        while (-not $routeC_started -and
            $routeC_bindWatch.ElapsedMilliseconds -lt 2000)
        {
            $routeC_serverProcess.Refresh()
            if ($routeC_serverProcess.HasExited)
            {
                break
            }
            if (Test-Path -LiteralPath $routeC_serverStdout)
            {
                $routeC_started = [bool](
                    Get-Content -LiteralPath $routeC_serverStdout -Raw |
                        Select-String 'KCP UDP listening on port 8888')
            }
            if (-not $routeC_started)
            {
                [Threading.Thread]::Sleep(10)
            }
        }
        Assert-RouteCLiveProbe `
            $routeC_started `
            'startup' `
            ("server executable did not bind within 2000ms: " +
             (Get-Content -LiteralPath $routeC_serverStderr -Raw `
                -ErrorAction SilentlyContinue))

        for ($routeC_ordinal = 0; $routeC_ordinal -lt 2; $routeC_ordinal++)
        {
            $routeC_workerParameters = @{
                ServerPath = $AssemblyPath
                IntervalMs = $RequestedIntervalMs
                FramesPerClient = $FrameCount
                SendIntervalMs = $CadenceMs
                Seed = $FaultSeed
                DropPercent = $ClientDropPercent
                ClientWorker = $true
                ServerPort = $routeC_boundPort
                ClientOrdinal = $routeC_ordinal
                SharedSendTimes = $routeC_sharedSendTimes
            }
            $routeC_jobs += Start-ThreadJob -ScriptBlock {
                param($ScriptPath, $WorkerParameters)
                & $ScriptPath @WorkerParameters
            } -ArgumentList $PSCommandPath, $routeC_workerParameters
        }

        $routeC_timeoutSeconds = [int][Math]::Ceiling(
            (10000.0 + $FrameCount * $CadenceMs + 30000.0) /
            1000.0)
        $null = Wait-Job -Job $routeC_jobs -Timeout $routeC_timeoutSeconds
        foreach ($routeC_job in $routeC_jobs)
        {
            $routeC_jobErrors = @(
                $routeC_job.ChildJobs |
                    ForEach-Object { $_.Error } |
                    ForEach-Object { $_.ToString() }) -join ' | '
            $routeC_failureReason = if (
                $null -ne $routeC_job.JobStateInfo.Reason)
            {
                $routeC_job.JobStateInfo.Reason.ToString()
            }
            else
            {
                $routeC_jobErrors
            }
            Assert-RouteCLiveProbe `
                ($routeC_job.State -eq 'Completed') `
                'client-worker' `
                ("client worker $($routeC_job.Id) ended as " +
                 "$($routeC_job.State): $routeC_failureReason")
        }
        $routeC_results = @(
            $routeC_jobs |
                ForEach-Object { Receive-Job -Job $_ -ErrorAction Stop })
        Assert-RouteCLiveProbe `
            ($routeC_results.Count -eq 2) `
            'client-worker' `
            'live run did not return two client worker results.'
        Assert-RouteCLiveProbe `
            (($routeC_results.playerIndex | Sort-Object) -join ',' -eq '0,1') `
            'client-worker' `
            'live workers did not own distinct player sessions.'

        if (-not [string]::IsNullOrWhiteSpace($TracePath))
        {
            $routeC_traceDirectory = Split-Path -Parent $TracePath
            if (-not [string]::IsNullOrWhiteSpace($routeC_traceDirectory))
            {
                [IO.Directory]::CreateDirectory(
                    $routeC_traceDirectory) | Out-Null
            }
            $routeC_traceEvents = @(
                foreach ($routeC_result in $routeC_results)
                {
                    foreach ($routeC_event in @($routeC_result.events))
                    {
                        $routeC_event
                    }
                })
            $routeC_traceLines = @(
                $routeC_traceEvents |
                    Sort-Object `
                        @{ Expression = {
                            [long]$_.receivedAtStopwatchTicks
                        } }, `
                        @{ Expression = { [int]$_.receiverPlayer } }, `
                        @{ Expression = { [int]$_.frameID } } |
                    ForEach-Object {
                        $_ | ConvertTo-Json -Depth 6 -Compress
                    })
            $routeC_traceLines | Set-Content `
                -LiteralPath $TracePath `
                -Encoding UTF8
        }
    }
    finally
    {
        foreach ($routeC_job in $routeC_jobs)
        {
            if ($routeC_job.State -eq 'Running' -or
                $routeC_job.State -eq 'NotStarted')
            {
                Stop-Job -Job $routeC_job -ErrorAction SilentlyContinue
            }
            Remove-Job -Job $routeC_job -Force -ErrorAction SilentlyContinue
        }
        if ($null -ne $routeC_serverProcess)
        {
            if (-not $routeC_serverProcess.HasExited)
            {
                'stop' | Set-Content `
                    -LiteralPath $routeC_stopFile `
                    -Encoding Ascii
                $routeC_serverStopped =
                    $routeC_serverProcess.WaitForExit(2000)
            }
            else
            {
                $routeC_serverStopped =
                    $routeC_serverProcess.ExitCode -eq 0
            }
            if (-not $routeC_serverProcess.HasExited)
            {
                $routeC_serverProcess.Kill()
                $routeC_serverProcess.WaitForExit(2000) | Out-Null
            }
        }
    }

    Assert-RouteCLiveProbe `
        $routeC_serverStopped `
        'stop' `
        'server executable did not stop cleanly within 2000ms.'
    Assert-RouteCLiveProbe `
        ($routeC_serverProcess.ExitCode -eq 0) `
        'stop' `
        ("server executable exited with " +
         $routeC_serverProcess.ExitCode + ': ' +
         (Get-Content -LiteralPath $routeC_serverStderr -Raw `
            -ErrorAction SilentlyContinue))
    Assert-RouteCLiveProbe `
        (Test-Path -LiteralPath $routeC_diagnosticsFile -PathType Leaf) `
        'diagnostics' `
        'server executable omitted its diagnostics JSON.'
    $routeC_portReleased = Test-RouteCLivePortReleased $routeC_boundPort
    Assert-RouteCLiveProbe `
        $routeC_portReleased `
        'cleanup' `
        'fixed KCP CLI port remained bound after cleanup.'

    $routeC_serverDiagnosticsJson = Get-Content `
        -LiteralPath $routeC_diagnosticsFile `
        -Raw
    Assert-RouteCLiveProbe `
        ($routeC_serverDiagnosticsJson -notmatch
            '(?i)"[^"]*(?:token|nonce|attemptid|sessionid)[^"]*"\s*:') `
        'diagnostics' `
        'server executable diagnostics contain a secret-like field.'
    $routeC_serverDiagnostics =
        $routeC_serverDiagnosticsJson | ConvertFrom-Json
    Assert-RouteCLiveProbe `
        ($routeC_serverDiagnostics.schema -eq
            'route-c-kcp-server-diagnostics-v1') `
        'diagnostics' `
        'server executable diagnostics schema changed.'
    $routeC_serverGaps = @(
        $routeC_serverDiagnostics.players |
            ForEach-Object { $_.actualUpdateGapTicks })
    $routeC_serverWakeups = @(
        $routeC_serverDiagnostics.players |
            ForEach-Object { $_.wakeupErrorTicks })
    $routeC_serverEffective = @(
        $routeC_serverDiagnostics.players |
            ForEach-Object { $_.effectiveCheckDeltaMsSamples })
    $routeC_clientGaps = @($routeC_results.actualUpdateGapTicks)
    $routeC_clientWakeups = @($routeC_results.wakeupErrorTicks)
    $routeC_clientEffective = @($routeC_results.effectiveIntervalMsSamples)
    $routeC_allGapMs = ConvertTo-RouteCMilliseconds `
        ([long[]]@($routeC_serverGaps + $routeC_clientGaps))
    $routeC_allWakeupMs = ConvertTo-RouteCMilliseconds `
        ([long[]]@($routeC_serverWakeups + $routeC_clientWakeups))
    $routeC_allEffective = [int[]]@(
        $routeC_serverEffective + $routeC_clientEffective)
    $routeC_latencies = [double[]]@(
        $routeC_results.events |
            ForEach-Object { [double]$_.latencyMs })
    $routeC_serverWaitSnd = 0
    $routeC_serverRequested = [Collections.Generic.List[int]]::new()
    $routeC_serverKcpOutputDatagrams = 0L
    $routeC_serverKcpOutputBytes = 0L
    $routeC_serverKcpUpdateCalls = 0L
    for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
    {
        $routeC_latest = $routeC_serverDiagnostics.players |
            Where-Object playerIndex -eq $routeC_player |
            Select-Object -First 1
        Assert-RouteCLiveProbe `
            ($null -ne $routeC_latest) `
            'diagnostics' `
            "server diagnostics omitted player $routeC_player."
        $routeC_serverRequested.Add(
            [int]$routeC_latest.requestedIntervalMs)
        $routeC_serverKcpOutputDatagrams +=
            [long]$routeC_latest.kcpOutputDatagrams
        $routeC_serverKcpOutputBytes +=
            [long]$routeC_latest.kcpOutputPayloadBytes
        $routeC_serverKcpUpdateCalls +=
            [long]$routeC_latest.kcpUpdateCalls
        $routeC_serverWaitSnd = [Math]::Max(
            $routeC_serverWaitSnd,
            [int]$routeC_latest.waitSndHighWater)
    }
    $routeC_observedRequested = [int[]]@(
        @($routeC_serverRequested) +
        @($routeC_results.requestedIntervalMs) |
            Sort-Object -Unique)
    Assert-RouteCLiveProbe `
        ($routeC_observedRequested.Count -eq 1 -and
         $routeC_observedRequested[0] -eq $RequestedIntervalMs) `
        'kcp-interval' `
        'live KCP sessions did not retain the requested core interval.'
    $routeC_observedCore = [int[]]@(
        @($routeC_serverDiagnostics.players.coreEffectiveIntervalMs) +
        @($routeC_results.coreIntervalMs) |
            Sort-Object -Unique)
    Assert-RouteCLiveProbe `
        ($routeC_observedCore.Count -eq 1 -and
         $routeC_observedCore[0] -eq $RequestedIntervalMs) `
        'kcp-core-interval' `
        ("kcp2k core interval differs from requested value: " +
         ($routeC_observedCore -join ','))
    $routeC_sessionTimings = @(
        for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
        {
            $routeC_playerDiagnostics =
                $routeC_serverDiagnostics.players |
                    Where-Object playerIndex -eq $routeC_player |
                    Select-Object -First 1
            New-RouteCTimingSummary `
                'server' `
                $routeC_player `
                ([long[]]$routeC_playerDiagnostics.actualUpdateGapTicks) `
                ([long[]]$routeC_playerDiagnostics.wakeupErrorTicks)
        }
        foreach ($routeC_result in $routeC_results)
        {
            New-RouteCTimingSummary `
                'client' `
                ([int]$routeC_result.playerIndex) `
                ([long[]]$routeC_result.actualUpdateGapTicks) `
                ([long[]]$routeC_result.wakeupErrorTicks)
        })
    $routeC_clientCpuTicks = [long](
        ($routeC_results.workerCpuTimeTicks |
            Measure-Object -Sum).Sum)
    $routeC_totalCpuTicks =
        $routeC_clientCpuTicks +
        [long]$routeC_serverDiagnostics.workerCpuTimeTicks
    $routeC_wallMs = [Math]::Max(1.0, $routeC_wallWatch.Elapsed.TotalMilliseconds)
    $routeC_totalCpuMs = $routeC_totalCpuTicks / 10000.0
    $routeC_totalDatagrams = [long](
        ($routeC_results.sentDatagrams |
            Measure-Object -Sum).Sum) +
        [long](
            ($routeC_results.inputDatagrams |
                Measure-Object -Sum).Sum)
    $routeC_totalBytes = [long](
        ($routeC_results.sentBytes |
            Measure-Object -Sum).Sum) +
        [long](
            ($routeC_results.inputBytes |
                Measure-Object -Sum).Sum)
    $routeC_waitSndHighWater = [Math]::Max(
            $routeC_serverWaitSnd,
        [int](
                ($routeC_results.waitSndHighWater |
                    Measure-Object -Maximum).Maximum))
    $routeC_clientKcpOutputDatagrams = [long](
        ($routeC_results.outputDatagrams |
            Measure-Object -Sum).Sum)
    $routeC_clientKcpOutputBytes = [long](
        ($routeC_results.outputBytes |
            Measure-Object -Sum).Sum)
    $routeC_clientKcpUpdateCalls = [long](
        ($routeC_results.kcpUpdateCalls |
            Measure-Object -Sum).Sum)

    return [ordered]@{
        schema = 'route-c-kcp-live-socket-v1'
        scenario = 'clean'
        status = 'PASS'
        transport = 'kcp-udp'
        faultSemantics = 'udp-datagram-drop'
        environment = [ordered]@{
            machine = [Environment]::MachineName
            os = [Environment]::OSVersion.VersionString
            powershell = $PSVersionTable.PSVersion.ToString()
            stopwatchFrequency = [Diagnostics.Stopwatch]::Frequency
        }
        evidenceProvenance = [ordered]@{
            startedAtUtc = $routeC_startedAtUtc.ToString('o')
            finishedAtUtc = [DateTime]::UtcNow.ToString('o')
            serverLaunchMode = 'networkserver-exe-cli'
            serverProcessId = $routeC_serverProcess.Id
            serverAssemblySha256 =
                (Get-FileHash -Algorithm SHA256 -LiteralPath $AssemblyPath).Hash
            probeScriptSha256 =
                (Get-FileHash -Algorithm SHA256 -LiteralPath $PSCommandPath).Hash
        }
        input = [ordered]@{
            framesPerClient = $FrameCount
            businessInputBytes = 8
            logicalCadenceMs = $CadenceMs
            seed = $FaultSeed
            clientToServerDropPercent = $ClientDropPercent
            serverPort = $routeC_boundPort
        }
        correctness = [ordered]@{
            players = @(0, 1)
            sentPerClient = @($routeC_results.framesSent)
            receivedPerClient = @($routeC_results.framesReceived)
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
        transportMetrics = [ordered]@{
            totalDatagrams = $routeC_totalDatagrams
            totalBytes = $routeC_totalBytes
            udpWireDatagrams = $routeC_totalDatagrams
            udpWireBytes = $routeC_totalBytes
            kcpOutputDatagrams =
                $routeC_clientKcpOutputDatagrams +
                $routeC_serverKcpOutputDatagrams
            kcpOutputPayloadBytes =
                $routeC_clientKcpOutputBytes +
                $routeC_serverKcpOutputBytes
            kcpUpdateCalls =
                $routeC_clientKcpUpdateCalls +
                $routeC_serverKcpUpdateCalls
            retransmittedSegments = [long](
                ($routeC_results.retransmittedSegments |
                    Measure-Object -Sum).Sum)
            intentionallyDroppedDatagrams = [long](
                ($routeC_results.droppedDatagrams |
                    Measure-Object -Sum).Sum)
            waitSndHighWater = $routeC_waitSndHighWater
        }
        kcpUpdate = [ordered]@{
            requestedIntervalMs = $RequestedIntervalMs
            coreEffectiveIntervalMs = $routeC_observedCore[0]
            checkDeltaValuesMs = @(
                $routeC_allEffective | Sort-Object -Unique)
            sessions = $routeC_sessionTimings
            actualGapMs = [ordered]@{
                sampleCount = $routeC_allGapMs.Count
                p50 = [Math]::Round(
                    (Get-RouteCPercentile $routeC_allGapMs 0.50),
                    6)
                p95 = [Math]::Round(
                    (Get-RouteCPercentile $routeC_allGapMs 0.95),
                    6)
                p99 = [Math]::Round(
                    (Get-RouteCPercentile $routeC_allGapMs 0.99),
                    6)
                maximum = [Math]::Round(
                    (($routeC_allGapMs | Measure-Object -Maximum).Maximum),
                    6)
                sub10msObserved = [bool](
                    $routeC_allGapMs |
                        Where-Object { $_ -lt 10.0 } |
                        Select-Object -First 1)
            }
            wakeupErrorMs = [ordered]@{
                sampleCount = $routeC_allWakeupMs.Count
                p50 = [Math]::Round(
                    (Get-RouteCPercentile $routeC_allWakeupMs 0.50),
                    6)
                p95 = [Math]::Round(
                    (Get-RouteCPercentile $routeC_allWakeupMs 0.95),
                    6)
                p99 = [Math]::Round(
                    (Get-RouteCPercentile $routeC_allWakeupMs 0.99),
                    6)
                maximum = [Math]::Round(
                    (($routeC_allWakeupMs |
                        Measure-Object -Maximum).Maximum),
                    6)
            }
        }
        workerCpu = [ordered]@{
            serverMeasurementAvailable =
                $routeC_serverDiagnostics.workerCpuMeasurementAvailable
            serverCpuMs = [Math]::Round(
                $routeC_serverDiagnostics.workerCpuTimeTicks / 10000.0,
                6)
            clientCpuMs = [Math]::Round(
                $routeC_clientCpuTicks / 10000.0,
                6)
            totalCpuMs = [Math]::Round($routeC_totalCpuMs, 6)
            aggregatePercentOfOneCore = [Math]::Round(
                $routeC_totalCpuMs * 100.0 / $routeC_wallMs,
                6)
        }
        cleanup = [ordered]@{
            serverStopped = $routeC_serverStopped
            exactOwnedServerProcessStopped =
                $routeC_serverProcess.HasExited
            clientWorkersCompleted = 2
            portReleased = $routeC_portReleased
            wallDurationMs = [Math]::Round($routeC_wallMs, 3)
        }
    }
}

function New-RouteCLiveResumeFixture
{
    param([string]$AssemblyPath, [int]$RequestedIntervalMs)

    $routeC_assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $routeC_types = [pscustomobject]@{
        Server = $routeC_assembly.GetType(
            'FrameSyncServer.KcpUdpRelayServer',
            $true)
        ServerResumeFailureKind = $routeC_assembly.GetType(
            'FrameSyncServer.ServerResumeFailureKind',
            $true)
        SessionId = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCSessionId',
            $true)
        MessageType = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCMessageType',
            $true)
        DropReason = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCProtocolDropReason',
            $true)
        ResumeRejectedReason = $routeC_assembly.GetType(
            'FrameSyncDemo.ResumeRejectedReason',
            $true)
        Codec = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCProtocolCodec',
            $true)
        Settings = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCKcpSettings',
            $true)
        KcpSession = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCKcpSession',
            $true)
        Clock = $routeC_assembly.GetType(
            'FrameSyncDemo.StopwatchMonotonicClock',
            $true)
    }
    $routeC_server = [Activator]::CreateInstance(
        $routeC_types.Server,
        @($RequestedIntervalMs, 0))
    $routeC_clients = @($null, $null)
    try
    {
        $routeC_server.Run()
        $routeC_bindWatch = [Diagnostics.Stopwatch]::StartNew()
        while ($routeC_server.BoundPort -le 0 -and
            $routeC_bindWatch.ElapsedMilliseconds -lt 2000)
        {
            [Threading.Thread]::Yield() | Out-Null
        }
        Assert-RouteCLiveProbe `
            ($routeC_server.BoundPort -gt 0) `
            'resume-startup' `
            'server did not bind within 2000ms.'
        $routeC_endpoint = [Net.IPEndPoint]::new(
            [Net.IPAddress]::Loopback,
            $routeC_server.BoundPort)
        $routeC_zero = $routeC_types.SessionId.GetField(
            'Zero').GetValue($null)
        for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
        {
            $routeC_clients[$routeC_player] = New-RouteCLiveSocket
            $routeC_clients[$routeC_player].Connect($routeC_endpoint)
            $routeC_nonce = New-RouteCLiveNonce ([byte](140 + 32 * $routeC_player))
            $routeC_helloPayload = [byte[]]$routeC_types.Codec.GetMethod(
                'EncodeInitialHello').Invoke(
                    $null,
                    @(,$routeC_nonce))
            Send-RouteCLiveEnvelope `
                $routeC_types `
                $routeC_clients[$routeC_player] `
                'Hello' `
                $routeC_zero `
                0 `
                $routeC_helloPayload
        }
        $routeC_welcomeMessages = @(
            (Receive-RouteCLiveMessage `
                $routeC_types $routeC_clients[0] 'Welcome' 3000),
            (Receive-RouteCLiveMessage `
                $routeC_types $routeC_clients[1] 'Welcome' 3000))
        $routeC_welcomes = @(
            (ConvertFrom-RouteCLiveWelcome `
                $routeC_types $routeC_welcomeMessages[0]),
            (ConvertFrom-RouteCLiveWelcome `
                $routeC_types $routeC_welcomeMessages[1]))
        Assert-RouteCLiveProbe `
            (($routeC_welcomes.PlayerIndex | Sort-Object) -join ',' -eq '0,1') `
            'resume-welcome' `
            'resume fixture did not assign distinct players.'
        $routeC_readyPayload = [byte[]]$routeC_types.Codec.GetMethod(
            'EncodeReady').Invoke(
                $null,
                @(-1, 0, -1))
        for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
        {
            Send-RouteCLiveEnvelope `
                $routeC_types `
                $routeC_clients[$routeC_player] `
                'Ready' `
                $routeC_welcomeMessages[$routeC_player].SessionId `
                $routeC_welcomeMessages[$routeC_player].Generation `
                $routeC_readyPayload
        }
        $null = Receive-RouteCLiveMessage `
            $routeC_types $routeC_clients[0] 'Start' 3000
        $null = Receive-RouteCLiveMessage `
            $routeC_types $routeC_clients[1] 'Start' 3000
        return [pscustomobject]@{
            Types = $routeC_types
            Server = $routeC_server
            Clients = $routeC_clients
            WelcomeMessages = $routeC_welcomeMessages
            Welcomes = $routeC_welcomes
            BoundPort = $routeC_server.BoundPort
        }
    }
    catch
    {
        foreach ($routeC_client in $routeC_clients)
        {
            if ($null -ne $routeC_client)
            {
                $routeC_client.Dispose()
            }
        }
        $routeC_server.RequestStop()
        $routeC_server.WaitForStop(500) | Out-Null
        $routeC_server.Dispose()
        throw
    }
}

function Get-RouteCServerSessionStates
{
    param($Server)
    $routeC_binding = [Reflection.BindingFlags]'Instance, NonPublic'
    $routeC_router = $Server.GetType().GetField(
        '_router',
        $routeC_binding).GetValue($Server)
    $routeC_sessions = $routeC_router.GetType().GetField(
        '_sessions',
        $routeC_binding).GetValue($routeC_router)
    return @(
        $routeC_sessions |
            ForEach-Object { $_.State.ToString() })
}

function Get-RouteCResumeProbeAttempt
{
    param($Types, $Message)
    $routeC_arguments = @($Message.Payload, $null)
    Assert-RouteCLiveProbe `
        $Types.Codec.GetMethod('TryDecodeResumeProbe').Invoke(
            $null,
            $routeC_arguments) `
        'resume-probe' `
        'ResumeProbe payload did not decode.'
    return ,[byte[]]$routeC_arguments[1]
}

function Get-RouteCResumeAcceptedAttempt
{
    param($Types, $Message)
    $routeC_arguments = @(
        $Message.Payload,
        $null,
        0,
        0,
        0,
        0,
        0,
        0)
    Assert-RouteCLiveProbe `
        $Types.Codec.GetMethod('TryDecodeResumeAccepted').Invoke(
            $null,
            $routeC_arguments) `
        'resume-accepted' `
        'ResumeAccepted payload did not decode.'
    return [pscustomobject]@{
        Attempt = [byte[]]$routeC_arguments[1]
        UploadFrom = [int]$routeC_arguments[2]
        UploadThrough = [int]$routeC_arguments[3]
        ReplayFrom = [int]$routeC_arguments[4]
        ReplayThrough = [int]$routeC_arguments[5]
        PeerFrom = [int]$routeC_arguments[6]
        PeerThrough = [int]$routeC_arguments[7]
    }
}

function Get-RouteCResumeRejectedReason
{
    param($Types, $Message)
    $routeC_arguments = @($Message.Payload, $null, [uint16]0)
    Assert-RouteCLiveProbe `
        $Types.Codec.GetMethod('TryDecodeResumeRejected').Invoke(
            $null,
            $routeC_arguments) `
        'resume-rejected' `
        'ResumeRejected payload did not decode.'
    return [Enum]::ToObject(
        $Types.ResumeRejectedReason,
        [uint16]$routeC_arguments[2]).ToString()
}

function Send-RouteCLivePeerHistory
{
    param($Fixture, [int]$FrameCount, [int]$RequestedIntervalMs)

    $routeC_types = $Fixture.Types
    $routeC_socket = $Fixture.Clients[1]
    $routeC_welcomeMessage = $Fixture.WelcomeMessages[1]
    $routeC_welcome = $Fixture.Welcomes[1]
    $routeC_outputType = [Action``2].MakeGenericType(@([byte[]], [int]))
    $routeC_outputScript = {
        param([byte[]]$Bytes, [int]$Count)
        $routeC_copy = [byte[]]::new($Count)
        [Buffer]::BlockCopy($Bytes, 0, $routeC_copy, 0, $Count)
        $routeC_datagram = [byte[]]$routeC_types.Codec.GetMethod(
            'Encode').Invoke(
                $null,
                @(
                    [Enum]::Parse($routeC_types.MessageType, 'KcpData'),
                    $routeC_welcomeMessage.SessionId,
                    $routeC_welcomeMessage.Generation,
                    $routeC_copy))
        $routeC_socket.Send($routeC_datagram) | Out-Null
    }.GetNewClosure()
    $routeC_output =
        [Management.Automation.LanguagePrimitives]::ConvertTo(
            $routeC_outputScript,
            $routeC_outputType)
    $routeC_settings = [Activator]::CreateInstance(
        $routeC_types.Settings,
        @($RequestedIntervalMs))
    $routeC_kcp = [Activator]::CreateInstance(
        $routeC_types.KcpSession,
        @($routeC_welcome.Conversation, $routeC_settings, $routeC_output))
    for ($routeC_frame = 0; $routeC_frame -lt $FrameCount; $routeC_frame++)
    {
        Assert-RouteCLiveProbe `
            ($routeC_kcp.SendBusinessInput(
                [uint32](1000001 + $routeC_frame),
                $routeC_frame) -eq 0) `
            'history-send' `
            "KCP rejected retained history frame $routeC_frame."
    }

    $routeC_clock = [Activator]::CreateInstance($routeC_types.Clock)
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    $routeC_buffer = [byte[]]::new(1200)
    while ($routeC_kcp.PendingSendCount -gt 0 -and
        $routeC_watch.ElapsedMilliseconds -lt 4000)
    {
        while ($routeC_socket.Poll(
            0,
            [Net.Sockets.SelectMode]::SelectRead))
        {
            $routeC_count = $routeC_socket.Receive($routeC_buffer)
            $routeC_reason = [Enum]::ToObject($routeC_types.DropReason, 0)
            $routeC_arguments = @(
                $routeC_buffer,
                $routeC_count,
                $null,
                $routeC_reason)
            if (-not $routeC_types.Codec.GetMethod('TryDecode').Invoke(
                    $null,
                    $routeC_arguments))
            {
                continue
            }
            $routeC_message = $routeC_arguments[2]
            if ($routeC_message.MessageType.ToString() -eq 'KcpData')
            {
                $routeC_kcp.InputDatagram(
                    $routeC_message.Payload,
                    0,
                    $routeC_message.Payload.Length) | Out-Null
            }
        }
        $routeC_nowMs = [uint32]$routeC_clock.Milliseconds
        if ([int32](
            $routeC_nowMs -
            $routeC_kcp.NextUpdateAt($routeC_nowMs)) -ge 0)
        {
            $routeC_kcp.Update(
                $routeC_nowMs,
                [Diagnostics.Stopwatch]::GetTimestamp())
        }
        $routeC_socket.Poll(
            1000,
            [Net.Sockets.SelectMode]::SelectRead) | Out-Null
    }
    Assert-RouteCLiveProbe `
        ($routeC_kcp.PendingSendCount -eq 0) `
        'history-send' `
        'peer history KCP send queue did not drain.'
    return $FrameCount
}

function Receive-RouteCLiveReplay
{
    param(
        $Fixture,
        $Socket,
        $WelcomeMessage,
        $Welcome,
        [int]$ExpectedFrameCount,
        [int]$RequestedIntervalMs)

    $routeC_types = $Fixture.Types
    $routeC_outputType = [Action``2].MakeGenericType(@([byte[]], [int]))
    $routeC_outputScript = {
        param([byte[]]$Bytes, [int]$Count)
        $routeC_copy = [byte[]]::new($Count)
        [Buffer]::BlockCopy($Bytes, 0, $routeC_copy, 0, $Count)
        $routeC_datagram = [byte[]]$routeC_types.Codec.GetMethod(
            'Encode').Invoke(
                $null,
                @(
                    [Enum]::Parse($routeC_types.MessageType, 'KcpData'),
                    $WelcomeMessage.SessionId,
                    $WelcomeMessage.Generation,
                    $routeC_copy))
        $Socket.Send($routeC_datagram) | Out-Null
    }.GetNewClosure()
    $routeC_output =
        [Management.Automation.LanguagePrimitives]::ConvertTo(
            $routeC_outputScript,
            $routeC_outputType)
    $routeC_settings = [Activator]::CreateInstance(
        $routeC_types.Settings,
        @($RequestedIntervalMs))
    $routeC_kcp = [Activator]::CreateInstance(
        $routeC_types.KcpSession,
        @($Welcome.Conversation, $routeC_settings, $routeC_output))
    $routeC_clock = [Activator]::CreateInstance($routeC_types.Clock)
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    $routeC_buffer = [byte[]]::new(1200)
    $routeC_nextFrame = 0
    [uint32]$routeC_raw = 0
    [int]$routeC_frame = 0
    while ($routeC_nextFrame -lt $ExpectedFrameCount -and
        $routeC_watch.ElapsedMilliseconds -lt 5000)
    {
        while ($Socket.Poll(0, [Net.Sockets.SelectMode]::SelectRead))
        {
            $routeC_count = $Socket.Receive($routeC_buffer)
            $routeC_reason = [Enum]::ToObject($routeC_types.DropReason, 0)
            $routeC_arguments = @(
                $routeC_buffer,
                $routeC_count,
                $null,
                $routeC_reason)
            if (-not $routeC_types.Codec.GetMethod('TryDecode').Invoke(
                    $null,
                    $routeC_arguments))
            {
                continue
            }
            $routeC_message = $routeC_arguments[2]
            if ($routeC_message.MessageType.ToString() -eq 'KcpData')
            {
                $routeC_kcp.InputDatagram(
                    $routeC_message.Payload,
                    0,
                    $routeC_message.Payload.Length) | Out-Null
            }
        }
        $routeC_nowMs = [uint32]$routeC_clock.Milliseconds
        if ([int32](
            $routeC_nowMs -
            $routeC_kcp.NextUpdateAt($routeC_nowMs)) -ge 0)
        {
            $routeC_kcp.Update(
                $routeC_nowMs,
                [Diagnostics.Stopwatch]::GetTimestamp())
        }
        while ($routeC_kcp.TryReceiveBusinessInput(
            [ref]$routeC_raw,
            [ref]$routeC_frame))
        {
            Assert-RouteCLiveProbe `
                ($routeC_frame -eq $routeC_nextFrame) `
                'reconnect-replay' `
                ("expected replay frame $routeC_nextFrame " +
                 "but received $routeC_frame.")
            Assert-RouteCLiveProbe `
                ($routeC_raw -eq [uint32](1000001 + $routeC_frame)) `
                'reconnect-replay' `
                "replay frame $routeC_frame changed raw."
            $routeC_nextFrame++
        }
        $Socket.Poll(
            1000,
            [Net.Sockets.SelectMode]::SelectRead) | Out-Null
    }
    Assert-RouteCLiveProbe `
        ($routeC_nextFrame -eq $ExpectedFrameCount) `
        'reconnect-replay' `
        ("received $routeC_nextFrame of $ExpectedFrameCount " +
         'recoverable replay frames.')
    return $routeC_nextFrame
}

function Invoke-RouteCLiveResumeScenario
{
    param(
        [string]$AssemblyPath,
        [string]$ScenarioName,
        [int]$RequestedIntervalMs)

    $routeC_fixture = $null
    $routeC_result = $null
    $routeC_serverStopped = $false
    try
    {
        $routeC_fixture = New-RouteCLiveResumeFixture `
            $AssemblyPath `
            $RequestedIntervalMs
        $routeC_types = $routeC_fixture.Types
        if ($ScenarioName -eq 'grace-expired')
        {
            $routeC_rejections = @(
                (Receive-RouteCLiveMessage `
                    $routeC_types `
                    $routeC_fixture.Clients[0] `
                    'ResumeRejected' `
                    7000),
                (Receive-RouteCLiveMessage `
                    $routeC_types `
                    $routeC_fixture.Clients[1] `
                    'ResumeRejected' `
                    1000))
            $routeC_reasons = @(
                $routeC_rejections |
                    ForEach-Object {
                        Get-RouteCResumeRejectedReason $routeC_types $_
                    })
            Assert-RouteCLiveProbe `
                (($routeC_reasons | Sort-Object -Unique) -join ',' -eq
                    'ResumeGraceExpired') `
                'grace-expired' `
                'both peers did not observe ResumeGraceExpired.'
            $routeC_failureKind = [Enum]::Parse(
                $routeC_types.ServerResumeFailureKind,
                'GraceExpired')
            Assert-RouteCLiveProbe `
                ($routeC_fixture.Server.Diagnostics.GetResumeFailureCount(
                    $routeC_failureKind) -gt 0) `
                'grace-expired' `
                'server did not classify GraceExpired.'
            $routeC_result = [ordered]@{
                schema = 'route-c-kcp-live-socket-v1'
                scenario = 'grace-expired'
                status = 'PASS'
                transport = 'kcp-udp'
                endpointChanged = $false
                rejectedPeers = 2
                rejectionReason = 'ResumeGraceExpired'
                diagnosticFailureKind = 'GraceExpired'
                finalStates = Get-RouteCServerSessionStates `
                    $routeC_fixture.Server
            }
        }
        else
        {
            $routeC_historyFrames = if (
                $ScenarioName -eq 'history-expired')
            {
                Send-RouteCLivePeerHistory `
                    $routeC_fixture `
                    300 `
                    $RequestedIntervalMs
            }
            elseif ($ScenarioName -eq 'reconnect')
            {
                Send-RouteCLivePeerHistory `
                    $routeC_fixture `
                    8 `
                    $RequestedIntervalMs
            }
            else
            {
                0
            }
            $routeC_oldEndpoint = [Net.IPEndPoint](
                $routeC_fixture.Clients[0].LocalEndPoint)
            $routeC_reconnectSocket = New-RouteCLiveSocket
            $routeC_reconnectSocket.Connect(
                [Net.IPAddress]::Loopback,
                $routeC_fixture.BoundPort)
            $routeC_newEndpoint = [Net.IPEndPoint](
                $routeC_reconnectSocket.LocalEndPoint)
            Assert-RouteCLiveProbe `
                (-not $routeC_oldEndpoint.Equals($routeC_newEndpoint)) `
                'endpoint-change' `
                ("reconnect socket reused the old UDP Endpoint: " +
                 "old=$routeC_oldEndpoint new=$routeC_newEndpoint.")
            $routeC_reconnectNonce = New-RouteCLiveNonce 224
            $routeC_reconnectPayload = [byte[]]$routeC_types.Codec.GetMethod(
                'EncodeReconnectHello').Invoke(
                    $null,
                    @(
                        $routeC_reconnectNonce,
                        $routeC_fixture.Welcomes[0].ReconnectToken))
            Send-RouteCLiveEnvelope `
                $routeC_types `
                $routeC_reconnectSocket `
                'Hello' `
                $routeC_fixture.WelcomeMessages[0].SessionId `
                $routeC_fixture.WelcomeMessages[0].Generation `
                $routeC_reconnectPayload
            $routeC_reconnectWelcomeMessage = Receive-RouteCLiveMessage `
                $routeC_types `
                $routeC_reconnectSocket `
                'Welcome' `
                3000
            $routeC_reconnectWelcome = ConvertFrom-RouteCLiveWelcome `
                $routeC_types `
                $routeC_reconnectWelcomeMessage
            Assert-RouteCLiveProbe `
                ($routeC_reconnectWelcomeMessage.Generation -eq 2 -and
                 $routeC_reconnectWelcome.ResumeRequired) `
                'reconnect-welcome' `
                'reconnect did not switch generation into Resuming.'
            $routeC_fixture.Clients[0].Dispose()
            $routeC_fixture.Clients[0] = $routeC_reconnectSocket

            $routeC_readyPayload = [byte[]]$routeC_types.Codec.GetMethod(
                'EncodeReady').Invoke(
                    $null,
                    @(-1, 0, -1))
            Send-RouteCLiveEnvelope `
                $routeC_types `
                $routeC_reconnectSocket `
                'Ready' `
                $routeC_reconnectWelcomeMessage.SessionId `
                $routeC_reconnectWelcomeMessage.Generation `
                $routeC_readyPayload
            $routeC_probe = Receive-RouteCLiveMessage `
                $routeC_types `
                $routeC_fixture.Clients[1] `
                'ResumeProbe' `
                3000
            $routeC_attempt = Get-RouteCResumeProbeAttempt `
                $routeC_types `
                $routeC_probe
            $routeC_peerLatest = $routeC_historyFrames - 1
            $routeC_resumeStatePayload = [byte[]](
                $routeC_types.Codec.GetMethod(
                    'EncodeResumeState').Invoke(
                        $null,
                        @(
                            $routeC_attempt,
                            -1,
                            0,
                            $routeC_peerLatest)))
            Send-RouteCLiveEnvelope `
                $routeC_types `
                $routeC_fixture.Clients[1] `
                'ResumeState' `
                $routeC_fixture.WelcomeMessages[1].SessionId `
                $routeC_fixture.WelcomeMessages[1].Generation `
                $routeC_resumeStatePayload

            if ($ScenarioName -eq 'history-expired')
            {
                $routeC_rejections = @(
                    (Receive-RouteCLiveMessage `
                        $routeC_types `
                        $routeC_reconnectSocket `
                        'ResumeRejected' `
                        3000),
                    (Receive-RouteCLiveMessage `
                        $routeC_types `
                        $routeC_fixture.Clients[1] `
                        'ResumeRejected' `
                        3000))
                $routeC_reasons = @(
                    $routeC_rejections |
                        ForEach-Object {
                            Get-RouteCResumeRejectedReason $routeC_types $_
                        })
                Assert-RouteCLiveProbe `
                    (($routeC_reasons | Sort-Object -Unique) -join ',' -eq
                        'UnsafeResume') `
                    'history-expired' `
                    'both peers did not observe UnsafeResume.'
                $routeC_failureKind = [Enum]::Parse(
                    $routeC_types.ServerResumeFailureKind,
                    'RangeCapacityExceeded')
                Assert-RouteCLiveProbe `
                    ($routeC_fixture.Server.Diagnostics.GetResumeFailureCount(
                        $routeC_failureKind) -gt 0) `
                    'history-expired' `
                    'server did not classify RangeCapacityExceeded.'
                $routeC_result = [ordered]@{
                    schema = 'route-c-kcp-live-socket-v1'
                    scenario = 'history-expired'
                    status = 'PASS'
                    transport = 'kcp-udp'
                    endpointChanged = $true
                    retainedGapFrames = $routeC_historyFrames
                    historyCapacity = 256
                    rejectedPeers = 2
                    rejectionReason = 'UnsafeResume'
                    diagnosticFailureKind = 'RangeCapacityExceeded'
                    finalStates = Get-RouteCServerSessionStates `
                        $routeC_fixture.Server
                }
            }
            else
            {
                $routeC_acceptedMessages = @(
                    (Receive-RouteCLiveMessage `
                        $routeC_types `
                        $routeC_reconnectSocket `
                        'ResumeAccepted' `
                        3000),
                    (Receive-RouteCLiveMessage `
                        $routeC_types `
                        $routeC_fixture.Clients[1] `
                        'ResumeAccepted' `
                        3000))
                $routeC_accepted = Get-RouteCResumeAcceptedAttempt `
                    $routeC_types `
                    $routeC_acceptedMessages[0]
                Assert-RouteCLiveProbe `
                    ($routeC_accepted.UploadFrom -gt
                        $routeC_accepted.UploadThrough -and
                     $routeC_accepted.ReplayFrom -eq 0 -and
                     $routeC_accepted.ReplayThrough -eq
                        ($routeC_historyFrames - 1) -and
                     $routeC_accepted.PeerFrom -gt
                        $routeC_accepted.PeerThrough) `
                    'reconnect-accepted' `
                    'recoverable reconnect froze unexpected ranges.'
                $routeC_replayedFrames = Receive-RouteCLiveReplay `
                    $routeC_fixture `
                    $routeC_reconnectSocket `
                    $routeC_reconnectWelcomeMessage `
                    $routeC_reconnectWelcome `
                    $routeC_historyFrames `
                    $RequestedIntervalMs
                $routeC_reconnectCompletePayload = [byte[]](
                    $routeC_types.Codec.GetMethod(
                        'EncodeResumeComplete').Invoke(
                            $null,
                            @(
                                $routeC_accepted.Attempt,
                                -1,
                                ($routeC_historyFrames - 1))))
                $routeC_peerCompletePayload = [byte[]](
                    $routeC_types.Codec.GetMethod(
                        'EncodeResumeComplete').Invoke(
                            $null,
                            @(
                                $routeC_accepted.Attempt,
                                ($routeC_historyFrames - 1),
                                -1)))
                Send-RouteCLiveEnvelope `
                    $routeC_types `
                    $routeC_reconnectSocket `
                    'ResumeComplete' `
                    $routeC_reconnectWelcomeMessage.SessionId `
                    $routeC_reconnectWelcomeMessage.Generation `
                    $routeC_reconnectCompletePayload
                Send-RouteCLiveEnvelope `
                    $routeC_types `
                    $routeC_fixture.Clients[1] `
                    'ResumeComplete' `
                    $routeC_fixture.WelcomeMessages[1].SessionId `
                    $routeC_fixture.WelcomeMessages[1].Generation `
                    $routeC_peerCompletePayload
                $null = Receive-RouteCLiveMessage `
                    $routeC_types `
                    $routeC_reconnectSocket `
                    'ResumeComplete' `
                    3000
                $null = Receive-RouteCLiveMessage `
                    $routeC_types `
                    $routeC_fixture.Clients[1] `
                    'ResumeComplete' `
                    3000
                $routeC_states = Get-RouteCServerSessionStates `
                    $routeC_fixture.Server
                Assert-RouteCLiveProbe `
                    (($routeC_states -join ',') -eq 'Running,Running') `
                    'reconnect-complete' `
                    'both server sessions did not return to Running.'
                $routeC_result = [ordered]@{
                    schema = 'route-c-kcp-live-socket-v1'
                    scenario = 'reconnect'
                    status = 'PASS'
                    transport = 'kcp-udp'
                    endpointChanged = $true
                    generationBefore = 1
                    generationAfter = 2
                    recoverableGapFrames = $routeC_historyFrames
                    replayedFrames = $routeC_replayedFrames
                    resumeAccepted = $true
                    resumeCompleteObservedByPeers = 2
                    finalStates = $routeC_states
                }
            }
        }
    }
    finally
    {
        if ($null -ne $routeC_fixture)
        {
            foreach ($routeC_client in $routeC_fixture.Clients)
            {
                if ($null -ne $routeC_client)
                {
                    $routeC_client.Dispose()
                }
            }
            $routeC_fixture.Server.RequestStop()
            $routeC_serverStopped =
                $routeC_fixture.Server.WaitForStop(500)
            $routeC_fixture.Server.Dispose()
        }
    }
    Assert-RouteCLiveProbe `
        $routeC_serverStopped `
        'resume-stop' `
        'resume scenario server did not stop within 500ms.'
    $routeC_result.cleanup = [ordered]@{
        serverStopped = $routeC_serverStopped
        portReleased = Test-RouteCLivePortReleased `
            $routeC_fixture.BoundPort
    }
    Assert-RouteCLiveProbe `
        $routeC_result.cleanup.portReleased `
        'resume-cleanup' `
        'resume scenario port remained bound.'
    return $routeC_result
}

function Test-RouteCLivePortReleased
{
    param([int]$Port)
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
        return $false
    }
    finally
    {
        $routeC_probe.Dispose()
    }
}

function Invoke-RouteCLifecycleSmoke
{
    param([string]$AssemblyPath, [int]$RequestedIntervalMs)

    Assert-RouteCLiveProbe `
        (Test-Path -LiteralPath $AssemblyPath -PathType Leaf) `
        'assembly' `
        "server assembly does not exist: $AssemblyPath"

    $routeC_assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $routeC_types = [pscustomobject]@{
        Server = $routeC_assembly.GetType(
            'FrameSyncServer.KcpUdpRelayServer',
            $true)
        SessionId = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCSessionId',
            $true)
        MessageType = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCMessageType',
            $true)
        DropReason = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCProtocolDropReason',
            $true)
        Codec = $routeC_assembly.GetType(
            'FrameSyncDemo.RouteCProtocolCodec',
            $true)
    }
    $routeC_zeroSession = $routeC_types.SessionId.GetField('Zero').GetValue($null)
    $routeC_server = [Activator]::CreateInstance(
        $routeC_types.Server,
        @($RequestedIntervalMs, 0))
    $routeC_clients = @($null, $null)
    $routeC_boundPort = 0
    $routeC_serverStopped = $false
    try
    {
        $routeC_server.Run()
        $routeC_startWatch = [Diagnostics.Stopwatch]::StartNew()
        while ($routeC_server.BoundPort -le 0 -and
            $routeC_startWatch.ElapsedMilliseconds -lt 2000)
        {
            [Threading.Thread]::Yield() | Out-Null
        }
        Assert-RouteCLiveProbe `
            ($routeC_server.BoundPort -gt 0) `
            'startup' `
            'server worker did not bind within 2000ms.'
        $routeC_boundPort = $routeC_server.BoundPort
        $routeC_endpoint = [Net.IPEndPoint]::new(
            [Net.IPAddress]::Loopback,
            $routeC_boundPort)

        for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
        {
            $routeC_clients[$routeC_player] = New-RouteCLiveSocket
            $routeC_clients[$routeC_player].Connect($routeC_endpoint)
            $routeC_nonce = New-RouteCLiveNonce ([byte](1 + 40 * $routeC_player))
            $routeC_helloPayload = [byte[]]$routeC_types.Codec.GetMethod(
                'EncodeInitialHello').Invoke(
                    $null,
                    @(,$routeC_nonce))
            Send-RouteCLiveEnvelope `
                $routeC_types `
                $routeC_clients[$routeC_player] `
                'Hello' `
                $routeC_zeroSession `
                0 `
                $routeC_helloPayload
        }

        $routeC_welcomes = @(
            (Receive-RouteCLiveMessage `
                $routeC_types $routeC_clients[0] 'Welcome' 3000),
            (Receive-RouteCLiveMessage `
                $routeC_types $routeC_clients[1] 'Welcome' 3000))
        $routeC_players = @(
            (Get-RouteCLiveWelcomePlayer $routeC_types $routeC_welcomes[0]),
            (Get-RouteCLiveWelcomePlayer $routeC_types $routeC_welcomes[1]))
        Assert-RouteCLiveProbe `
            (($routeC_players | Sort-Object) -join ',' -eq '0,1') `
            'welcome' `
            'server did not assign distinct player indices.'

        $routeC_readyPayload = [byte[]]$routeC_types.Codec.GetMethod(
            'EncodeReady').Invoke(
                $null,
                @(-1, 0, -1))
        for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
        {
            Send-RouteCLiveEnvelope `
                $routeC_types `
                $routeC_clients[$routeC_player] `
                'Ready' `
                $routeC_welcomes[$routeC_player].SessionId `
                $routeC_welcomes[$routeC_player].Generation `
                $routeC_readyPayload
        }
        $null = Receive-RouteCLiveMessage `
            $routeC_types $routeC_clients[0] 'Start' 3000
        $null = Receive-RouteCLiveMessage `
            $routeC_types $routeC_clients[1] 'Start' 3000

        return [ordered]@{
            schema = 'route-c-kcp-live-socket-v1'
            scenario = 'lifecycle-smoke'
            status = 'PASS'
            requestedIntervalMs = $RequestedIntervalMs
            boundPort = $routeC_boundPort
            players = @($routeC_players | Sort-Object)
            serverStopped = $false
            portReleased = $false
        }
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
        $routeC_server.RequestStop()
        $routeC_serverStopped = $routeC_server.WaitForStop(500)
        Assert-RouteCLiveProbe `
            $routeC_serverStopped `
            'stop' `
            'server worker did not stop within 500ms.'
        $routeC_server.Dispose()
    }
}

if ($ClientWorker)
{
    Invoke-RouteCLiveClientWorker `
        $ServerPath `
        $ServerPort `
        $IntervalMs `
        $FramesPerClient `
        $SendIntervalMs `
        $Seed `
        $ClientOrdinal `
        $DropPercent `
        $SharedSendTimes
    return
}

if ($ContractOnly)
{
    Get-RouteCLiveProbeContract | ConvertTo-Json -Depth 6
    return
}

if ($LifecycleSmoke)
{
    $routeC_smoke = Invoke-RouteCLifecycleSmoke $ServerPath $IntervalMs
    $routeC_smoke.serverStopped = $true
    $routeC_smoke.portReleased = Test-RouteCLivePortReleased `
        $routeC_smoke.boundPort
    Write-RouteCLiveResult $routeC_smoke $SummaryPath
    return
}

if ($Scenario -eq 'clean')
{
    $routeC_result = Invoke-RouteCCleanLiveRun `
        $ServerPath `
        $IntervalMs `
        $FramesPerClient `
        $SendIntervalMs `
        $Seed `
        $DropPercent `
        $EvidencePath
    Write-RouteCLiveResult $routeC_result $SummaryPath
    return
}

$routeC_resumeResult = Invoke-RouteCLiveResumeScenario `
    $ServerPath `
    $Scenario `
    $IntervalMs
Write-RouteCLiveResult $routeC_resumeResult $SummaryPath
