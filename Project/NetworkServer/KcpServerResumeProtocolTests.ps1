param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath,
    [switch]$ServerOnly
)

$ErrorActionPreference = 'Stop'

function Assert-ResumeTrue
{
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-ResumeEqual
{
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual)
    {
        throw "$Message Expected=$Expected Actual=$Actual"
    }
}

function New-ResumeBytes
{
    param([byte]$Start, [int]$Count)
    $routeC_bytes = [byte[]]::new($Count)
    for ($routeC_index = 0; $routeC_index -lt $Count; $routeC_index++)
    {
        $routeC_bytes[$routeC_index] = [byte]($Start + $routeC_index)
    }
    return ,$routeC_bytes
}

function New-ResumeFactory
{
    param([Type]$ReturnType, [object[]]$Values)
    $routeC_queue = [Collections.Queue]::new()
    foreach ($routeC_value in $Values) { $routeC_queue.Enqueue($routeC_value) }
    $routeC_delegateType = [Func``1].MakeGenericType(@($ReturnType))
    $routeC_script = { return $routeC_queue.Dequeue() }.GetNewClosure()
    return [Management.Automation.LanguagePrimitives]::ConvertTo(
        $routeC_script,
        $routeC_delegateType)
}

function New-ResumeMessage
{
    param($Types, [string]$TypeName, $Session, [byte[]]$Payload)
    $routeC_type = [Enum]::Parse($Types.MessageType, $TypeName)
    return [Activator]::CreateInstance(
        $Types.Message,
        @($routeC_type, $Session.SessionId, $Session.Generation, $Payload))
}

function Decode-ResumeEnvelope
{
    param($Types, [byte[]]$Datagram)
    $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
    $routeC_arguments = @($Datagram, $Datagram.Length, $null, $routeC_reason)
    Assert-ResumeTrue $Types.Codec.GetMethod('TryDecode').Invoke(
        $null,
        $routeC_arguments) `
        'ASSERT resume-envelope: invalid Route C datagram.'
    return $routeC_arguments[2]
}

function Get-KcpBusinessFrames
{
    param(
        $Types,
        $Router,
        $Session,
        [object[]]$Actions,
        [uint32]$StartMs,
        [int]$ExpectedCount)
    $routeC_ackPayloads = [Collections.ArrayList]::new()
    $routeC_output = [Action[byte[], int]]{
        param([byte[]]$Bytes, [int]$Count)
        $routeC_copy = [byte[]]::new($Count)
        [Buffer]::BlockCopy($Bytes, 0, $routeC_copy, 0, $Count)
        [void]$routeC_ackPayloads.Add($routeC_copy)
    }.GetNewClosure()
    $routeC_settings = [Activator]::CreateInstance(
        $Types.KcpSettings,
        @(10))
    $routeC_receiver = [Activator]::CreateInstance(
        $Types.KcpSession,
        @($Session.Conversation, $routeC_settings, $routeC_output))
    $routeC_frames = [Collections.Generic.List[int]]::new()
    $routeC_currentActions = @($Actions)
    for ($routeC_round = 0;
         $routeC_round -lt 128 -and $routeC_frames.Count -lt $ExpectedCount;
         $routeC_round++)
    {
        foreach ($routeC_action in $routeC_currentActions)
        {
            if (-not $routeC_action.Endpoint.Equals($Session.Endpoint))
                { continue }
            $routeC_message = Decode-ResumeEnvelope $Types $routeC_action.Datagram
            if ($routeC_message.MessageType.ToString() -ne 'KcpData')
                { continue }
            Assert-ResumeEqual 0 $routeC_receiver.InputDatagram(
                $routeC_message.Payload,
                0,
                $routeC_message.Payload.Length) `
                'ASSERT kcp-input: server emitted invalid KCP data.'
        }

        [uint32]$routeC_raw = 0
        [int]$routeC_frame = 0
        while ($routeC_receiver.TryReceiveBusinessInput(
            [ref]$routeC_raw,
            [ref]$routeC_frame))
        {
            $routeC_frames.Add($routeC_frame)
        }
        if ($routeC_frames.Count -ge $ExpectedCount)
            { break }

        [uint32]$routeC_now = $StartMs + [uint32](($routeC_round + 1) * 10)
        $routeC_receiver.Update(
            $routeC_now,
            [Diagnostics.Stopwatch]::GetTimestamp())
        foreach ($routeC_ackPayload in @($routeC_ackPayloads))
        {
            $routeC_ackMessage = [Activator]::CreateInstance(
                $Types.Message,
                @(
                    ([Enum]::Parse($Types.MessageType, 'KcpData')),
                    $Session.SessionId,
                    $Session.Generation,
                    [byte[]]$routeC_ackPayload))
            Assert-ResumeTrue $Router.HandleKcpData(
                $routeC_ackMessage,
                $Session.Endpoint,
                $routeC_now) `
                'ASSERT kcp-ack: server rejected receiver ACK.'
        }
        $routeC_ackPayloads.Clear()
        $Router.Tick(
            $routeC_now,
            [Diagnostics.Stopwatch]::GetTimestamp())
        $routeC_currentActions = @($Types.Router.GetMethod(
            'DrainActions').Invoke(
                $Router,
                @()))
    }
    return $routeC_frames.ToArray()
}

function New-ResumeScenario
{
    param(
        $Types,
        [int]$IdentityBase,
        [byte[]]$AttemptID,
        [int]$ReconnectRemote,
        [int]$ReconnectFloor,
        [int]$ReconnectLocal,
        [int]$PeerRemote,
        [int]$PeerFloor,
        [int]$PeerLocal,
        [int]$PeerHistoryFrom,
        [int]$PeerHistoryThrough,
        [bool]$SubmitResumeReady = $true)

    $routeC_id0 = [Activator]::CreateInstance(
        $Types.SessionId,
        @([uint64]($IdentityBase + 1), [uint64]($IdentityBase + 2)))
    $routeC_id1 = [Activator]::CreateInstance(
        $Types.SessionId,
        @([uint64]($IdentityBase + 3), [uint64]($IdentityBase + 4)))
    $routeC_token0 = New-ResumeBytes 3 32
    $routeC_token1 = New-ResumeBytes 43 32
    $routeC_token2 = New-ResumeBytes 83 32
    $routeC_token3 = New-ResumeBytes 123 32
    $routeC_diagnostics = [Activator]::CreateInstance($Types.Diagnostics)
    $routeC_router = [Activator]::CreateInstance(
        $Types.Router,
        @(
            $routeC_diagnostics,
            (New-ResumeFactory $Types.SessionId @($routeC_id0, $routeC_id1)),
            (New-ResumeFactory ([byte[]]) @($routeC_token0, $routeC_token1, $routeC_token2, $routeC_token3)),
            (New-ResumeFactory ([uint32]) @([uint32]601, [uint32]602, [uint32]603, [uint32]604)),
            (New-ResumeFactory ([byte[]]) @(,$AttemptID)),
            10))
    $routeC_endpoint0 = [Net.IPEndPoint]::new(
        [Net.IPAddress]::Loopback,
        17000 + ($IdentityBase % 1000))
    $routeC_endpoint1 = [Net.IPEndPoint]::new(
        [Net.IPAddress]::Loopback,
        18000 + ($IdentityBase % 1000))
    $routeC_reconnectEndpoint = [Net.IPEndPoint]::new(
        [Net.IPAddress]::Loopback,
        19000 + ($IdentityBase % 1000))
    $routeC_hello = $Types.Router.GetMethod('HandleInitialHelloAt')
    $routeC_hello0Arguments = @($routeC_endpoint0, (New-ResumeBytes 13 16), [uint32]0, $null, $null)
    $routeC_hello1Arguments = @($routeC_endpoint1, (New-ResumeBytes 33 16), [uint32]0, $null, $null)
    $routeC_hello.Invoke($routeC_router, $routeC_hello0Arguments) | Out-Null
    $routeC_hello.Invoke($routeC_router, $routeC_hello1Arguments) | Out-Null
    $routeC_session0 = $routeC_hello0Arguments[4]
    $routeC_session1 = $routeC_hello1Arguments[4]
    $routeC_initialReady = [byte[]]$Types.Codec.GetMethod('EncodeReady').Invoke(
        $null,
        @(-1, 0, -1))
    $routeC_handleReady = $Types.Router.GetMethod('HandleReady')
    $routeC_handleReady.Invoke(
        $routeC_router,
        @((New-ResumeMessage $Types 'Ready' $routeC_session0 $routeC_initialReady), $routeC_endpoint0, [uint32]0)) | Out-Null
    $routeC_handleReady.Invoke(
        $routeC_router,
        @((New-ResumeMessage $Types 'Ready' $routeC_session1 $routeC_initialReady), $routeC_endpoint1, [uint32]0)) | Out-Null
    $Types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()) | Out-Null

    if ($PeerHistoryThrough -ge $PeerHistoryFrom)
    {
        for ($routeC_frame = $PeerHistoryFrom; $routeC_frame -le $PeerHistoryThrough; $routeC_frame++)
        {
            $routeC_session1.History.Record(
                $routeC_frame,
                [uint32](3000 + $routeC_frame)) | Out-Null
        }
    }

    $routeC_reconnectArguments = @(
        $routeC_reconnectEndpoint,
        $routeC_session0.SessionId,
        [uint32]1,
        $routeC_token0,
        (New-ResumeBytes 63 16),
        [uint32]100,
        $null,
        $null)
    $Types.Router.GetMethod('HandleReconnectHelloAt').Invoke(
        $routeC_router,
        $routeC_reconnectArguments) | Out-Null
    $routeC_session0 = $routeC_reconnectArguments[7]
    $routeC_resumeReady = [byte[]]$Types.Codec.GetMethod('EncodeReady').Invoke(
        $null,
        @($ReconnectRemote, $ReconnectFloor, $ReconnectLocal))
    if ($SubmitResumeReady)
    {
        $routeC_handleReady.Invoke(
            $routeC_router,
            @((New-ResumeMessage $Types 'Ready' $routeC_session0 $routeC_resumeReady), $routeC_reconnectEndpoint, [uint32]110)) | Out-Null
        $Types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()) | Out-Null
    }
    $routeC_resumeStatePayload = [byte[]]$Types.Codec.GetMethod('EncodeResumeState').Invoke(
        $null,
        @($AttemptID, $PeerRemote, $PeerFloor, $PeerLocal))
    $routeC_resumeState = New-ResumeMessage $Types 'ResumeState' $routeC_session1 $routeC_resumeStatePayload

    return [pscustomobject]@{
        Router = $routeC_router
        Diagnostics = $routeC_diagnostics
        Session0 = $routeC_session0
        Session1 = $routeC_session1
        Endpoint0 = $routeC_reconnectEndpoint
        Endpoint1 = $routeC_endpoint1
        ResumeState = $routeC_resumeState
        AttemptID = $AttemptID
        CurrentToken = $routeC_token2
    }
}

function Assert-ResumeRejectedPair
{
    param($Types, $Scenario, [uint16]$ExpectedReason)
    $routeC_actions = @($Types.Router.GetMethod('DrainActions').Invoke(
        $Scenario.Router,
        @()))
    Assert-ResumeEqual 2 $routeC_actions.Count `
        'ASSERT rejected-pair: terminal rejection was not sent exactly once per endpoint.'
    foreach ($routeC_action in $routeC_actions)
    {
        $routeC_message = Decode-ResumeEnvelope $Types $routeC_action.Datagram
        Assert-ResumeEqual 'ResumeRejected' $routeC_message.MessageType.ToString() `
            'ASSERT rejected-type: terminal control changed.'
        $routeC_arguments = @($routeC_message.Payload, $null, [uint16]0)
        Assert-ResumeTrue $Types.Codec.GetMethod('TryDecodeResumeRejected').Invoke(
            $null,
            $routeC_arguments) `
            'ASSERT rejected-payload: payload did not decode.'
        Assert-ResumeTrue ([Linq.Enumerable]::SequenceEqual(
            [byte[]]$Scenario.AttemptID,
            [byte[]]$routeC_arguments[1])) `
            'ASSERT rejected-attempt: active AttemptID was not preserved.'
        Assert-ResumeEqual $ExpectedReason $routeC_arguments[2] `
            'ASSERT rejected-reason: wire reason changed.'
    }
    Assert-ResumeEqual 'Terminated' $Scenario.Session0.State.ToString() `
        'ASSERT rejected-player0: session did not terminate.'
    Assert-ResumeEqual 'Terminated' $Scenario.Session1.State.ToString() `
        'ASSERT rejected-player1: session did not terminate.'
}

function Assert-ResumeFailureCount
{
    param($Scenario, [string]$FailureKind, [int]$ExpectedCount)
    $routeC_failureType = $routeC_assembly.GetType(
        'FrameSyncServer.ServerResumeFailureKind',
        $true)
    $routeC_failure = [Enum]::Parse($routeC_failureType, $FailureKind)
    $routeC_actual = $Scenario.Diagnostics.GetType().GetMethod(
        'GetResumeFailureCount').Invoke(
            $Scenario.Diagnostics,
            @($routeC_failure))
    Assert-ResumeEqual $ExpectedCount $routeC_actual `
        "ASSERT failure-count-${FailureKind}: internal failure reason changed."
}

if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf))
{
    throw "Server assembly does not exist: $ServerPath"
}

$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_historyType = $routeC_assembly.GetType(
    'FrameSyncServer.ServerInputHistory',
    $true)
$routeC_dispositionType = $routeC_assembly.GetType(
    'FrameSyncDemo.OutboundHistoryDisposition',
    $true)

$routeC_player0 = [Activator]::CreateInstance($routeC_historyType, @(4))
$routeC_player1 = [Activator]::CreateInstance($routeC_historyType, @(4))

Assert-ResumeEqual 'Accepted' $routeC_player0.Record(7, [uint32]0x70).ToString() `
    'ASSERT player0-frame7: first raw was not accepted.'
Assert-ResumeEqual 'Accepted' $routeC_player1.Record(7, [uint32]0x170).ToString() `
    'ASSERT player1-frame7: PlayerIndex + FrameID keys were not independent.'
Assert-ResumeEqual 'IdempotentDuplicate' $routeC_player0.Record(7, [uint32]0x70).ToString() `
    'ASSERT idempotent: same player/frame/raw was not idempotent.'
Assert-ResumeEqual 'ConflictingDuplicate' $routeC_player0.Record(7, [uint32]0x71).ToString() `
    'ASSERT conflict: same player/frame with different raw was not rejected.'
$routeC_retainedArguments = @(7, [uint32]0)
Assert-ResumeTrue $routeC_historyType.GetMethod('TryGet').Invoke(
    $routeC_player0,
    $routeC_retainedArguments) `
    'ASSERT conflict-retention: first raw disappeared.'
Assert-ResumeEqual ([uint32]0x70) $routeC_retainedArguments[1] `
    'ASSERT conflict-retention: conflicting raw overwrote the first raw.'

for ($routeC_frame = 8; $routeC_frame -le 11; $routeC_frame++)
{
    Assert-ResumeEqual 'Accepted' $routeC_player0.Record(
        $routeC_frame,
        [uint32]$routeC_frame).ToString() `
        "ASSERT normal-roll-${routeC_frame}: normal rolling was treated as capacity exhaustion."
}
Assert-ResumeEqual 'HistoryUnavailable' $routeC_player0.Record(7, [uint32]0x70).ToString() `
    'ASSERT unavailable: evicted history was not reported unavailable.'

Assert-ResumeTrue ([Enum]::IsDefined($routeC_dispositionType, 'CapacityExceeded')) `
    'RED: CapacityExceeded disposition is missing.'
$routeC_tryBeginFreeze = $routeC_historyType.GetMethod('TryBeginFreeze')
Assert-ResumeTrue ($null -ne $routeC_tryBeginFreeze) `
    'RED: ServerInputHistory.TryBeginFreeze is missing.'

$routeC_frozen = [Activator]::CreateInstance($routeC_historyType, @(4))
Assert-ResumeEqual 'Accepted' $routeC_frozen.Record(7, [uint32]0x70).ToString() `
    'ASSERT frozen-setup: base frame was not accepted.'
Assert-ResumeTrue $routeC_tryBeginFreeze.Invoke($routeC_frozen, @(7)) `
    'ASSERT freeze: valid freeze boundary was rejected.'
for ($routeC_frame = 8; $routeC_frame -le 11; $routeC_frame++)
{
    Assert-ResumeEqual 'Accepted' $routeC_frozen.Record(
        $routeC_frame,
        [uint32]$routeC_frame).ToString() `
        "ASSERT live-tail-${routeC_frame}: bounded live-tail frame was rejected."
}
Assert-ResumeEqual 'CapacityExceeded' $routeC_frozen.Record(12, [uint32]0x12).ToString() `
    'ASSERT live-tail-capacity: 257-equivalent frame did not report capacity exhaustion.'
$routeC_releaseTail = $routeC_historyType.GetMethod('TryReleaseLiveTail')
Assert-ResumeTrue ($null -ne $routeC_releaseTail) `
    'RED: live tail cannot be released atomically in FrameID order.'
$routeC_releaseArguments = @($null)
Assert-ResumeTrue $routeC_releaseTail.Invoke(
    $routeC_frozen,
    $routeC_releaseArguments) `
    'ASSERT live-tail-release: frozen tail did not release.'
$routeC_releasedPayloads = [byte[][]]$routeC_releaseArguments[0]
Assert-ResumeEqual 4 $routeC_releasedPayloads.Length `
    'ASSERT live-tail-release-count: tail did not release exactly once.'
$routeC_historyCodecType = $routeC_assembly.GetType(
    'FrameSyncDemo.RouteCProtocolCodec',
    $true)
for ($routeC_offset = 0; $routeC_offset -lt 4; $routeC_offset++)
{
    $routeC_businessArguments = @(
        $routeC_releasedPayloads[$routeC_offset],
        [uint32]0,
        0)
    Assert-ResumeTrue $routeC_historyCodecType.GetMethod(
        'TryDecodeBusinessInput').Invoke(
            $null,
            $routeC_businessArguments) `
        "ASSERT live-tail-release-${routeC_offset}: payload did not decode."
    Assert-ResumeEqual (8 + $routeC_offset) $routeC_businessArguments[2] `
        "ASSERT live-tail-order-${routeC_offset}: tail order changed."
}
$routeC_secondReleaseArguments = @($null)
Assert-ResumeTrue (-not $routeC_releaseTail.Invoke(
    $routeC_frozen,
    $routeC_secondReleaseArguments)) `
    'ASSERT live-tail-release-idempotence: tail released twice.'
Assert-ResumeEqual 'Accepted' $routeC_frozen.Record(12, [uint32]0x12).ToString() `
    'ASSERT live-tail-unfreeze: normal rolling did not resume after release.'

$routeC_preFreezeTail = [Activator]::CreateInstance($routeC_historyType, @(4))
$routeC_preFreezeTail.Record(7, [uint32]0x70) | Out-Null
$routeC_preFreezeTail.Record(9, [uint32]0x90) | Out-Null
$routeC_preFreezeTail.Record(8, [uint32]0x80) | Out-Null
Assert-ResumeTrue $routeC_tryBeginFreeze.Invoke($routeC_preFreezeTail, @(7)) `
    'ASSERT pre-freeze-tail-freeze: valid boundary was rejected.'
$routeC_preFreezeReleaseArguments = @($null)
Assert-ResumeTrue $routeC_releaseTail.Invoke(
    $routeC_preFreezeTail,
    $routeC_preFreezeReleaseArguments) `
    'ASSERT pre-freeze-tail-release: tail did not release.'
$routeC_preFreezePayloads = [byte[][]]$routeC_preFreezeReleaseArguments[0]
Assert-ResumeEqual 2 $routeC_preFreezePayloads.Length `
    'RED: inputs received before final freeze were lost instead of migrated to live tail.'
for ($routeC_offset = 0; $routeC_offset -lt 2; $routeC_offset++)
{
    $routeC_businessArguments = @(
        $routeC_preFreezePayloads[$routeC_offset],
        [uint32]0,
        0)
    Assert-ResumeTrue $routeC_historyCodecType.GetMethod(
        'TryDecodeBusinessInput').Invoke(
            $null,
            $routeC_businessArguments) `
        "ASSERT pre-freeze-tail-${routeC_offset}: payload did not decode."
    Assert-ResumeEqual (8 + $routeC_offset) $routeC_businessArguments[2] `
        "ASSERT pre-freeze-tail-order-${routeC_offset}: migration order changed."
}

Write-Output 'PASS: Task 9 Slice 1 server history keys, dispositions, freeze, and capacity semantics are correct.'

$routeC_types = [pscustomobject]@{
    Router = $routeC_assembly.GetType('FrameSyncServer.ServerSessionRouter', $true)
    Diagnostics = $routeC_assembly.GetType('FrameSyncServer.KcpServerDiagnostics', $true)
    SessionId = $routeC_assembly.GetType('FrameSyncDemo.RouteCSessionId', $true)
    Message = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolMessage', $true)
    MessageType = $routeC_assembly.GetType('FrameSyncDemo.RouteCMessageType', $true)
    DropReason = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolDropReason', $true)
    Codec = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolCodec', $true)
    KcpSession = $routeC_assembly.GetType('FrameSyncDemo.RouteCKcpSession', $true)
    KcpSettings = $routeC_assembly.GetType('FrameSyncDemo.RouteCKcpSettings', $true)
}
$routeC_handleResumeState = $routeC_types.Router.GetMethod('HandleResumeState')
Assert-ResumeTrue ($null -ne $routeC_handleResumeState) `
    'RED: ServerSessionRouter.HandleResumeState is missing.'

$routeC_sessionId0 = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]1001, [uint64]1002))
$routeC_sessionId1 = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]2001, [uint64]2002))
$routeC_token0 = New-ResumeBytes 1 32
$routeC_token1 = New-ResumeBytes 41 32
$routeC_token2 = New-ResumeBytes 81 32
$routeC_attemptID = New-ResumeBytes 121 16
$routeC_diagnostics = [Activator]::CreateInstance($routeC_types.Diagnostics)
$routeC_router = [Activator]::CreateInstance(
    $routeC_types.Router,
    @(
        $routeC_diagnostics,
        (New-ResumeFactory $routeC_types.SessionId @($routeC_sessionId0, $routeC_sessionId1)),
        (New-ResumeFactory ([byte[]]) @($routeC_token0, $routeC_token1, $routeC_token2)),
        (New-ResumeFactory ([uint32]) @([uint32]501, [uint32]502, [uint32]503)),
        (New-ResumeFactory ([byte[]]) @(,$routeC_attemptID)),
        10))
$routeC_endpoint0 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 16001)
$routeC_endpoint1 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 16002)
$routeC_reconnectEndpoint0 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 16003)
$routeC_hello = $routeC_types.Router.GetMethod('HandleInitialHelloAt')
$routeC_hello0Arguments = @($routeC_endpoint0, (New-ResumeBytes 11 16), [uint32]0, $null, $null)
$routeC_hello1Arguments = @($routeC_endpoint1, (New-ResumeBytes 31 16), [uint32]0, $null, $null)
$routeC_hello.Invoke($routeC_router, $routeC_hello0Arguments) | Out-Null
$routeC_hello.Invoke($routeC_router, $routeC_hello1Arguments) | Out-Null
$routeC_session0 = $routeC_hello0Arguments[4]
$routeC_session1 = $routeC_hello1Arguments[4]
$routeC_handleReady = $routeC_types.Router.GetMethod('HandleReady')
$routeC_initialReady = [byte[]]$routeC_types.Codec.GetMethod('EncodeReady').Invoke(
    $null,
    @(-1, 0, -1))
$routeC_handleReady.Invoke(
    $routeC_router,
    @((New-ResumeMessage $routeC_types 'Ready' $routeC_session0 $routeC_initialReady), $routeC_endpoint0, [uint32]0)) | Out-Null
$routeC_handleReady.Invoke(
    $routeC_router,
    @((New-ResumeMessage $routeC_types 'Ready' $routeC_session1 $routeC_initialReady), $routeC_endpoint1, [uint32]0)) | Out-Null
$routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()) | Out-Null
for ($routeC_frame = 100; $routeC_frame -le 115; $routeC_frame++)
{
    Assert-ResumeEqual 'Accepted' $routeC_session0.History.Record(
        $routeC_frame,
        [uint32](1000 + $routeC_frame)).ToString() `
        "ASSERT p0-history-${routeC_frame}: setup failed."
}
for ($routeC_frame = 100; $routeC_frame -le 123; $routeC_frame++)
{
    Assert-ResumeEqual 'Accepted' $routeC_session1.History.Record(
        $routeC_frame,
        [uint32](2000 + $routeC_frame)).ToString() `
        "ASSERT p1-history-${routeC_frame}: setup failed."
}

$routeC_reconnect = $routeC_types.Router.GetMethod('HandleReconnectHelloAt')
$routeC_reconnectArguments = @(
    $routeC_reconnectEndpoint0,
    $routeC_session0.SessionId,
    [uint32]1,
    $routeC_token0,
    (New-ResumeBytes 61 16),
    [uint32]100,
    $null,
    $null)
$routeC_reconnect.Invoke($routeC_router, $routeC_reconnectArguments) | Out-Null
$routeC_session0 = $routeC_reconnectArguments[7]
$routeC_preReadyInputHandler = $routeC_types.Router.GetMethod(
    'HandleDecodedBusinessInput',
    [Reflection.BindingFlags]'Instance,NonPublic')
Assert-ResumeTrue $routeC_preReadyInputHandler.Invoke(
    $routeC_router,
    @($routeC_session0, [uint32]1121, 121)) `
    'ASSERT pre-ready-input: new-generation input was rejected.'
$routeC_router.Tick([uint32]105, [Diagnostics.Stopwatch]::GetTimestamp())
$routeC_preReadyActions = @($routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_router,
    @()))
$routeC_preReadyRelayCount = 0
foreach ($routeC_action in $routeC_preReadyActions)
{
    if ($routeC_action.Endpoint.Equals($routeC_endpoint1) -and
        (Decode-ResumeEnvelope $routeC_types $routeC_action.Datagram).
            MessageType.ToString() -eq 'KcpData')
    {
        $routeC_preReadyRelayCount++
    }
}
Assert-ResumeEqual 0 $routeC_preReadyRelayCount `
    'RED: Resuming sender relayed live input before boundary validation.'
$routeC_resumeReady = [byte[]]$routeC_types.Codec.GetMethod('EncodeReady').Invoke(
    $null,
    @(110, 100, 120))
Assert-ResumeTrue $routeC_handleReady.Invoke(
    $routeC_router,
    @((New-ResumeMessage $routeC_types 'Ready' $routeC_session0 $routeC_resumeReady), $routeC_reconnectEndpoint0, [uint32]110)) `
    'ASSERT resume-ready: reconnecting Ready was rejected.'
$routeC_probeActions = @($routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()))
Assert-ResumeEqual 1 $routeC_probeActions.Count `
    'ASSERT resume-probe-count: online peer did not receive exactly one probe.'
Assert-ResumeTrue $routeC_probeActions[0].Endpoint.Equals($routeC_endpoint1) `
    'ASSERT resume-probe-endpoint: probe targeted the wrong peer.'
$routeC_probe = Decode-ResumeEnvelope $routeC_types $routeC_probeActions[0].Datagram
Assert-ResumeEqual 'ResumeProbe' $routeC_probe.MessageType.ToString() `
    'ASSERT resume-probe-type: wrong control was emitted.'
$routeC_probeArguments = @($routeC_probe.Payload, $null)
Assert-ResumeTrue $routeC_types.Codec.GetMethod('TryDecodeResumeProbe').Invoke(
    $null,
    $routeC_probeArguments) `
    'ASSERT resume-probe-payload: probe did not decode.'
Assert-ResumeTrue ([Linq.Enumerable]::SequenceEqual(
    [byte[]]$routeC_attemptID,
    [byte[]]$routeC_probeArguments[1])) `
    'ASSERT resume-attempt: server did not use the injected 16-byte AttemptID.'
Assert-ResumeEqual 'Resuming' $routeC_session1.State.ToString() `
    'ASSERT online-state: online peer did not enter Resuming.'

Assert-ResumeTrue $routeC_handleReady.Invoke(
    $routeC_router,
    @((New-ResumeMessage $routeC_types 'Ready' $routeC_session0 $routeC_resumeReady), $routeC_reconnectEndpoint0, [uint32]111)) `
    'ASSERT duplicate-ready: matching retry was rejected.'
$routeC_retryProbeActions = @($routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()))
Assert-ResumeEqual 1 $routeC_retryProbeActions.Count `
    'ASSERT duplicate-ready-probe: retry did not resend one stable probe.'
Assert-ResumeTrue ([Linq.Enumerable]::SequenceEqual(
    [byte[]]$routeC_probeActions[0].Datagram,
    [byte[]]$routeC_retryProbeActions[0].Datagram)) `
    'ASSERT duplicate-ready-bytes: retry changed the frozen AttemptID or envelope.'

$routeC_heartbeat = New-ResumeMessage $routeC_types 'Heartbeat' $routeC_session1 ([byte[]]::new(0))
Assert-ResumeTrue $routeC_types.Router.GetMethod('HandleHeartbeat').Invoke(
    $routeC_router,
    @($routeC_heartbeat, $routeC_endpoint1, [uint32]120)) `
    'ASSERT resume-heartbeat: valid heartbeat was rejected.'
Assert-ResumeEqual 0 @($routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @())).Count `
    'ASSERT resume-heartbeat: heartbeat incorrectly satisfied ResumeProbe.'

$routeC_resumeStatePayload = [byte[]]$routeC_types.Codec.GetMethod('EncodeResumeState').Invoke(
    $null,
    @($routeC_attemptID, 115, 100, 123))
$routeC_validResumeState = New-ResumeMessage $routeC_types 'ResumeState' $routeC_session1 $routeC_resumeStatePayload
$routeC_resumeStateType = [Enum]::Parse($routeC_types.MessageType, 'ResumeState')
$routeC_unknownSession = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]9001, [uint64]9002))
$routeC_wrongSessionMessage = [Activator]::CreateInstance(
    $routeC_types.Message,
    @($routeC_resumeStateType, $routeC_unknownSession, [uint32]1, $routeC_resumeStatePayload))
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_router,
    @($routeC_wrongSessionMessage, $routeC_endpoint1, [uint32]121))) `
    'ASSERT wrong-session: invalid ResumeState was accepted.'
$routeC_wrongGenerationMessage = [Activator]::CreateInstance(
    $routeC_types.Message,
    @($routeC_resumeStateType, $routeC_session1.SessionId, [uint32]2, $routeC_resumeStatePayload))
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_router,
    @($routeC_wrongGenerationMessage, $routeC_endpoint1, [uint32]121))) `
    'ASSERT wrong-generation: invalid ResumeState was accepted.'
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_router,
    @($routeC_validResumeState, $routeC_endpoint0, [uint32]121))) `
    'ASSERT wrong-endpoint: invalid ResumeState was accepted.'
$routeC_wrongAttemptPayload = [byte[]]$routeC_types.Codec.GetMethod('EncodeResumeState').Invoke(
    $null,
    @((New-ResumeBytes 141 16), 115, 100, 123))
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_router,
    @((New-ResumeMessage $routeC_types 'ResumeState' $routeC_session1 $routeC_wrongAttemptPayload), $routeC_endpoint1, [uint32]121))) `
    'ASSERT wrong-attempt: invalid ResumeState was accepted.'
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_router,
    @((New-ResumeMessage $routeC_types 'ResumeState' $routeC_session0 $routeC_resumeStatePayload), $routeC_reconnectEndpoint0, [uint32]121))) `
    'ASSERT wrong-direction: reconnecting player answered its own probe.'
Assert-ResumeTrue $routeC_handleResumeState.Invoke(
    $routeC_router,
    @($routeC_validResumeState, $routeC_endpoint1, [uint32]121)) `
    'ASSERT resume-state: matching online state was rejected.'
$routeC_acceptedActions = @($routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()))
Assert-ResumeEqual 2 $routeC_acceptedActions.Count `
    'RED: matching boundaries did not emit ResumeAccepted to both endpoints.'
$routeC_acceptedPayload = $null
foreach ($routeC_action in $routeC_acceptedActions)
{
    $routeC_acceptedMessage = Decode-ResumeEnvelope $routeC_types $routeC_action.Datagram
    Assert-ResumeEqual 'ResumeAccepted' $routeC_acceptedMessage.MessageType.ToString() `
        'ASSERT accepted-type: wrong control was emitted.'
    if ($null -eq $routeC_acceptedPayload)
    {
        $routeC_acceptedPayload = [byte[]]$routeC_acceptedMessage.Payload
    }
    else
    {
        Assert-ResumeTrue ([Linq.Enumerable]::SequenceEqual(
            [byte[]]$routeC_acceptedPayload,
            [byte[]]$routeC_acceptedMessage.Payload)) `
            'ASSERT accepted-payload-stability: peers received different frozen ranges.'
    }
    $routeC_acceptedArguments = @(
        $routeC_acceptedMessage.Payload,
        $null,
        0,
        0,
        0,
        0,
        0,
        0)
    Assert-ResumeTrue $routeC_types.Codec.GetMethod('TryDecodeResumeAccepted').Invoke(
        $null,
        $routeC_acceptedArguments) `
        'ASSERT accepted-payload: payload did not decode.'
    Assert-ResumeTrue ([Linq.Enumerable]::SequenceEqual(
        [byte[]]$routeC_attemptID,
        [byte[]]$routeC_acceptedArguments[1])) `
        'ASSERT accepted-attempt: AttemptID changed.'
    Assert-ResumeEqual 116 $routeC_acceptedArguments[2] 'ASSERT upload-from: wrong range.'
    Assert-ResumeEqual 120 $routeC_acceptedArguments[3] 'ASSERT upload-through: wrong range.'
    Assert-ResumeEqual 111 $routeC_acceptedArguments[4] 'ASSERT replay-from: wrong range.'
    Assert-ResumeEqual 123 $routeC_acceptedArguments[5] 'ASSERT replay-through: wrong range.'
    Assert-ResumeEqual 116 $routeC_acceptedArguments[6] 'ASSERT peer-from: wrong range.'
    Assert-ResumeEqual 120 $routeC_acceptedArguments[7] 'ASSERT peer-through: wrong range.'
}

Assert-ResumeEqual 'Accepted' $routeC_session1.History.Record(124, [uint32]2124).ToString() `
    'ASSERT live-tail-124: post-freeze input was not accepted.'
Assert-ResumeTrue $routeC_handleResumeState.Invoke(
    $routeC_router,
    @($routeC_validResumeState, $routeC_endpoint1, [uint32]122)) `
    'ASSERT duplicate-resume-state: matching retry was rejected.'
$routeC_duplicateAcceptedActions = @($routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()))
Assert-ResumeEqual 2 $routeC_duplicateAcceptedActions.Count `
    'ASSERT duplicate-accepted-count: retry did not resend both controls.'
foreach ($routeC_action in $routeC_duplicateAcceptedActions)
{
    $routeC_duplicateAccepted = Decode-ResumeEnvelope $routeC_types $routeC_action.Datagram
    Assert-ResumeTrue ([Linq.Enumerable]::SequenceEqual(
        [byte[]]$routeC_acceptedPayload,
        [byte[]]$routeC_duplicateAccepted.Payload)) `
        'ASSERT frozen-range-growth: frame 124 changed the frozen ResumeAccepted payload.'
}

foreach ($routeC_counter in @(
    'ResumeWrongSessionCount',
    'ResumeWrongGenerationCount',
    'ResumeWrongEndpointCount',
    'ResumeWrongAttemptCount',
    'ResumeWrongDirectionCount'))
{
    $routeC_property = $routeC_diagnostics.GetType().GetProperty($routeC_counter)
    Assert-ResumeTrue ($null -ne $routeC_property) `
        "ASSERT resume-counter-surface: $routeC_counter is missing."
    Assert-ResumeEqual 1 $routeC_property.GetValue($routeC_diagnostics, $null) `
        "ASSERT resume-counter-value: $routeC_counter did not count independently."
}

$routeC_wrongDirectionHandler = $routeC_types.Router.GetMethod(
    'HandleServerOnlyResumeControl')
Assert-ResumeTrue ($null -ne $routeC_wrongDirectionHandler) `
    'RED: client-originated ResumeProbe/ResumeAccepted have no diagnostic route.'
$routeC_wrongDirectionScenario = New-ResumeScenario `
    $routeC_types `
    3900 `
    (New-ResumeBytes 149 16) `
    110 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    123
foreach ($routeC_typeName in @('ResumeProbe', 'ResumeAccepted'))
{
    $routeC_wrongDirectionMessage = New-ResumeMessage `
        $routeC_types `
        $routeC_typeName `
        $routeC_wrongDirectionScenario.Session1 `
        ([byte[]]::new(0))
    Assert-ResumeTrue (-not $routeC_wrongDirectionHandler.Invoke(
        $routeC_wrongDirectionScenario.Router,
        @(
            $routeC_wrongDirectionMessage,
            $routeC_wrongDirectionScenario.Endpoint1))) `
        "ASSERT wrong-direction-${routeC_typeName}: server-only control was accepted."
}
Assert-ResumeEqual 2 $routeC_wrongDirectionScenario.Diagnostics.ResumeWrongDirectionCount `
    'RED: server-only controls did not increment wrong-direction diagnostics.'

Write-Output 'PASS: Task 9 Slice 2 server AttemptID, probe/state boundary, idempotence, and heartbeat isolation are correct.'
Write-Output 'PASS: Task 9 Slice 3 exact frozen ranges and immutable live-tail boundary are correct.'

$routeC_budgetScenario = New-ResumeScenario `
    $routeC_types `
    4700 `
    (New-ResumeBytes 162 16) `
    -1 `
    0 `
    -1 `
    -1 `
    0 `
    255 `
    0 `
    255
Assert-ResumeTrue $routeC_handleResumeState.Invoke(
    $routeC_budgetScenario.Router,
    @(
        $routeC_budgetScenario.ResumeState,
        $routeC_budgetScenario.Endpoint1,
        [uint32]121)) `
    'ASSERT replay-budget-freeze: 256-frame replay plan was rejected.'
$routeC_attemptField = $routeC_types.Router.GetField(
    '_resumeAttempt',
    [Reflection.BindingFlags]'Instance,NonPublic')
$routeC_budgetAttempt = $routeC_attemptField.GetValue(
    $routeC_budgetScenario.Router)
$routeC_pendingReplayProperty = $routeC_budgetAttempt.GetType().GetProperty(
    'PendingReplayCount')
Assert-ResumeTrue ($null -ne $routeC_pendingReplayProperty) `
    'RED: server replay has no progressive pending-work budget seam.'
Assert-ResumeEqual 256 $routeC_budgetAttempt.PendingReplayCount `
    'RED: replay payloads were synchronously queued during ResumeState handling.'
$routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_budgetScenario.Router,
    @()) | Out-Null
$routeC_budgetScenario.Router.Tick(
    [uint32]130,
    [Diagnostics.Stopwatch]::GetTimestamp())
$routeC_remainingReplay = $routeC_budgetAttempt.PendingReplayCount
Assert-ResumeTrue ($routeC_remainingReplay -ge 192 -and
    $routeC_remainingReplay -lt 256) `
    'RED: one server round did not consume between 1 and 64 replay messages.'
$routeC_kcpProperty = $routeC_budgetScenario.Session0.GetType().GetProperty(
    'KcpSession',
    [Reflection.BindingFlags]'Instance,NonPublic')
$routeC_budgetKcp = $routeC_kcpProperty.GetValue(
    $routeC_budgetScenario.Session0)
$routeC_budgetSnapshot = $routeC_budgetKcp.SnapshotDiagnostics()
Assert-ResumeTrue ($routeC_budgetSnapshot.WaitSndHighWater -le 64) `
    'RED: server replay queued more than 64 KCP messages in one round.'
$routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_budgetScenario.Router,
    @()) | Out-Null

$routeC_missingHistoryScenario = New-ResumeScenario `
    $routeC_types `
    4000 `
    (New-ResumeBytes 151 16) `
    110 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    122
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_missingHistoryScenario.Router,
    @(
        $routeC_missingHistoryScenario.ResumeState,
        $routeC_missingHistoryScenario.Endpoint1,
        [uint32]121))) `
    'ASSERT missing-history-result: unsafe state was reported accepted.'
Assert-ResumeRejectedPair $routeC_types $routeC_missingHistoryScenario 7
Assert-ResumeFailureCount `
    $routeC_missingHistoryScenario `
    'ServerHistoryUnavailable' `
    1
Assert-ResumeEqual 0 @($routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_missingHistoryScenario.Router,
    @())).Count `
    'ASSERT rejected-idempotence: terminal rejection repeated after drain.'

$routeC_reconnectFloorScenario = New-ResumeScenario `
    $routeC_types `
    5000 `
    (New-ResumeBytes 152 16) `
    109 `
    111 `
    120 `
    115 `
    100 `
    123 `
    110 `
    123
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_reconnectFloorScenario.Router,
    @(
        $routeC_reconnectFloorScenario.ResumeState,
        $routeC_reconnectFloorScenario.Endpoint1,
        [uint32]121))) `
    'ASSERT reconnect-floor-result: range below reconnect floor was accepted.'
Assert-ResumeRejectedPair $routeC_types $routeC_reconnectFloorScenario 7
Assert-ResumeFailureCount `
    $routeC_reconnectFloorScenario `
    'CorrectionFloorUnavailable' `
    1

$routeC_peerFloorScenario = New-ResumeScenario `
    $routeC_types `
    6000 `
    (New-ResumeBytes 153 16) `
    110 `
    100 `
    120 `
    114 `
    116 `
    123 `
    111 `
    123
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_peerFloorScenario.Router,
    @(
        $routeC_peerFloorScenario.ResumeState,
        $routeC_peerFloorScenario.Endpoint1,
        [uint32]121))) `
    'ASSERT peer-floor-result: peer range below peer floor was accepted.'
Assert-ResumeRejectedPair $routeC_types $routeC_peerFloorScenario 7
Assert-ResumeFailureCount `
    $routeC_peerFloorScenario `
    'CorrectionFloorUnavailable' `
    1

$routeC_rangeScenario = New-ResumeScenario `
    $routeC_types `
    7000 `
    (New-ResumeBytes 154 16) `
    110 `
    100 `
    357 `
    100 `
    100 `
    123 `
    111 `
    123
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_rangeScenario.Router,
    @(
        $routeC_rangeScenario.ResumeState,
        $routeC_rangeScenario.Endpoint1,
        [uint32]121))) `
    'ASSERT range-capacity-result: 257-frame upload range was accepted.'
Assert-ResumeRejectedPair $routeC_types $routeC_rangeScenario 7
Assert-ResumeFailureCount $routeC_rangeScenario 'RangeCapacityExceeded' 1

$routeC_crossReconnectScenario = New-ResumeScenario `
    $routeC_types `
    7500 `
    (New-ResumeBytes 164 16) `
    124 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    123
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_crossReconnectScenario.Router,
    @(
        $routeC_crossReconnectScenario.ResumeState,
        $routeC_crossReconnectScenario.Endpoint1,
        [uint32]121))) `
    'RED: reconnect claimed remote progress beyond the peer local boundary.'
Assert-ResumeRejectedPair $routeC_types $routeC_crossReconnectScenario 7
Assert-ResumeFailureCount `
    $routeC_crossReconnectScenario `
    'InvalidResumeState' `
    1

$routeC_crossPeerScenario = New-ResumeScenario `
    $routeC_types `
    7600 `
    (New-ResumeBytes 165 16) `
    110 `
    100 `
    120 `
    121 `
    100 `
    123 `
    111 `
    123
Assert-ResumeTrue (-not $routeC_handleResumeState.Invoke(
    $routeC_crossPeerScenario.Router,
    @(
        $routeC_crossPeerScenario.ResumeState,
        $routeC_crossPeerScenario.Endpoint1,
        [uint32]121))) `
    'RED: peer claimed remote progress beyond the reconnect local boundary.'
Assert-ResumeRejectedPair $routeC_types $routeC_crossPeerScenario 7
Assert-ResumeFailureCount `
    $routeC_crossPeerScenario `
    'InvalidResumeState' `
    1

$routeC_graceScenario = New-ResumeScenario `
    $routeC_types `
    8000 `
    (New-ResumeBytes 155 16) `
    110 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    123
$routeC_graceScenario.Router.Tick([uint32]5000, [Diagnostics.Stopwatch]::GetTimestamp())
Assert-ResumeRejectedPair $routeC_types $routeC_graceScenario 6
Assert-ResumeFailureCount $routeC_graceScenario 'GraceExpired' 1

$routeC_generationScenario = New-ResumeScenario `
    $routeC_types `
    9000 `
    (New-ResumeBytes 156 16) `
    110 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    123
$routeC_secondReconnectEndpoint = [Net.IPEndPoint]::new(
    [Net.IPAddress]::Loopback,
    20001)
$routeC_secondReconnectArguments = @(
    $routeC_secondReconnectEndpoint,
    $routeC_generationScenario.Session0.SessionId,
    [uint32]2,
    $routeC_generationScenario.CurrentToken,
    (New-ResumeBytes 73 16),
    [uint32]121,
    $null,
    $null)
$routeC_secondReconnectResult = $routeC_types.Router.GetMethod(
    'HandleReconnectHelloAt').Invoke(
        $routeC_generationScenario.Router,
        $routeC_secondReconnectArguments)
Assert-ResumeEqual 'Rejected' $routeC_secondReconnectResult.ToString() `
    'ASSERT generation-change-result: active resume attempt allowed another generation.'
Assert-ResumeRejectedPair $routeC_types $routeC_generationScenario 7
Assert-ResumeFailureCount $routeC_generationScenario 'GenerationChanged' 1

$routeC_preReadyGenerationScenario = New-ResumeScenario `
    $routeC_types `
    9500 `
    (New-ResumeBytes 166 16) `
    110 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    123 `
    $false
$routeC_preReadySecondReconnectArguments = @(
    ([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 20501)),
    $routeC_preReadyGenerationScenario.Session0.SessionId,
    [uint32]2,
    $routeC_preReadyGenerationScenario.CurrentToken,
    (New-ResumeBytes 74 16),
    [uint32]111,
    $null,
    $null)
$routeC_preReadySecondResult = $routeC_types.Router.GetMethod(
    'HandleReconnectHelloAt').Invoke(
        $routeC_preReadyGenerationScenario.Router,
        $routeC_preReadySecondReconnectArguments)
Assert-ResumeEqual 'Rejected' $routeC_preReadySecondResult.ToString() `
    'RED: a second generation was accepted while Resuming before Ready.'
$routeC_preReadyRejectedActions = @($routeC_types.Router.GetMethod(
    'DrainActions').Invoke(
        $routeC_preReadyGenerationScenario.Router,
        @()))
Assert-ResumeEqual 2 $routeC_preReadyRejectedActions.Count `
    'ASSERT pre-ready-generation-reject: both endpoints were not terminated.'
foreach ($routeC_action in $routeC_preReadyRejectedActions)
{
    $routeC_preReadyRejected = Decode-ResumeEnvelope `
        $routeC_types `
        $routeC_action.Datagram
    Assert-ResumeEqual 'ResumeRejected' $routeC_preReadyRejected.MessageType.ToString() `
        'ASSERT pre-ready-generation-type: wrong terminal control.'
    $routeC_preReadyRejectArguments = @(
        $routeC_preReadyRejected.Payload,
        $null,
        [uint16]0)
    Assert-ResumeTrue $routeC_types.Codec.GetMethod(
        'TryDecodeResumeRejected').Invoke(
            $null,
            $routeC_preReadyRejectArguments) `
        'ASSERT pre-ready-generation-payload: rejection did not decode.'
    Assert-ResumeTrue ([Linq.Enumerable]::SequenceEqual(
        [byte[]]::new(16),
        [byte[]]$routeC_preReadyRejectArguments[1])) `
        'ASSERT pre-ready-generation-attempt: no-attempt rejection was not zero.'
    Assert-ResumeEqual 7 $routeC_preReadyRejectArguments[2] `
        'ASSERT pre-ready-generation-reason: non-grace reason changed.'
}
Assert-ResumeFailureCount `
    $routeC_preReadyGenerationScenario `
    'GenerationChanged' `
    1

$routeC_businessInputHandler = $routeC_types.Router.GetMethod(
    'HandleDecodedBusinessInput',
    [Reflection.BindingFlags]'Instance,NonPublic')
Assert-ResumeTrue ($null -ne $routeC_businessInputHandler) `
    'RED: production decoded-input resume safety seam is missing.'

$routeC_conflictScenario = New-ResumeScenario `
    $routeC_types `
    10000 `
    (New-ResumeBytes 157 16) `
    110 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    123
Assert-ResumeTrue $routeC_handleResumeState.Invoke(
    $routeC_conflictScenario.Router,
    @(
        $routeC_conflictScenario.ResumeState,
        $routeC_conflictScenario.Endpoint1,
        [uint32]121)) `
    'ASSERT conflict-freeze: valid attempt did not freeze.'
$routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_conflictScenario.Router,
    @()) | Out-Null
Assert-ResumeTrue $routeC_businessInputHandler.Invoke(
    $routeC_conflictScenario.Router,
    @($routeC_conflictScenario.Session1, [uint32]4124, 124)) `
    'ASSERT conflict-first: first live-tail raw was rejected.'
Assert-ResumeTrue (-not $routeC_businessInputHandler.Invoke(
    $routeC_conflictScenario.Router,
    @($routeC_conflictScenario.Session1, [uint32]5124, 124))) `
    'ASSERT conflict-second: different raw for the same frame was accepted.'
Assert-ResumeRejectedPair $routeC_types $routeC_conflictScenario 7
Assert-ResumeFailureCount $routeC_conflictScenario 'ConflictingInput' 1

$routeC_tailCapacityScenario = New-ResumeScenario `
    $routeC_types `
    11000 `
    (New-ResumeBytes 158 16) `
    110 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    123
Assert-ResumeTrue $routeC_handleResumeState.Invoke(
    $routeC_tailCapacityScenario.Router,
    @(
        $routeC_tailCapacityScenario.ResumeState,
        $routeC_tailCapacityScenario.Endpoint1,
        [uint32]121)) `
    'ASSERT capacity-freeze: valid attempt did not freeze.'
$routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_tailCapacityScenario.Router,
    @()) | Out-Null
for ($routeC_frame = 124; $routeC_frame -le 379; $routeC_frame++)
{
    Assert-ResumeTrue $routeC_businessInputHandler.Invoke(
        $routeC_tailCapacityScenario.Router,
        @(
            $routeC_tailCapacityScenario.Session1,
            [uint32](4000 + $routeC_frame),
            $routeC_frame)) `
        "ASSERT capacity-${routeC_frame}: bounded live-tail frame was rejected."
}
Assert-ResumeTrue (-not $routeC_businessInputHandler.Invoke(
    $routeC_tailCapacityScenario.Router,
    @($routeC_tailCapacityScenario.Session1, [uint32]4380, 380))) `
    'ASSERT capacity-257: 257th live-tail frame was accepted.'
Assert-ResumeRejectedPair $routeC_types $routeC_tailCapacityScenario 7
Assert-ResumeFailureCount `
    $routeC_tailCapacityScenario `
    'LiveTailCapacityExceeded' `
    1

$routeC_contaminatedScenario = New-ResumeScenario `
    $routeC_types `
    11500 `
    (New-ResumeBytes 168 16) `
    110 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    123
Assert-ResumeTrue $routeC_handleResumeState.Invoke(
    $routeC_contaminatedScenario.Router,
    @(
        $routeC_contaminatedScenario.ResumeState,
        $routeC_contaminatedScenario.Endpoint1,
        [uint32]121)) `
    'ASSERT contaminated-freeze: valid attempt did not freeze.'
Assert-ResumeTrue $routeC_businessInputHandler.Invoke(
    $routeC_contaminatedScenario.Router,
    @($routeC_contaminatedScenario.Session1, [uint32]4124, 124)) `
    'ASSERT contaminated-first: first live-tail raw was rejected.'
Assert-ResumeTrue (-not $routeC_businessInputHandler.Invoke(
    $routeC_contaminatedScenario.Router,
    @($routeC_contaminatedScenario.Session1, [uint32]5124, 124))) `
    'ASSERT contaminated-second: conflicting raw was accepted.'
Assert-ResumeRejectedPair $routeC_types $routeC_contaminatedScenario 7
$routeC_contaminatedScenario.Router.Tick(
    [uint32]500,
    [Diagnostics.Stopwatch]::GetTimestamp())
Assert-ResumeEqual 0 @($routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_contaminatedScenario.Router,
    @())).Count `
    'RED: terminated router emitted queued KCP or control work on a later tick.'
$routeC_terminatedReady = [byte[]]$routeC_types.Codec.GetMethod(
    'EncodeReady').Invoke(
        $null,
        @(110, 100, 120))
Assert-ResumeTrue (-not $routeC_handleReady.Invoke(
    $routeC_contaminatedScenario.Router,
    @(
        (New-ResumeMessage `
            $routeC_types `
            'Ready' `
            $routeC_contaminatedScenario.Session0 `
            $routeC_terminatedReady),
        $routeC_contaminatedScenario.Endpoint0,
        [uint32]501))) `
    'RED: Ready reopened a terminated resume session.'
Assert-ResumeEqual 0 @($routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_contaminatedScenario.Router,
    @())).Count `
    'RED: terminated Ready emitted Start or resume controls.'

Write-Output 'PASS: Task 9 Slice 4 safety failures reject both peers with stable wire reasons and distinct internal diagnostics.'

$routeC_handleResumeComplete = $routeC_types.Router.GetMethod(
    'HandleResumeComplete')
$routeC_handleResumeRejected = $routeC_types.Router.GetMethod(
    'HandleResumeRejected')
Assert-ResumeTrue ($null -ne $routeC_handleResumeComplete) `
    'RED: server ResumeComplete coordinator is missing.'
Assert-ResumeTrue ($null -ne $routeC_handleResumeRejected) `
    'RED: server cannot terminate an attempt after client history failure.'

$routeC_tailBudgetScenario = New-ResumeScenario `
    $routeC_types `
    11800 `
    (New-ResumeBytes 169 16) `
    -1 `
    0 `
    -1 `
    -1 `
    0 `
    -1 `
    0 `
    -1
Assert-ResumeTrue $routeC_handleResumeState.Invoke(
    $routeC_tailBudgetScenario.Router,
    @(
        $routeC_tailBudgetScenario.ResumeState,
        $routeC_tailBudgetScenario.Endpoint1,
        [uint32]121)) `
    'ASSERT tail-budget-freeze: empty resume plan did not freeze.'
$routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_tailBudgetScenario.Router,
    @()) | Out-Null
for ($routeC_frame = 0; $routeC_frame -lt 256; $routeC_frame++)
{
    Assert-ResumeTrue $routeC_businessInputHandler.Invoke(
        $routeC_tailBudgetScenario.Router,
        @(
            $routeC_tailBudgetScenario.Session1,
            [uint32](7000 + $routeC_frame),
            $routeC_frame)) `
        "ASSERT tail-budget-record-${routeC_frame}: bounded tail rejected."
}
$routeC_emptyCompletePayload = [byte[]]$routeC_types.Codec.GetMethod(
    'EncodeResumeComplete').Invoke(
        $null,
        @($routeC_tailBudgetScenario.AttemptID, -1, -1))
$routeC_emptyComplete0 = New-ResumeMessage `
    $routeC_types `
    'ResumeComplete' `
    $routeC_tailBudgetScenario.Session0 `
    $routeC_emptyCompletePayload
$routeC_emptyComplete1 = New-ResumeMessage `
    $routeC_types `
    'ResumeComplete' `
    $routeC_tailBudgetScenario.Session1 `
    $routeC_emptyCompletePayload
Assert-ResumeTrue $routeC_handleResumeComplete.Invoke(
    $routeC_tailBudgetScenario.Router,
    @(
        $routeC_emptyComplete0,
        $routeC_tailBudgetScenario.Endpoint0,
        [uint32]122)) `
    'ASSERT tail-budget-complete0: first completion rejected.'
Assert-ResumeTrue $routeC_handleResumeComplete.Invoke(
    $routeC_tailBudgetScenario.Router,
    @(
        $routeC_emptyComplete1,
        $routeC_tailBudgetScenario.Endpoint1,
        [uint32]123)) `
    'ASSERT tail-budget-complete1: second completion rejected.'
$routeC_tailBudgetAttempt = $routeC_attemptField.GetValue(
    $routeC_tailBudgetScenario.Router)
$routeC_pendingTailProperty = $routeC_tailBudgetAttempt.GetType().GetProperty(
    'PendingTailCount')
Assert-ResumeTrue ($null -ne $routeC_pendingTailProperty) `
    'RED: frozen live-tail has no progressive pending-work budget seam.'
Assert-ResumeEqual 256 $routeC_tailBudgetAttempt.PendingTailCount `
    'RED: live-tail was synchronously queued during completion handling.'
Assert-ResumeEqual 'Resuming' $routeC_tailBudgetScenario.Session0.State.ToString() `
    'RED: session returned Running before bounded tail queueing finished.'
$routeC_tailBudgetScenario.Router.Tick(
    [uint32]130,
    [Diagnostics.Stopwatch]::GetTimestamp())
$routeC_remainingTail = $routeC_tailBudgetAttempt.PendingTailCount
Assert-ResumeTrue ($routeC_remainingTail -ge 192 -and
    $routeC_remainingTail -lt 256) `
    'RED: one server round did not consume between 1 and 64 tail messages.'
$routeC_tailBudgetKcp = $routeC_kcpProperty.GetValue(
    $routeC_tailBudgetScenario.Session0)
Assert-ResumeTrue ($routeC_tailBudgetKcp.SnapshotDiagnostics().WaitSndHighWater -le 64) `
    'RED: server tail release queued more than 64 KCP messages in one round.'
$routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_tailBudgetScenario.Router,
    @()) | Out-Null

$routeC_completeScenario = New-ResumeScenario `
    $routeC_types `
    12000 `
    (New-ResumeBytes 159 16) `
    110 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    123
Assert-ResumeEqual 'Accepted' $routeC_completeScenario.Session0.History.Record(
    116,
    [uint32]1116).ToString() `
    'ASSERT retained-upload-setup: pre-retained frame 116 was not accepted.'
Assert-ResumeTrue $routeC_handleResumeState.Invoke(
    $routeC_completeScenario.Router,
    @(
        $routeC_completeScenario.ResumeState,
        $routeC_completeScenario.Endpoint1,
        [uint32]121)) `
    'ASSERT complete-freeze: valid attempt did not freeze.'
$routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_completeScenario.Router,
    @()) | Out-Null
$routeC_completeScenario.Router.Tick(
    [uint32]130,
    [Diagnostics.Stopwatch]::GetTimestamp())
$routeC_replayActions = @($routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_completeScenario.Router,
    @()))
$routeC_replayFrames = @(Get-KcpBusinessFrames `
    $routeC_types `
    $routeC_completeScenario.Router `
    $routeC_completeScenario.Session0 `
    $routeC_replayActions `
    ([uint32]130) `
    13)
Assert-ResumeEqual 13 $routeC_replayFrames.Count `
    'ASSERT replay-count: server did not replay exactly 111..123 through reconnect KCP.'
for ($routeC_offset = 0; $routeC_offset -lt 13; $routeC_offset++)
{
    Assert-ResumeEqual (111 + $routeC_offset) $routeC_replayFrames[$routeC_offset] `
        "ASSERT replay-order-${routeC_offset}: server replay order changed."
}

$routeC_earlyCompletePayload = [byte[]]$routeC_types.Codec.GetMethod(
    'EncodeResumeComplete').Invoke(
        $null,
        @($routeC_completeScenario.AttemptID, 120, 123))
$routeC_earlyComplete = New-ResumeMessage `
    $routeC_types `
    'ResumeComplete' `
    $routeC_completeScenario.Session0 `
    $routeC_earlyCompletePayload
Assert-ResumeTrue (-not $routeC_handleResumeComplete.Invoke(
    $routeC_completeScenario.Router,
    @(
        $routeC_earlyComplete,
        $routeC_completeScenario.Endpoint0,
        [uint32]300))) `
    'ASSERT early-complete: reconnect completion unlocked before upload existed.'
Assert-ResumeEqual 'Resuming' $routeC_completeScenario.Session0.State.ToString() `
    'ASSERT early-complete-state: reconnecting player unlocked early.'

for ($routeC_frame = 116; $routeC_frame -le 120; $routeC_frame++)
{
    Assert-ResumeTrue $routeC_businessInputHandler.Invoke(
        $routeC_completeScenario.Router,
        @(
            $routeC_completeScenario.Session0,
            [uint32](1000 + $routeC_frame),
            $routeC_frame)) `
        "ASSERT upload-${routeC_frame}: reconnect upload was rejected."
}
Assert-ResumeTrue $routeC_businessInputHandler.Invoke(
    $routeC_completeScenario.Router,
    @($routeC_completeScenario.Session0, [uint32]1121, 121)) `
    'ASSERT reconnect-tail: reconnect live tail was rejected.'
Assert-ResumeTrue $routeC_businessInputHandler.Invoke(
    $routeC_completeScenario.Router,
    @($routeC_completeScenario.Session1, [uint32]2124, 124)) `
    'ASSERT peer-tail: peer live tail was rejected.'
$routeC_completeScenario.Router.Tick(
    [uint32]500,
    [Diagnostics.Stopwatch]::GetTimestamp())
$routeC_uploadActions = @($routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_completeScenario.Router,
    @()))
$routeC_uploadFrames = @(Get-KcpBusinessFrames `
    $routeC_types `
    $routeC_completeScenario.Router `
    $routeC_completeScenario.Session1 `
    $routeC_uploadActions `
    ([uint32]500) `
    5)
Assert-ResumeEqual 5 $routeC_uploadFrames.Count `
    'ASSERT upload-relay-count: server did not relay exactly 116..120 through peer KCP.'
for ($routeC_offset = 0; $routeC_offset -lt 5; $routeC_offset++)
{
    Assert-ResumeEqual (116 + $routeC_offset) $routeC_uploadFrames[$routeC_offset] `
        "ASSERT upload-relay-order-${routeC_offset}: upload relay order changed."
}

Assert-ResumeTrue $routeC_handleResumeComplete.Invoke(
    $routeC_completeScenario.Router,
    @(
        $routeC_earlyComplete,
        $routeC_completeScenario.Endpoint0,
        [uint32]800)) `
    'ASSERT reconnect-complete: covered reconnect completion was rejected.'
Assert-ResumeEqual 0 @($routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_completeScenario.Router,
    @())).Count `
    'ASSERT one-complete: one client completion released the tail.'
$routeC_peerCompletePayload = [byte[]]$routeC_types.Codec.GetMethod(
    'EncodeResumeComplete').Invoke(
        $null,
        @($routeC_completeScenario.AttemptID, 123, 120))
$routeC_peerComplete = New-ResumeMessage `
    $routeC_types `
    'ResumeComplete' `
    $routeC_completeScenario.Session1 `
    $routeC_peerCompletePayload
Assert-ResumeTrue $routeC_handleResumeComplete.Invoke(
    $routeC_completeScenario.Router,
    @(
        $routeC_peerComplete,
        $routeC_completeScenario.Endpoint1,
        [uint32]801)) `
    'ASSERT peer-complete: covered peer completion was rejected.'
$routeC_preTailCompleteActions = @($routeC_types.Router.GetMethod(
    'DrainActions').Invoke(
        $routeC_completeScenario.Router,
        @()))
Assert-ResumeEqual 0 $routeC_preTailCompleteActions.Count `
    'RED: completion was emitted before frozen tails entered the bounded pump.'
$routeC_completeScenario.Router.Tick(
    [uint32]802,
    [Diagnostics.Stopwatch]::GetTimestamp())
$routeC_serverCompleteActions = @($routeC_types.Router.GetMethod(
    'DrainActions').Invoke(
        $routeC_completeScenario.Router,
        @()))
$routeC_serverCompleteMessages = @($routeC_serverCompleteActions |
    ForEach-Object { Decode-ResumeEnvelope $routeC_types $_.Datagram })
$routeC_completeControls = @($routeC_serverCompleteMessages |
    Where-Object MessageType -eq ([Enum]::Parse(
        $routeC_types.MessageType,
        'ResumeComplete')))
$routeC_tailKcpMessages = @($routeC_serverCompleteMessages |
    Where-Object MessageType -eq ([Enum]::Parse(
        $routeC_types.MessageType,
        'KcpData')))
Assert-ResumeEqual 2 $routeC_completeControls.Count `
    'ASSERT server-complete-count: completion was not sent once per endpoint.'
Assert-ResumeEqual 2 $routeC_tailKcpMessages.Count `
    'ASSERT tail-kcp-count: both frozen tails were not flushed through KCP.'
foreach ($routeC_serverComplete in $routeC_completeControls)
{
    Assert-ResumeEqual 'ResumeComplete' $routeC_serverComplete.MessageType.ToString() `
        'ASSERT server-complete-type: wrong terminal resume control.'
}
Assert-ResumeEqual 'Running' $routeC_completeScenario.Session0.State.ToString() `
    'ASSERT complete-player0: reconnecting player did not resume Running.'
Assert-ResumeEqual 'Running' $routeC_completeScenario.Session1.State.ToString() `
    'ASSERT complete-player1: peer did not resume Running.'
$routeC_tail0 = @(121, [uint32]0)
$routeC_tail1 = @(124, [uint32]0)
Assert-ResumeTrue $routeC_completeScenario.Session0.History.GetType().GetMethod(
    'TryGet').Invoke(
        $routeC_completeScenario.Session0.History,
        $routeC_tail0) `
    'ASSERT release-tail0: reconnect tail disappeared.'
Assert-ResumeTrue $routeC_completeScenario.Session1.History.GetType().GetMethod(
    'TryGet').Invoke(
        $routeC_completeScenario.Session1.History,
        $routeC_tail1) `
    'ASSERT release-tail1: peer tail disappeared.'

Assert-ResumeTrue $routeC_handleResumeState.Invoke(
    $routeC_completeScenario.Router,
    @(
        $routeC_completeScenario.ResumeState,
        $routeC_completeScenario.Endpoint1,
        [uint32]802)) `
    'ASSERT late-state-result: completed attempt rejected matching late state.'
$routeC_lateStateActions = @($routeC_types.Router.GetMethod(
    'DrainActions').Invoke(
        $routeC_completeScenario.Router,
        @()))
Assert-ResumeEqual 2 $routeC_lateStateActions.Count `
    'ASSERT late-state-count: matching late state did not resend stable completion.'
foreach ($routeC_action in $routeC_lateStateActions)
{
    Assert-ResumeEqual 'ResumeComplete' (Decode-ResumeEnvelope `
        $routeC_types `
        $routeC_action.Datagram).MessageType.ToString() `
        'RED: late ResumeState regressed a completed attempt back to ResumeAccepted.'
}
$routeC_lateReadyPayload = [byte[]]$routeC_types.Codec.GetMethod(
    'EncodeReady').Invoke(
        $null,
        @(110, 100, 120))
Assert-ResumeTrue $routeC_handleReady.Invoke(
    $routeC_completeScenario.Router,
    @(
        (New-ResumeMessage `
            $routeC_types `
            'Ready' `
            $routeC_completeScenario.Session0 `
            $routeC_lateReadyPayload),
        $routeC_completeScenario.Endpoint0,
        [uint32]803)) `
    'ASSERT late-ready-result: completed attempt rejected matching late Ready.'
$routeC_lateReadyActions = @($routeC_types.Router.GetMethod(
    'DrainActions').Invoke(
        $routeC_completeScenario.Router,
        @()))
Assert-ResumeEqual 2 $routeC_lateReadyActions.Count `
    'ASSERT late-ready-count: matching late Ready did not resend stable completion.'
foreach ($routeC_action in $routeC_lateReadyActions)
{
    Assert-ResumeEqual 'ResumeComplete' (Decode-ResumeEnvelope `
        $routeC_types `
        $routeC_action.Datagram).MessageType.ToString() `
        'RED: late Ready emitted Start or reopened a completed attempt.'
}
$routeC_completeScenario.Router.Tick(
    [uint32]10000,
    [Diagnostics.Stopwatch]::GetTimestamp())
$routeC_postCompleteGraceActions = @($routeC_types.Router.GetMethod(
    'DrainActions').Invoke(
        $routeC_completeScenario.Router,
        @()))
Assert-ResumeEqual 2 $routeC_postCompleteGraceActions.Count `
    'ASSERT post-complete-grace-count: grace rejection was not sent to both peers.'
foreach ($routeC_action in $routeC_postCompleteGraceActions)
{
    $routeC_postCompleteGrace = Decode-ResumeEnvelope `
        $routeC_types `
        $routeC_action.Datagram
    Assert-ResumeEqual 'ResumeRejected' $routeC_postCompleteGrace.MessageType.ToString() `
        'ASSERT post-complete-grace-type: stale traffic survived terminal grace.'
    $routeC_postCompleteGraceArguments = @(
        $routeC_postCompleteGrace.Payload,
        $null,
        [uint16]0)
    Assert-ResumeTrue $routeC_types.Codec.GetMethod(
        'TryDecodeResumeRejected').Invoke(
            $null,
            $routeC_postCompleteGraceArguments) `
        'ASSERT post-complete-grace-payload: rejection did not decode.'
    Assert-ResumeTrue ([Linq.Enumerable]::SequenceEqual(
        [byte[]]::new(16),
        [byte[]]$routeC_postCompleteGraceArguments[1])) `
        'RED: completed AttemptID leaked into a later no-attempt grace rejection.'
    Assert-ResumeEqual 6 $routeC_postCompleteGraceArguments[2] `
        'ASSERT post-complete-grace-reason: grace wire reason changed.'
}

$routeC_clientRejectScenario = New-ResumeScenario `
    $routeC_types `
    13000 `
    (New-ResumeBytes 160 16) `
    110 `
    100 `
    120 `
    115 `
    100 `
    123 `
    111 `
    123
Assert-ResumeTrue $routeC_handleResumeState.Invoke(
    $routeC_clientRejectScenario.Router,
    @(
        $routeC_clientRejectScenario.ResumeState,
        $routeC_clientRejectScenario.Endpoint1,
        [uint32]121)) `
    'ASSERT client-reject-freeze: valid attempt did not freeze.'
$routeC_types.Router.GetMethod('DrainActions').Invoke(
    $routeC_clientRejectScenario.Router,
    @()) | Out-Null
$routeC_clientRejectPayload = [byte[]]$routeC_types.Codec.GetMethod(
    'EncodeResumeRejected').Invoke(
        $null,
        @($routeC_clientRejectScenario.AttemptID, [uint16]7))
$routeC_clientRejectMessage = New-ResumeMessage `
    $routeC_types `
    'ResumeRejected' `
    $routeC_clientRejectScenario.Session0 `
    $routeC_clientRejectPayload
Assert-ResumeTrue $routeC_handleResumeRejected.Invoke(
    $routeC_clientRejectScenario.Router,
    @(
        $routeC_clientRejectMessage,
        $routeC_clientRejectScenario.Endpoint0,
        [uint32]122)) `
    'ASSERT client-reject-result: matching client rejection was ignored.'
Assert-ResumeRejectedPair $routeC_types $routeC_clientRejectScenario 7
Assert-ResumeFailureCount `
    $routeC_clientRejectScenario `
    'ClientHistoryUnavailable' `
    1

Write-Output 'PASS: Task 9 Slice 5 upload coverage, dual completion, client rejection, and tail release are correct.'

if ($ServerOnly)
{
    Write-Output 'PASS: server-only resume protocol coverage completed against the Release assembly.'
    return
}

$routeC_clientType = $routeC_assembly.GetType(
    'FrameSyncDemo.KcpUdpClientStateMachine',
    $true)
$routeC_clockType = $routeC_assembly.GetType(
    'FrameSyncDemo.StopwatchMonotonicClock',
    $true)
$routeC_readinessType = $routeC_assembly.GetType(
    'FrameSyncDemo.ResumeReadiness',
    $true)
$routeC_eventType = $routeC_assembly.GetType(
    'FrameSyncDemo.NetworkTransportEvent',
    $true)
$routeC_clock = [Activator]::CreateInstance($routeC_clockType)
$routeC_clientNonce = New-ResumeBytes 5 16
$routeC_clientToken = New-ResumeBytes 55 32
$routeC_clientAttempt = New-ResumeBytes 105 16
$routeC_nonceFactory = New-ResumeFactory ([byte[]]) @(,$routeC_clientNonce)
$routeC_clientSends = [Collections.ArrayList]::new()
$routeC_clientEvents = [Collections.ArrayList]::new()
$routeC_sendType = [Action``1].MakeGenericType(@($routeC_types.Message))
$routeC_sendScript = {
    param($Message)
    [void]$routeC_clientSends.Add($Message)
}.GetNewClosure()
$routeC_sendAction = [Management.Automation.LanguagePrimitives]::ConvertTo(
    $routeC_sendScript,
    $routeC_sendType)
$routeC_eventActionType = [Action``1].MakeGenericType(@($routeC_eventType))
$routeC_eventScript = {
    param($Event)
    [void]$routeC_clientEvents.Add($Event)
}.GetNewClosure()
$routeC_eventAction = [Management.Automation.LanguagePrimitives]::ConvertTo(
    $routeC_eventScript,
    $routeC_eventActionType)
$routeC_client = [Activator]::CreateInstance(
    $routeC_clientType,
    @($routeC_clock, $routeC_nonceFactory, $routeC_sendAction, $routeC_eventAction))
$routeC_client.Start()
$routeC_clientSession = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]3001, [uint64]3002))
$routeC_welcomePayload = [byte[]]$routeC_types.Codec.GetMethod('EncodeWelcome').Invoke(
    $null,
    @(
        $routeC_clientNonce,
        [byte]1,
        [uint32]701,
        $routeC_clientToken,
        1000,
        3000,
        $false))
$routeC_welcomeMessage = [Activator]::CreateInstance(
    $routeC_types.Message,
    @(
        ([Enum]::Parse($routeC_types.MessageType, 'Welcome')),
        $routeC_clientSession,
        [uint32]1,
        $routeC_welcomePayload))
$routeC_client.HandleIncoming($routeC_welcomeMessage)
$routeC_startPayload = [byte[]]$routeC_types.Codec.GetMethod('EncodeStart').Invoke(
    $null,
    @(0))
$routeC_startMessage = [Activator]::CreateInstance(
    $routeC_types.Message,
    @(
        ([Enum]::Parse($routeC_types.MessageType, 'Start')),
        $routeC_clientSession,
        [uint32]1,
        $routeC_startPayload))
$routeC_client.HandleIncoming($routeC_startMessage)
Assert-ResumeEqual 'Running' $routeC_client.State.ToString() `
    'ASSERT client-running: setup did not reach Running.'

$routeC_resumeStateTypeValue = [Enum]::Parse($routeC_types.MessageType, 'ResumeState')
$routeC_resumeProbeTypeValue = [Enum]::Parse($routeC_types.MessageType, 'ResumeProbe')
$routeC_wrongDirectionPayload = [byte[]]$routeC_types.Codec.GetMethod('EncodeResumeState').Invoke(
    $null,
    @($routeC_clientAttempt, 1, 0, 1))
$routeC_client.HandleIncoming([Activator]::CreateInstance(
    $routeC_types.Message,
    @($routeC_resumeStateTypeValue, $routeC_clientSession, [uint32]1, $routeC_wrongDirectionPayload)))
$routeC_client.HandleIncoming([Activator]::CreateInstance(
    $routeC_types.Message,
    @(
        $routeC_resumeProbeTypeValue,
        ([Activator]::CreateInstance($routeC_types.SessionId, @([uint64]9991, [uint64]9992))),
        [uint32]1,
        ([byte[]]$routeC_types.Codec.GetMethod('EncodeResumeProbe').Invoke($null, @(,$routeC_clientAttempt))))))
$routeC_client.HandleIncoming([Activator]::CreateInstance(
    $routeC_types.Message,
    @(
        $routeC_resumeProbeTypeValue,
        $routeC_clientSession,
        [uint32]2,
        ([byte[]]$routeC_types.Codec.GetMethod('EncodeResumeProbe').Invoke($null, @(,$routeC_clientAttempt))))))

$routeC_clientSendCountBeforeProbe = $routeC_clientSends.Count
$routeC_probePayload = [byte[]]$routeC_types.Codec.GetMethod('EncodeResumeProbe').Invoke(
    $null,
    @(,$routeC_clientAttempt))
$routeC_clientProbe = [Activator]::CreateInstance(
    $routeC_types.Message,
    @($routeC_resumeProbeTypeValue, $routeC_clientSession, [uint32]1, $routeC_probePayload))
$routeC_client.HandleIncoming($routeC_clientProbe)
Assert-ResumeEqual 'Resuming' $routeC_client.State.ToString() `
    'ASSERT client-probe-state: online peer did not enter Resuming.'
Assert-ResumeEqual $routeC_clientSendCountBeforeProbe $routeC_clientSends.Count `
    'ASSERT client-probe-readiness: default ResumeState was sent before readiness.'
$routeC_clientReadiness = [Activator]::CreateInstance(
    $routeC_readinessType,
    @(115, 100, 123))
$routeC_clientType.GetMethod('SubmitResumeReadiness').Invoke(
    $routeC_client,
    @($routeC_clientReadiness)) | Out-Null
$routeC_firstResumeState = $routeC_clientSends[$routeC_clientSends.Count - 1]
Assert-ResumeEqual 'ResumeState' $routeC_firstResumeState.MessageType.ToString() `
    'ASSERT client-resume-state-type: readiness did not emit ResumeState.'
$routeC_firstResumeStatePayload = [byte[]]$routeC_firstResumeState.Payload

$routeC_client.HandleIncoming($routeC_clientProbe)
$routeC_duplicateResumeState = $routeC_clientSends[$routeC_clientSends.Count - 1]
Assert-ResumeTrue ([Linq.Enumerable]::SequenceEqual(
    $routeC_firstResumeStatePayload,
    [byte[]]$routeC_duplicateResumeState.Payload)) `
    'ASSERT client-probe-idempotence: duplicate probe changed ResumeState bytes.'
$routeC_wrongClientAttempt = New-ResumeBytes 125 16
$routeC_client.HandleIncoming([Activator]::CreateInstance(
    $routeC_types.Message,
    @(
        $routeC_resumeProbeTypeValue,
        $routeC_clientSession,
        [uint32]1,
        ([byte[]]$routeC_types.Codec.GetMethod('EncodeResumeProbe').Invoke($null, @(,$routeC_wrongClientAttempt))))))

Start-Sleep -Milliseconds 260
$routeC_resumeStateCountBeforeTick = @(
    $routeC_clientSends | Where-Object MessageType -eq $routeC_resumeStateTypeValue).Count
$routeC_client.Tick()
$routeC_resumeStateCountAfterTick = @(
    $routeC_clientSends | Where-Object MessageType -eq $routeC_resumeStateTypeValue).Count
Assert-ResumeEqual ($routeC_resumeStateCountBeforeTick + 1) $routeC_resumeStateCountAfterTick `
    'ASSERT client-resume-state-retry: 250ms retry did not resend ResumeState.'

foreach ($routeC_clientCounter in @{
    ResumeWrongDirectionCount = 1
    ResumeWrongSessionCount = 1
    ResumeWrongGenerationCount = 1
    ResumeWrongAttemptCount = 1
}.GetEnumerator())
{
    $routeC_property = $routeC_clientType.GetProperty($routeC_clientCounter.Key)
    Assert-ResumeTrue ($null -ne $routeC_property) `
        "ASSERT client-counter-surface: $($routeC_clientCounter.Key) is missing."
    Assert-ResumeEqual $routeC_clientCounter.Value $routeC_property.GetValue($routeC_client, $null) `
        "ASSERT client-counter-value: $($routeC_clientCounter.Key) did not count independently."
}
$routeC_clientDiagnostic = $routeC_client.DiagnosticSummary
Assert-ResumeTrue (-not $routeC_clientDiagnostic.Contains([BitConverter]::ToString($routeC_clientToken))) `
    'ASSERT token-log: reconnect token leaked into client diagnostics.'
Assert-ResumeTrue (-not $routeC_clientDiagnostic.Contains([BitConverter]::ToString($routeC_clientAttempt))) `
    'ASSERT attempt-log: raw AttemptID leaked into client diagnostics.'

$routeC_planType = $routeC_assembly.GetType(
    'FrameSyncDemo.KcpClientResumePlan',
    $false)
$routeC_coordinatorType = $routeC_assembly.GetType(
    'FrameSyncDemo.KcpClientResumeCoordinator',
    $false)
Assert-ResumeTrue ($null -ne $routeC_planType) `
    'RED: immutable client resume plan is missing.'
Assert-ResumeTrue ($null -ne $routeC_coordinatorType) `
    'RED: bounded client resume coordinator is missing.'
$routeC_takePlan = $routeC_clientType.GetMethod('TryTakeResumePlan')
$routeC_submitProgress = $routeC_clientType.GetMethod('SubmitResumeProgress')
Assert-ResumeTrue ($null -ne $routeC_takePlan) `
    'RED: client cannot hand one accepted frozen plan to its worker.'
Assert-ResumeTrue ($null -ne $routeC_submitProgress) `
    'RED: client cannot report bounded resume progress.'

$routeC_acceptedTypeValue = [Enum]::Parse(
    $routeC_types.MessageType,
    'ResumeAccepted')
$routeC_clientAcceptedPayload = [byte[]]$routeC_types.Codec.GetMethod(
    'EncodeResumeAccepted').Invoke(
        $null,
        @($routeC_clientAttempt, 116, 120, 111, 123, 116, 120))
$routeC_client.HandleIncoming([Activator]::CreateInstance(
    $routeC_types.Message,
    @(
        $routeC_acceptedTypeValue,
        $routeC_clientSession,
        [uint32]1,
        $routeC_clientAcceptedPayload)))
$routeC_planArguments = @($null)
Assert-ResumeTrue $routeC_takePlan.Invoke(
    $routeC_client,
    $routeC_planArguments) `
    'ASSERT client-plan: accepted plan was not handed off once.'
$routeC_clientPlan = $routeC_planArguments[0]
Assert-ResumeTrue $routeC_clientPlan.IsPeer `
    'ASSERT client-plan-role: online peer role changed.'
Assert-ResumeEqual 123 $routeC_clientPlan.LocalFrozenThrough `
    'ASSERT client-plan-local-freeze: wrong local freeze upper bound.'
Assert-ResumeEqual 116 $routeC_clientPlan.ExpectedRemoteFrom `
    'ASSERT client-plan-remote-from: wrong peer wait lower bound.'
Assert-ResumeEqual 120 $routeC_clientPlan.ExpectedRemoteThrough `
    'ASSERT client-plan-remote-through: wrong peer wait upper bound.'
$routeC_secondPlanArguments = @($null)
Assert-ResumeTrue (-not $routeC_takePlan.Invoke(
    $routeC_client,
    $routeC_secondPlanArguments)) `
    'ASSERT client-plan-once: frozen plan was handed off twice.'

$routeC_outboundHistoryType = $routeC_assembly.GetType(
    'FrameSyncDemo.OutboundActualHistory',
    $true)
$routeC_peerClientHistory = [Activator]::CreateInstance(
    $routeC_outboundHistoryType,
    @(256))
$routeC_peerCoordinator = [Activator]::CreateInstance(
    $routeC_coordinatorType,
    @($routeC_peerClientHistory))
$routeC_coordinatorType.GetMethod('ObserveRemoteFrame').Invoke(
    $routeC_peerCoordinator,
    @(116)) | Out-Null
$routeC_coordinatorType.GetMethod('ObserveRemoteFrame').Invoke(
    $routeC_peerCoordinator,
    @(117)) | Out-Null
$routeC_coordinatorType.GetMethod('ObserveRemoteFrame').Invoke(
    $routeC_peerCoordinator,
    @(118)) | Out-Null
$routeC_coordinatorType.GetMethod('ObserveRemoteFrame').Invoke(
    $routeC_peerCoordinator,
    @(119)) | Out-Null
$routeC_peerUploadArguments = @($routeC_clientPlan, $null)
Assert-ResumeTrue $routeC_coordinatorType.GetMethod('TryBegin').Invoke(
    $routeC_peerCoordinator,
    $routeC_peerUploadArguments) `
    'ASSERT peer-coordinator-begin: valid peer plan was rejected.'
Assert-ResumeEqual 0 ([byte[][]]$routeC_peerUploadArguments[1]).Length `
    'ASSERT peer-upload: online peer invented a second upload batch.'
Assert-ResumeTrue (-not $routeC_peerCoordinator.IsCompleteReady) `
    'RED: pre-Accepted replay progress was forgotten or unlocked early.'
$routeC_coordinatorType.GetMethod('ObserveRemoteFrame').Invoke(
    $routeC_peerCoordinator,
    @(120)) | Out-Null
Assert-ResumeTrue $routeC_peerCoordinator.IsCompleteReady `
    'ASSERT peer-complete-ready: full peer range did not unlock completion.'

$routeC_resumeCompleteTypeValue = [Enum]::Parse(
    $routeC_types.MessageType,
    'ResumeComplete')
$routeC_completeCountBefore = @(
    $routeC_clientSends | Where-Object MessageType -eq $routeC_resumeCompleteTypeValue).Count
$routeC_submitProgress.Invoke(
    $routeC_client,
    @(
        $routeC_peerCoordinator.UploadedLocalThrough,
        $routeC_peerCoordinator.ReceivedRemoteThrough)) | Out-Null
$routeC_completeMessages = @(
    $routeC_clientSends | Where-Object MessageType -eq $routeC_resumeCompleteTypeValue)
Assert-ResumeEqual ($routeC_completeCountBefore + 1) $routeC_completeMessages.Count `
    'ASSERT client-complete-send: complete bounds did not emit ResumeComplete.'
$routeC_completeArguments = @(
    $routeC_completeMessages[-1].Payload,
    $null,
    0,
    0)
Assert-ResumeTrue $routeC_types.Codec.GetMethod(
    'TryDecodeResumeComplete').Invoke(
        $null,
        $routeC_completeArguments) `
    'ASSERT client-complete-payload: ResumeComplete did not decode.'
Assert-ResumeEqual 123 $routeC_completeArguments[2] `
    'ASSERT client-complete-uploaded: uploaded upper bound changed.'
Assert-ResumeEqual 120 $routeC_completeArguments[3] `
    'ASSERT client-complete-received: received upper bound changed.'
$routeC_serverCompleteMessage = [Activator]::CreateInstance(
    $routeC_types.Message,
    @(
        $routeC_resumeCompleteTypeValue,
        $routeC_clientSession,
        [uint32]1,
        [byte[]]$routeC_completeMessages[-1].Payload))
$routeC_client.HandleIncoming($routeC_serverCompleteMessage)
Assert-ResumeEqual 'Running' $routeC_client.State.ToString() `
    'ASSERT client-running-after-complete: server completion did not resume Running.'
$routeC_client.HandleIncoming($routeC_serverCompleteMessage)
Assert-ResumeEqual 'Running' $routeC_client.State.ToString() `
    'ASSERT client-complete-idempotence: duplicate server completion changed state.'
$routeC_releaseClientTailArguments = @($null)
Assert-ResumeTrue $routeC_coordinatorType.GetMethod(
    'TryReleaseLocalTail').Invoke(
        $routeC_peerCoordinator,
        $routeC_releaseClientTailArguments) `
    'ASSERT client-tail-release: server completion did not release local freeze.'
$routeC_secondClientTailReleaseArguments = @($null)
Assert-ResumeTrue (-not $routeC_coordinatorType.GetMethod(
    'TryReleaseLocalTail').Invoke(
        $routeC_peerCoordinator,
        $routeC_secondClientTailReleaseArguments)) `
    'ASSERT client-tail-release-once: local tail released twice.'

$routeC_missingClientHistory = [Activator]::CreateInstance(
    $routeC_outboundHistoryType,
    @(256))
for ($routeC_frame = 117; $routeC_frame -le 120; $routeC_frame++)
{
    $routeC_missingClientHistory.Record(
        $routeC_frame,
        [uint32](5000 + $routeC_frame)) | Out-Null
}
$routeC_reconnectPlan = [Activator]::CreateInstance(
    $routeC_planType,
    @(
        $routeC_clientAttempt,
        $false,
        116,
        120,
        111,
        123,
        116,
        120))
$routeC_missingCoordinator = [Activator]::CreateInstance(
    $routeC_coordinatorType,
    @($routeC_missingClientHistory))
$routeC_missingUploadArguments = @($routeC_reconnectPlan, $null)
Assert-ResumeTrue (-not $routeC_coordinatorType.GetMethod('TryBegin').Invoke(
    $routeC_missingCoordinator,
    $routeC_missingUploadArguments)) `
    'ASSERT missing-client-history: upload with missing frame 116 was accepted.'

$routeC_tryTakeUpload = $routeC_coordinatorType.GetMethod(
    'TryTakeNextUploadPayload')
$routeC_pendingUploadProperty = $routeC_coordinatorType.GetProperty(
    'PendingUploadCount')
Assert-ResumeTrue ($null -ne $routeC_tryTakeUpload) `
    'RED: client resume upload has no progressive per-round pump seam.'
Assert-ResumeTrue ($null -ne $routeC_pendingUploadProperty) `
    'RED: client resume upload cannot expose bounded pending work.'
$routeC_bulkHistory = [Activator]::CreateInstance(
    $routeC_outboundHistoryType,
    @(256))
for ($routeC_frame = 0; $routeC_frame -lt 256; $routeC_frame++)
{
    $routeC_bulkHistory.Record(
        $routeC_frame,
        [uint32](6000 + $routeC_frame)) | Out-Null
}
$routeC_bulkPlan = [Activator]::CreateInstance(
    $routeC_planType,
    @($routeC_clientAttempt, $false, 0, 255, 0, -1, 0, 255))
$routeC_bulkCoordinator = [Activator]::CreateInstance(
    $routeC_coordinatorType,
    @($routeC_bulkHistory))
$routeC_bulkArguments = @($routeC_bulkPlan, $null)
Assert-ResumeTrue $routeC_coordinatorType.GetMethod('TryBegin').Invoke(
    $routeC_bulkCoordinator,
    $routeC_bulkArguments) `
    'ASSERT client-bulk-begin: valid 256-frame upload was rejected.'
Assert-ResumeEqual 256 $routeC_bulkCoordinator.PendingUploadCount `
    'ASSERT client-bulk-pending: full upload was not retained for pumping.'
for ($routeC_index = 0; $routeC_index -lt 64; $routeC_index++)
{
    $routeC_nextUploadArguments = @($null)
    Assert-ResumeTrue $routeC_tryTakeUpload.Invoke(
        $routeC_bulkCoordinator,
        $routeC_nextUploadArguments) `
        "ASSERT client-bulk-take-${routeC_index}: pending upload ended early."
}
Assert-ResumeEqual 192 $routeC_bulkCoordinator.PendingUploadCount `
    'RED: one client worker round consumed more than 64 resume uploads.'
Assert-ResumeTrue (-not $routeC_bulkCoordinator.IsCompleteReady) `
    'ASSERT client-bulk-complete: partial upload unlocked completion.'

Write-Output 'PASS: Task 9 Slice 2 client probe/state boundary, retries, counters, and secret-safe diagnostics are correct.'
Write-Output 'PASS: Task 9 Slice 5 client frozen plan, ordered progress, completion, and missing history gate are correct.'
