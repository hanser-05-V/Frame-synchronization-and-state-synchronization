param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath
)

$ErrorActionPreference = 'Stop'

function Assert-RawTrue
{
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-RawEqual
{
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual)
    {
        throw "$Message Expected=$Expected Actual=$Actual"
    }
}

function New-RawSession
{
    param([Type]$SessionType, [UInt64]$High, [UInt64]$Low)
    return [Activator]::CreateInstance($SessionType, @($High, $Low))
}

function New-RawMatch
{
    param(
        [Type]$MatchType,
        [Type]$SessionType,
        [object[]]$Candidates,
        [int]$WindowSize = 6)
    $routeC_queue = [Collections.Queue]::new()
    foreach ($routeC_candidate in $Candidates) { $routeC_queue.Enqueue($routeC_candidate) }
    $routeC_factoryType = [Func``1].MakeGenericType(@($SessionType))
    $routeC_script = { return $routeC_queue.Dequeue() }.GetNewClosure()
    $routeC_factory = [Management.Automation.LanguagePrimitives]::ConvertTo(
        $routeC_script,
        $routeC_factoryType)
    return [Activator]::CreateInstance($MatchType, @($WindowSize, $routeC_factory))
}

function New-RawNonce
{
    param([byte]$Start)
    $routeC_nonce = [byte[]]::new(16)
    for ($routeC_index = 0; $routeC_index -lt 16; $routeC_index++)
    {
        $routeC_nonce[$routeC_index] = [byte]($Start + $routeC_index)
    }
    return $routeC_nonce
}

function New-RawHelloDatagram
{
    param($Types, [byte[]]$Nonce)
    $routeC_payload = $Types.RawCodec.GetMethod('EncodeHello').Invoke(
        $null,
        @(,$Nonce))
    $routeC_type = [Enum]::Parse($Types.MessageType, 'RawHello')
    $routeC_zero = $Types.SessionId.GetField('Zero').GetValue($null)
    return $Types.EnvelopeCodec.GetMethod('Encode').Invoke(
        $null,
        @($routeC_type, $routeC_zero, [uint32]0, $routeC_payload))
}

function Invoke-RawProcess
{
    param($Match, [Net.IPEndPoint]$Endpoint, [byte[]]$Datagram)
    $routeC_parameterTypes = [Type[]]@([Net.IPEndPoint], [byte[]])
    return $Match.GetType().GetMethod('Process', $routeC_parameterTypes).Invoke(
        $Match,
        @($Endpoint, $Datagram))
}

function Decode-RawMessage
{
    param($Types, [byte[]]$Datagram)
    $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
    $routeC_arguments = @($Datagram, $Datagram.Length, $null, $routeC_reason)
    $routeC_ok = $Types.EnvelopeCodec.GetMethod('TryDecode').Invoke(
        $null,
        $routeC_arguments)
    Assert-RawTrue $routeC_ok 'Server action was not a valid Route C envelope.'
    return $routeC_arguments[2]
}

function Decode-RawWelcome
{
    param($Types, $Message)
    $routeC_arguments = @($Message.Payload, $null, [byte]0, [byte]0)
    $routeC_ok = $Types.RawCodec.GetMethod('TryDecodeWelcome').Invoke(
        $null,
        $routeC_arguments)
    Assert-RawTrue $routeC_ok 'Welcome payload was invalid.'
    return @($routeC_arguments[2], $routeC_arguments[3])
}

function Decode-RawFaultReason
{
    param($Types, $Message)
    $routeC_fault = [Activator]::CreateInstance($Types.Fault)
    $routeC_arguments = @($Message.Payload, $routeC_fault)
    $routeC_ok = $Types.RawCodec.GetMethod('TryDecodeFault').Invoke(
        $null,
        $routeC_arguments)
    Assert-RawTrue $routeC_ok 'Fault payload was invalid.'
    return $routeC_arguments[1].Reason.ToString()
}

function Decode-RawFault
{
    param($Types, $Message)
    $routeC_fault = [Activator]::CreateInstance($Types.Fault)
    $routeC_arguments = @($Message.Payload, $routeC_fault)
    $routeC_ok = $Types.RawCodec.GetMethod('TryDecodeFault').Invoke(
        $null,
        $routeC_arguments)
    Assert-RawTrue $routeC_ok 'Fault payload was invalid.'
    return $routeC_arguments[1]
}

function Decode-RawInput
{
    param($Types, $Message, [byte]$WindowSize)
    $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
    $routeC_arguments = @($Message.Payload, $WindowSize, $null, $routeC_reason)
    $routeC_ok = $Types.RawCodec.GetMethod('TryDecodeInput').Invoke(
        $null,
        $routeC_arguments)
    Assert-RawTrue $routeC_ok 'Relayed RawInput payload was invalid.'
    return $routeC_arguments[2]
}

function New-RawEnvelope
{
    param($Types, [string]$TypeName, $Session, [uint32]$Generation, [byte[]]$Payload)
    $routeC_type = [Enum]::Parse($Types.MessageType, $TypeName)
    return $Types.EnvelopeCodec.GetMethod('Encode').Invoke(
        $null,
        @($routeC_type, $Session, $Generation, $Payload))
}

function New-RawReadyDatagram
{
    param($Types, $Session)
    $routeC_payload = $Types.RawCodec.GetMethod('EncodeReady').Invoke($null, @())
    return New-RawEnvelope $Types 'RawReady' $Session 1 $routeC_payload
}

function New-RawFaultDatagram
{
    param(
        $Types,
        $Session,
        [string]$Reason,
        [byte]$PlayerIndex,
        [int]$FrameID,
        [int]$ObservedLatestFrameID)
    $routeC_reason = [Enum]::Parse(
        $routeC_assembly.GetType('FrameSyncDemo.RawUdpFaultReason', $true),
        $Reason)
    $routeC_fault = [Activator]::CreateInstance(
        $Types.Fault,
        @($routeC_reason, $PlayerIndex, $FrameID, $ObservedLatestFrameID))
    $routeC_payload = $Types.RawCodec.GetMethod('EncodeFault').Invoke(
        $null,
        @($routeC_fault))
    return New-RawEnvelope $Types 'RawFault' $Session 1 $routeC_payload
}

function New-RawInputDatagram
{
    param(
        $Types,
        $Session,
        [byte]$PlayerIndex,
        [uint32]$Sequence,
        [int]$FrameID,
        [uint32]$Raw,
        [byte]$WindowSize = 6)
    $routeC_count = [Math]::Min([int]$WindowSize, $FrameID + 1)
    $routeC_entries = [Array]::CreateInstance($Types.InputEntry, $routeC_count)
    $routeC_first = $FrameID - $routeC_count + 1
    for ($routeC_index = 0; $routeC_index -lt $routeC_count; $routeC_index++)
    {
        $routeC_entryFrame = $routeC_first + $routeC_index
        $routeC_entryRaw = if ($routeC_entryFrame -eq $FrameID)
        {
            $Raw
        }
        else
        {
            [uint32](1000 + $routeC_entryFrame)
        }
        $routeC_entry = [Activator]::CreateInstance(
            $Types.InputEntry,
            @($routeC_entryRaw, $routeC_entryFrame))
        $routeC_entries.SetValue($routeC_entry, $routeC_index)
    }
    $routeC_payload = $Types.RawCodec.GetMethod('EncodeInput').Invoke(
        $null,
        @($PlayerIndex, $WindowSize, $Sequence, $FrameID, $routeC_entries))
    return New-RawEnvelope $Types 'RawInput' $Session 1 $routeC_payload
}

Assert-RawTrue (Test-Path -LiteralPath $ServerPath -PathType Leaf) 'Server assembly is missing.'
$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_types = [pscustomobject]@{
    Match = $routeC_assembly.GetType('FrameSyncServer.RawUdpServerMatch', $false)
    SessionId = $routeC_assembly.GetType('FrameSyncDemo.RouteCSessionId', $true)
    MessageType = $routeC_assembly.GetType('FrameSyncDemo.RouteCMessageType', $true)
    DropReason = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolDropReason', $true)
    Fault = $routeC_assembly.GetType('FrameSyncDemo.RawUdpFault', $false)
    EnvelopeCodec = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolCodec', $true)
    RawCodec = $routeC_assembly.GetType('FrameSyncDemo.RawUdpProtocolCodec', $false)
    InputEntry = $routeC_assembly.GetType('FrameSyncDemo.RawUdpInputEntry', $false)
}
Assert-RawTrue ($null -ne $routeC_types.Match) 'ASSERT first-two-slots: RawUdpServerMatch is missing.'
Assert-RawTrue ($null -ne $routeC_types.Fault) 'ASSERT match-full: RawUdpFault is missing.'
Assert-RawTrue ($null -ne $routeC_types.RawCodec) 'ASSERT raw-codec-link: RawUdpProtocolCodec is missing.'

$routeC_zero = New-RawSession $routeC_types.SessionId 0 0
$routeC_session0 = New-RawSession $routeC_types.SessionId 1 2
$routeC_session1 = New-RawSession $routeC_types.SessionId 3 4
$routeC_nonce0 = New-RawNonce 16
$routeC_nonce1 = New-RawNonce 48
$routeC_nonce2 = New-RawNonce 80
$routeC_endpoint0 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 10001)
$routeC_endpoint1 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 10002)
$routeC_endpoint2 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 10003)

function New-RawFixture
{
    param([bool]$Running, [int]$WindowSize = 1)
    $routeC_match = New-RawMatch `
        $routeC_types.Match `
        $routeC_types.SessionId `
        @($routeC_session0, $routeC_session1) `
        $WindowSize
    $routeC_first = Invoke-RawProcess `
        $routeC_match `
        $routeC_endpoint0 `
        (New-RawHelloDatagram $routeC_types $routeC_nonce0)
    $routeC_second = Invoke-RawProcess `
        $routeC_match `
        $routeC_endpoint1 `
        (New-RawHelloDatagram $routeC_types $routeC_nonce1)
    $routeC_boundSession0 = (Decode-RawMessage `
        $routeC_types `
        $routeC_first.Actions[0].Datagram).SessionId
    $routeC_boundSession1 = (Decode-RawMessage `
        $routeC_types `
        $routeC_second.Actions[0].Datagram).SessionId
    if ($Running)
    {
        Invoke-RawProcess `
            $routeC_match `
            $routeC_endpoint0 `
            (New-RawReadyDatagram $routeC_types $routeC_boundSession0) | Out-Null
        Invoke-RawProcess `
            $routeC_match `
            $routeC_endpoint1 `
            (New-RawReadyDatagram $routeC_types $routeC_boundSession1) | Out-Null
    }
    return [pscustomobject]@{
        Match = $routeC_match
        Session0 = $routeC_boundSession0
        Session1 = $routeC_boundSession1
    }
}

$routeC_match = New-RawMatch `
    $routeC_types.Match `
    $routeC_types.SessionId `
    @($routeC_zero, $routeC_session0, $routeC_session0, $routeC_session1)
$routeC_first = Invoke-RawProcess `
    $routeC_match `
    $routeC_endpoint0 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce0)
Assert-RawEqual 1 $routeC_first.Actions.Length 'ASSERT first-slot: expected one Welcome.'
$routeC_firstBytes = $routeC_first.Actions[0].Datagram
$routeC_firstMessage = Decode-RawMessage $routeC_types $routeC_firstBytes
$routeC_firstWelcome = Decode-RawWelcome $routeC_types $routeC_firstMessage
Assert-RawEqual 0 $routeC_firstWelcome[0] 'ASSERT first-slot: player index changed.'
Assert-RawTrue ($routeC_firstMessage.SessionId.Equals($routeC_session0)) 'ASSERT zero-retry: zero SessionID was accepted.'

$routeC_second = Invoke-RawProcess `
    $routeC_match `
    $routeC_endpoint1 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce1)
Assert-RawEqual 1 $routeC_second.Actions.Length 'ASSERT second-slot: expected one Welcome.'
$routeC_secondMessage = Decode-RawMessage $routeC_types $routeC_second.Actions[0].Datagram
$routeC_secondWelcome = Decode-RawWelcome $routeC_types $routeC_secondMessage
Assert-RawEqual 1 $routeC_secondWelcome[0] 'ASSERT second-slot: player index changed.'
Assert-RawTrue ($routeC_secondMessage.SessionId.Equals($routeC_session1)) 'ASSERT collision-retry: duplicate SessionID was accepted.'

$routeC_duplicate = Invoke-RawProcess `
    $routeC_match `
    $routeC_endpoint0 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce0)
Assert-RawEqual 1 $routeC_duplicate.Actions.Length 'ASSERT duplicate-hello: Welcome was not resent.'
Assert-RawTrue `
    ([Linq.Enumerable]::SequenceEqual[byte]($routeC_firstBytes, $routeC_duplicate.Actions[0].Datagram)) `
    'ASSERT duplicate-hello: Welcome bytes changed.'

$routeC_nonceMoved = Invoke-RawProcess `
    $routeC_match `
    $routeC_endpoint2 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce0)
Assert-RawTrue (-not $routeC_nonceMoved.IsTerminal) 'ASSERT nonce-new-endpoint: external traffic killed match.'
Assert-RawEqual 0 $routeC_nonceMoved.Actions.Length 'ASSERT nonce-new-endpoint: response must be silent.'

$routeC_full1 = Invoke-RawProcess `
    $routeC_match `
    $routeC_endpoint2 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce2)
$routeC_full2 = Invoke-RawProcess `
    $routeC_match `
    $routeC_endpoint2 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce2)
foreach ($routeC_full in @($routeC_full1, $routeC_full2))
{
    Assert-RawEqual 1 $routeC_full.Actions.Length 'ASSERT match-full: each Hello needs one response.'
    $routeC_fullMessage = Decode-RawMessage $routeC_types $routeC_full.Actions[0].Datagram
    Assert-RawTrue $routeC_fullMessage.SessionId.IsZero 'ASSERT match-full: SessionID must be zero.'
    Assert-RawEqual 0 $routeC_fullMessage.Generation 'ASSERT match-full: Generation must be zero.'
    Assert-RawEqual 'MatchFull' (Decode-RawFaultReason $routeC_types $routeC_fullMessage) 'ASSERT match-full: wrong fault reason.'
}

$routeC_endpointConflictMatch = New-RawMatch `
    $routeC_types.Match `
    $routeC_types.SessionId `
    @($routeC_session0)
Invoke-RawProcess `
    $routeC_endpointConflictMatch `
    $routeC_endpoint0 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce0) | Out-Null
$routeC_endpointConflict = Invoke-RawProcess `
    $routeC_endpointConflictMatch `
    $routeC_endpoint0 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce1)
Assert-RawTrue $routeC_endpointConflict.IsTerminal 'ASSERT endpoint-new-nonce: expected terminal fault.'
Assert-RawEqual 'ProtocolViolation' $routeC_endpointConflict.FaultReason.ToString() 'ASSERT endpoint-new-nonce: wrong fault.'

$routeC_barrierMatch = New-RawMatch `
    $routeC_types.Match `
    $routeC_types.SessionId `
    @($routeC_session0, $routeC_session1)
$routeC_barrierFirst = Invoke-RawProcess `
    $routeC_barrierMatch `
    $routeC_endpoint0 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce0)
$routeC_barrierSession0 = (Decode-RawMessage `
    $routeC_types `
    $routeC_barrierFirst.Actions[0].Datagram).SessionId
$routeC_barrierSecond = Invoke-RawProcess `
    $routeC_barrierMatch `
    $routeC_endpoint1 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce1)
$routeC_barrierSession1 = (Decode-RawMessage `
    $routeC_types `
    $routeC_barrierSecond.Actions[0].Datagram).SessionId

$routeC_ready0 = Invoke-RawProcess `
    $routeC_barrierMatch `
    $routeC_endpoint0 `
    (New-RawReadyDatagram $routeC_types $routeC_barrierSession0)
Assert-RawTrue (-not $routeC_ready0.IsTerminal) 'ASSERT first-ready: single Ready terminated match.'
Assert-RawEqual 0 $routeC_ready0.Actions.Length 'ASSERT first-ready: Start sent before both Ready.'
$routeC_ready0Duplicate = Invoke-RawProcess `
    $routeC_barrierMatch `
    $routeC_endpoint0 `
    (New-RawReadyDatagram $routeC_types $routeC_barrierSession0)
Assert-RawTrue (-not $routeC_ready0Duplicate.IsTerminal) 'ASSERT duplicate-ready: duplicate terminated match.'

$routeC_preStartMatch = New-RawMatch `
    $routeC_types.Match `
    $routeC_types.SessionId `
    @($routeC_session0)
$routeC_preStartHello = Invoke-RawProcess `
    $routeC_preStartMatch `
    $routeC_endpoint0 `
    (New-RawHelloDatagram $routeC_types $routeC_nonce0)
$routeC_preStartSession = (Decode-RawMessage `
    $routeC_types `
    $routeC_preStartHello.Actions[0].Datagram).SessionId
$routeC_preStart = Invoke-RawProcess `
    $routeC_preStartMatch `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_preStartSession 0 0 0 10)
Assert-RawTrue $routeC_preStart.IsTerminal 'ASSERT pre-start-input: bound input did not terminate.'
Assert-RawEqual 'ProtocolViolation' $routeC_preStart.FaultReason.ToString() 'ASSERT pre-start-input: wrong fault.'

$routeC_ready1 = Invoke-RawProcess `
    $routeC_barrierMatch `
    $routeC_endpoint1 `
    (New-RawReadyDatagram $routeC_types $routeC_barrierSession1)
Assert-RawTrue (-not $routeC_ready1.IsTerminal) 'ASSERT second-ready: valid Ready terminated match.'
Assert-RawEqual 2 $routeC_ready1.Actions.Length 'ASSERT second-ready: both players need Start.'
foreach ($routeC_startAction in $routeC_ready1.Actions)
{
    $routeC_startMessage = Decode-RawMessage $routeC_types $routeC_startAction.Datagram
    Assert-RawEqual 'RawStart' $routeC_startMessage.MessageType.ToString() 'ASSERT start-type: wrong message.'
    $routeC_startArguments = @($routeC_startMessage.Payload, [int]0)
    $routeC_startOk = $routeC_types.RawCodec.GetMethod('TryDecodeStart').Invoke(
        $null,
        $routeC_startArguments)
    Assert-RawTrue $routeC_startOk 'ASSERT start-frame: invalid Start payload.'
    Assert-RawEqual 0 $routeC_startArguments[1] 'ASSERT start-frame: Start must be frame zero.'
}
Assert-RawTrue $routeC_barrierMatch.RelayEnabled 'ASSERT relay-enabled: both Ready did not open relay.'

$routeC_startRetries = $routeC_barrierMatch.CreateStartRetries()
Assert-RawEqual 2 $routeC_startRetries.Length 'ASSERT start-retry: both players still need Start.'
$routeC_frame0Player0 = Invoke-RawProcess `
    $routeC_barrierMatch `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_barrierSession0 0 0 0 10)
Assert-RawTrue (-not $routeC_frame0Player0.IsTerminal) 'ASSERT player0-frame0: valid input terminated.'
Assert-RawEqual 2 $routeC_frame0Player0.AcceptedWorkItems 'ASSERT player0-frame0: accepted input and rebuilt datagram were not both budgeted.'
$routeC_startRetries = $routeC_barrierMatch.CreateStartRetries()
Assert-RawEqual 1 $routeC_startRetries.Length 'ASSERT player0-start-stop: wrong retry count.'
Assert-RawEqual 10002 $routeC_startRetries[0].Endpoint.Port 'ASSERT player0-start-stop: retry targets wrong player.'

$routeC_frame0Player1 = Invoke-RawProcess `
    $routeC_barrierMatch `
    $routeC_endpoint1 `
    (New-RawInputDatagram $routeC_types $routeC_barrierSession1 1 0 0 20)
Assert-RawTrue (-not $routeC_frame0Player1.IsTerminal) 'ASSERT player1-frame0: valid input terminated.'
Assert-RawEqual 0 $routeC_barrierMatch.CreateStartRetries().Length 'ASSERT player1-start-stop: retries did not stop.'

$routeC_identity = New-RawFixture $false 1
$routeC_unknownSession = New-RawSession $routeC_types.SessionId 99 100
$routeC_unknown = Invoke-RawProcess `
    $routeC_identity.Match `
    $routeC_endpoint0 `
    (New-RawReadyDatagram $routeC_types $routeC_unknownSession)
Assert-RawTrue (-not $routeC_unknown.IsTerminal) 'ASSERT unknown-session: traffic killed match.'
Assert-RawEqual 'SessionNotFound' $routeC_unknown.DropReason.ToString() 'ASSERT unknown-session: wrong drop.'

$routeC_wrongEndpoint = Invoke-RawProcess `
    $routeC_identity.Match `
    $routeC_endpoint2 `
    (New-RawReadyDatagram $routeC_types $routeC_identity.Session0)
Assert-RawTrue (-not $routeC_wrongEndpoint.IsTerminal) 'ASSERT wrong-endpoint: traffic killed match.'
Assert-RawEqual 'WrongEndpoint' $routeC_wrongEndpoint.DropReason.ToString() 'ASSERT wrong-endpoint: wrong drop.'

$routeC_stale = Invoke-RawProcess `
    $routeC_identity.Match `
    $routeC_endpoint0 `
    (New-RawEnvelope $routeC_types 'RawReady' $routeC_identity.Session0 2 ([byte[]]::new(0)))
Assert-RawTrue (-not $routeC_stale.IsTerminal) 'ASSERT stale-generation: traffic killed match.'
Assert-RawEqual 'StaleGeneration' $routeC_stale.DropReason.ToString() 'ASSERT stale-generation: wrong drop.'

$routeC_badOuter = New-RawFixture $false 1
$routeC_validReady = New-RawReadyDatagram $routeC_types $routeC_badOuter.Session0
foreach ($routeC_mutation in @('magic', 'version', 'type', 'length'))
{
    $routeC_bad = [byte[]]$routeC_validReady.Clone()
    switch ($routeC_mutation)
    {
        'magic' { $routeC_bad[0] = 0 }
        'version' { $routeC_bad[4] = 2 }
        'type' { $routeC_bad[5] = 19 }
        'length' { $routeC_bad[7] = 1 }
    }
    $routeC_badResult = Invoke-RawProcess `
        $routeC_badOuter.Match `
        $routeC_endpoint0 `
        $routeC_bad
    Assert-RawTrue `
        (-not $routeC_badResult.IsTerminal) `
        "ASSERT bad-outer-${routeC_mutation}: bound garbage killed match."
    Assert-RawEqual 0 $routeC_badResult.Actions.Length "ASSERT bad-outer-${routeC_mutation}: garbage got reply."
}

$routeC_wrongDirection = New-RawFixture $false 1
$routeC_welcomeArguments = [object[]]::new(3)
$routeC_welcomeArguments[0] = [byte[]]$routeC_nonce0
$routeC_welcomeArguments[1] = [byte]0
$routeC_welcomeArguments[2] = [byte]1
$routeC_wrongDirectionPayload = $routeC_types.RawCodec.GetMethod('EncodeWelcome').Invoke(
    $null,
    $routeC_welcomeArguments)
$routeC_wrongDirectionResult = Invoke-RawProcess `
    $routeC_wrongDirection.Match `
    $routeC_endpoint0 `
    (New-RawEnvelope $routeC_types 'RawWelcome' $routeC_wrongDirection.Session0 1 $routeC_wrongDirectionPayload)
Assert-RawTrue $routeC_wrongDirectionResult.IsTerminal 'ASSERT wrong-direction: bound violation was not terminal.'
Assert-RawEqual 'ProtocolViolation' $routeC_wrongDirectionResult.FaultReason.ToString() 'ASSERT wrong-direction: wrong fault.'

$routeC_wrongPlayer = New-RawFixture $true 1
$routeC_wrongPlayerResult = Invoke-RawProcess `
    $routeC_wrongPlayer.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_wrongPlayer.Session0 1 1 0 10 1)
Assert-RawTrue $routeC_wrongPlayerResult.IsTerminal 'ASSERT wrong-player: bound violation was not terminal.'
Assert-RawEqual 'ProtocolViolation' $routeC_wrongPlayerResult.FaultReason.ToString() 'ASSERT wrong-player: wrong fault.'

$routeC_unboundOversized = New-RawFixture $false 1
$routeC_oversized = [byte[]]::new(1201)
$routeC_unboundOversizedResult = Invoke-RawProcess `
    $routeC_unboundOversized.Match `
    $routeC_endpoint2 `
    $routeC_oversized
Assert-RawTrue (-not $routeC_unboundOversizedResult.IsTerminal) 'ASSERT unbound-1201: garbage killed match.'
Assert-RawEqual 'RawDatagramTooLarge' $routeC_unboundOversizedResult.DropReason.ToString() 'ASSERT unbound-1201: wrong drop.'
$routeC_boundOversized = Invoke-RawProcess `
    $routeC_unboundOversized.Match `
    $routeC_endpoint0 `
    $routeC_oversized
Assert-RawTrue $routeC_boundOversized.IsTerminal 'ASSERT bound-1201: violation was not terminal.'

$routeC_packetOld = New-RawFixture $true 1
Invoke-RawProcess `
    $routeC_packetOld.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_packetOld.Session0 0 100 0 10 1) | Out-Null
$routeC_packetOldResult = Invoke-RawProcess `
    $routeC_packetOld.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_packetOld.Session0 0 36 0 10 1)
Assert-RawTrue (-not $routeC_packetOldResult.IsTerminal) 'ASSERT packet-too-old: old packet killed match.'
Assert-RawEqual 'PacketTooOld' $routeC_packetOldResult.DropReason.ToString() 'ASSERT packet-too-old: wrong drop.'

$routeC_sequenceConflict = New-RawFixture $true 1
Invoke-RawProcess `
    $routeC_sequenceConflict.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_sequenceConflict.Session0 0 7 0 10 1) | Out-Null
$routeC_sequenceConflictResult = Invoke-RawProcess `
    $routeC_sequenceConflict.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_sequenceConflict.Session0 0 7 0 11 1)
Assert-RawTrue $routeC_sequenceConflictResult.IsTerminal 'ASSERT sequence-conflict: conflict was not terminal.'
Assert-RawEqual 'ProtocolViolation' $routeC_sequenceConflictResult.FaultReason.ToString() 'ASSERT sequence-conflict: wrong fault.'

$routeC_frameConflict = New-RawFixture $true 1
Invoke-RawProcess `
    $routeC_frameConflict.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_frameConflict.Session0 0 1 0 10 1) | Out-Null
$routeC_frameConflictResult = Invoke-RawProcess `
    $routeC_frameConflict.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_frameConflict.Session0 0 2 0 11 1)
Assert-RawTrue $routeC_frameConflictResult.IsTerminal 'ASSERT frame-conflict: conflict was not terminal.'
Assert-RawEqual 'ConflictingInput' $routeC_frameConflictResult.FaultReason.ToString() 'ASSERT frame-conflict: exact reason lost.'

$routeC_capacity = New-RawFixture $true 1
Invoke-RawProcess `
    $routeC_capacity.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_capacity.Session0 0 1 0 10 1) | Out-Null
$routeC_capacityResult = Invoke-RawProcess `
    $routeC_capacity.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_capacity.Session0 0 2 257 267 1)
Assert-RawTrue $routeC_capacityResult.IsTerminal 'ASSERT capacity: overflow was not terminal.'
Assert-RawEqual 'CapacityExceeded' $routeC_capacityResult.FaultReason.ToString() 'ASSERT capacity: exact reason lost.'

$routeC_rawOversizedUnbound = New-RawFixture $false 1
$routeC_rawOversizedPayload = [byte[]]::new(148)
$routeC_rawOversizedPayload[0] = 0
$routeC_rawOversizedPayload[1] = 17
$routeC_rawOversizedDatagram = New-RawEnvelope `
    $routeC_types `
    'RawInput' `
    $routeC_rawOversizedUnbound.Session0 `
    1 `
    $routeC_rawOversizedPayload
$routeC_rawOversizedDrop = Invoke-RawProcess `
    $routeC_rawOversizedUnbound.Match `
    $routeC_endpoint2 `
    $routeC_rawOversizedDatagram
Assert-RawTrue (-not $routeC_rawOversizedDrop.IsTerminal) 'ASSERT unbound-raw-168: garbage killed match.'
Assert-RawEqual 'RawDatagramTooLarge' $routeC_rawOversizedDrop.DropReason.ToString() 'ASSERT unbound-raw-168: wrong drop.'
$routeC_rawOversizedBound = Invoke-RawProcess `
    $routeC_rawOversizedUnbound.Match `
    $routeC_endpoint0 `
    $routeC_rawOversizedDatagram
Assert-RawTrue $routeC_rawOversizedBound.IsTerminal 'ASSERT bound-raw-168: violation was not terminal.'
Assert-RawEqual 'ProtocolViolation' $routeC_rawOversizedBound.FaultReason.ToString() 'ASSERT bound-raw-168: wrong fault.'

$routeC_ambiguous = New-RawFixture $true 1
Invoke-RawProcess `
    $routeC_ambiguous.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_ambiguous.Session0 0 0 0 10 1) | Out-Null
$routeC_ambiguousResult = Invoke-RawProcess `
    $routeC_ambiguous.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_ambiguous.Session0 0 2147483648 1 11 1)
Assert-RawTrue $routeC_ambiguousResult.IsTerminal 'ASSERT sequence-ambiguous: ambiguity was not terminal.'
Assert-RawEqual 'ProtocolViolation' $routeC_ambiguousResult.FaultReason.ToString() 'ASSERT sequence-ambiguous: wrong fault.'

$routeC_gap = New-RawFixture $true 1
$routeC_gapResult = $null
for ($routeC_frame = 1; $routeC_frame -le 17; $routeC_frame++)
{
    $routeC_gapResult = Invoke-RawProcess `
        $routeC_gap.Match `
        $routeC_endpoint0 `
        (New-RawInputDatagram `
            $routeC_types `
            $routeC_gap.Session0 `
            0 `
            ([uint32]$routeC_frame) `
            $routeC_frame `
            ([uint32](100 + $routeC_frame)) `
            1)
}
Assert-RawTrue $routeC_gapResult.IsTerminal 'ASSERT gap-grace: evidence plus 16 newer unique sequences did not terminate.'
Assert-RawEqual 'UnrecoverableInputGap' $routeC_gapResult.FaultReason.ToString() 'ASSERT gap-grace: exact reason lost.'

$routeC_inputOld = New-RawFixture $true 1
for ($routeC_frame = 0; $routeC_frame -le 256; $routeC_frame++)
{
    Invoke-RawProcess `
        $routeC_inputOld.Match `
        $routeC_endpoint0 `
        (New-RawInputDatagram `
            $routeC_types `
            $routeC_inputOld.Session0 `
            0 `
            ([uint32]$routeC_frame) `
            $routeC_frame `
            ([uint32](2000 + $routeC_frame)) `
            1) | Out-Null
}
Invoke-RawProcess `
    $routeC_inputOld.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_inputOld.Session0 0 1000 256 2256 1) | Out-Null
$routeC_inputOldResult = Invoke-RawProcess `
    $routeC_inputOld.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_inputOld.Session0 0 999 0 2000 1)
Assert-RawTrue (-not $routeC_inputOldResult.IsTerminal) 'ASSERT input-too-old: old frame killed match.'
Assert-RawEqual 'InputTooOld' $routeC_inputOldResult.DropReason.ToString() 'ASSERT input-too-old: wrong drop.'

$routeC_relay = New-RawFixture $true 6
$routeC_relayFrame0 = New-RawInputDatagram `
    $routeC_types $routeC_relay.Session0 0 0 0 1000 6
$routeC_relayResult0 = Invoke-RawProcess `
    $routeC_relay.Match $routeC_endpoint0 $routeC_relayFrame0
Assert-RawEqual 1 $routeC_relayResult0.Actions.Length 'ASSERT normal-relay: one upstream sequence must create one downlink.'
Assert-RawEqual 10002 $routeC_relayResult0.Actions[0].Endpoint.Port 'ASSERT normal-relay: relay targeted wrong peer.'
$routeC_relayMessage0 = Decode-RawMessage $routeC_types $routeC_relayResult0.Actions[0].Datagram
Assert-RawTrue ($routeC_relayMessage0.SessionId.Equals($routeC_relay.Session1)) 'ASSERT relay-session: receiver SessionID was not used.'
Assert-RawEqual 1 $routeC_relayMessage0.Generation 'ASSERT relay-generation: generation changed.'
$routeC_relayWindow0 = Decode-RawInput $routeC_types $routeC_relayMessage0 6
Assert-RawEqual 0 $routeC_relayWindow0.PlayerIndex 'ASSERT relay-player: original payload player was not preserved.'
Assert-RawEqual 0 $routeC_relayWindow0.PacketSequence 'ASSERT relay-sequence: receiver sequence did not start independently at zero.'
Assert-RawEqual 1000 $routeC_relayWindow0.Entries[0].Raw 'ASSERT relay-raw: rebuilt first raw changed.'

$routeC_duplicateRelay = Invoke-RawProcess `
    $routeC_relay.Match $routeC_endpoint0 $routeC_relayFrame0
Assert-RawEqual 0 $routeC_duplicateRelay.Actions.Length 'ASSERT duplicate-sequence: duplicate produced a relay.'
$routeC_tailDatagram = New-RawInputDatagram `
    $routeC_types $routeC_relay.Session0 0 1 0 1000 6
$routeC_tailResult = Invoke-RawProcess `
    $routeC_relay.Match $routeC_endpoint0 $routeC_tailDatagram
Assert-RawEqual 1 $routeC_tailResult.Actions.Length 'ASSERT tail-relay: new sequence with known window was not relayed once.'
$routeC_tailMessage = Decode-RawMessage $routeC_types $routeC_tailResult.Actions[0].Datagram
$routeC_tailWindow = Decode-RawInput $routeC_types $routeC_tailMessage 6
Assert-RawEqual 1 $routeC_tailWindow.PacketSequence 'ASSERT tail-sequence: fresh receiver sequence missing.'

$routeC_reverse = Invoke-RawProcess `
    $routeC_relay.Match `
    $routeC_endpoint1 `
    (New-RawInputDatagram $routeC_types $routeC_relay.Session1 1 99 0 2000 6)
Assert-RawEqual 1 $routeC_reverse.Actions.Length 'ASSERT reverse-relay: reverse direction was not relayed.'
$routeC_reverseMessage = Decode-RawMessage $routeC_types $routeC_reverse.Actions[0].Datagram
$routeC_reverseWindow = Decode-RawInput $routeC_types $routeC_reverseMessage 6
Assert-RawTrue ($routeC_reverseMessage.SessionId.Equals($routeC_relay.Session0)) 'ASSERT reverse-session: player zero SessionID missing.'
Assert-RawEqual 0 $routeC_reverseWindow.PacketSequence 'ASSERT receiver-sequence: per-receiver sequences were not independent.'

$routeC_late = New-RawFixture $true 6
$routeC_frontResult = Invoke-RawProcess `
    $routeC_late.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_late.Session0 0 6 6 1006 6)
Assert-RawEqual 1 $routeC_frontResult.Actions.Length 'ASSERT late-setup: normal frontier should relay once.'
$routeC_lateResult = Invoke-RawProcess `
    $routeC_late.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_late.Session0 0 5 5 1005 6)
Assert-RawEqual 6 $routeC_lateResult.Actions.Length 'ASSERT late-recovery: missing frame did not receive exactly N bounded opportunities.'
for ($routeC_copy = 0; $routeC_copy -lt 6; $routeC_copy++)
{
    $routeC_lateMessage = Decode-RawMessage $routeC_types $routeC_lateResult.Actions[$routeC_copy].Datagram
    $routeC_lateWindow = Decode-RawInput $routeC_types $routeC_lateMessage 6
    Assert-RawTrue ($routeC_lateMessage.SessionId.Equals($routeC_late.Session1)) 'ASSERT late-session: receiver SessionID changed.'
    Assert-RawEqual 0 $routeC_lateWindow.PlayerIndex 'ASSERT late-player: original player changed.'
    Assert-RawEqual ([uint32](1 + $routeC_copy)) $routeC_lateWindow.PacketSequence 'ASSERT late-sequence: copies need fresh downlink sequences.'
    Assert-RawEqual 0 $routeC_lateWindow.Entries[0].FrameID 'ASSERT late-frame: recovered frame missing from rebuilt window.'
}

$routeC_historyBoundary = New-RawFixture $true 6
for ($routeC_frame = 0; $routeC_frame -le 44; $routeC_frame++)
{
    Invoke-RawProcess `
        $routeC_historyBoundary.Match `
        $routeC_endpoint0 `
        (New-RawInputDatagram `
            $routeC_types `
            $routeC_historyBoundary.Session0 `
            0 `
            ([uint32]$routeC_frame) `
            $routeC_frame `
            ([uint32](1000 + $routeC_frame)) `
            6) | Out-Null
}
Invoke-RawProcess `
    $routeC_historyBoundary.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram `
        $routeC_types `
        $routeC_historyBoundary.Session0 `
        0 `
        46 `
        300 `
        1300 `
        6) | Out-Null
$routeC_boundaryResult = Invoke-RawProcess `
    $routeC_historyBoundary.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram `
        $routeC_types `
        $routeC_historyBoundary.Session0 `
        0 `
        45 `
        45 `
        1045 `
        6)
Assert-RawTrue (-not $routeC_boundaryResult.IsTerminal) `
    'ASSERT history-boundary: legal delayed recovery terminated the match.'
Assert-RawTrue ($routeC_boundaryResult.Actions.Length -gt 0) `
    'ASSERT history-boundary: legal delayed recovery was not relayed.'

$routeC_conflictBroadcast = New-RawFixture $true 1
Invoke-RawProcess `
    $routeC_conflictBroadcast.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_conflictBroadcast.Session0 0 1 0 10 1) | Out-Null
$routeC_conflictBroadcastResult = Invoke-RawProcess `
    $routeC_conflictBroadcast.Match `
    $routeC_endpoint0 `
    (New-RawInputDatagram $routeC_types $routeC_conflictBroadcast.Session0 0 2 0 11 1)
Assert-RawEqual 2 $routeC_conflictBroadcastResult.Actions.Length 'ASSERT conflict-broadcast: terminal fault was not sent to both players.'
for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
{
    $routeC_faultMessage = Decode-RawMessage $routeC_types $routeC_conflictBroadcastResult.Actions[$routeC_player].Datagram
    $routeC_fault = Decode-RawFault $routeC_types $routeC_faultMessage
    Assert-RawEqual 'ConflictingInput' $routeC_fault.Reason.ToString() 'ASSERT conflict-broadcast: original reason changed.'
    Assert-RawEqual 0 $routeC_fault.PlayerIndex 'ASSERT conflict-broadcast: original player changed.'
    Assert-RawEqual 0 $routeC_fault.FrameID 'ASSERT conflict-broadcast: original frame changed.'
}

$routeC_gapBroadcast = New-RawFixture $true 1
$routeC_gapBroadcastResult = $null
for ($routeC_frame = 1; $routeC_frame -le 17; $routeC_frame++)
{
    $routeC_gapBroadcastResult = Invoke-RawProcess `
        $routeC_gapBroadcast.Match `
        $routeC_endpoint0 `
        (New-RawInputDatagram $routeC_types $routeC_gapBroadcast.Session0 0 ([uint32]$routeC_frame) $routeC_frame ([uint32](100 + $routeC_frame)) 1)
}
Assert-RawEqual 2 $routeC_gapBroadcastResult.Actions.Length 'ASSERT gap-broadcast: terminal gap fault was not sent to both players.'
foreach ($routeC_gapAction in $routeC_gapBroadcastResult.Actions)
{
    $routeC_gapFault = Decode-RawFault $routeC_types (Decode-RawMessage $routeC_types $routeC_gapAction.Datagram)
    Assert-RawEqual 'UnrecoverableInputGap' $routeC_gapFault.Reason.ToString() 'ASSERT gap-broadcast: original reason changed.'
    Assert-RawEqual 0 $routeC_gapFault.PlayerIndex 'ASSERT gap-broadcast: original player changed.'
    Assert-RawEqual 0 $routeC_gapFault.FrameID 'ASSERT gap-broadcast: original missing frame changed.'
}

$routeC_peerFault = New-RawFixture $true 1
$routeC_peerFaultDatagram = New-RawFaultDatagram `
    $routeC_types $routeC_peerFault.Session0 'ConflictingInput' 1 42 47
$routeC_peerFaultFirst = Invoke-RawProcess `
    $routeC_peerFault.Match $routeC_endpoint0 $routeC_peerFaultDatagram
Assert-RawEqual 2 $routeC_peerFaultFirst.Actions.Length 'ASSERT peer-fault: terminal fault was not broadcast.'
foreach ($routeC_faultAction in $routeC_peerFaultFirst.Actions)
{
    $routeC_preservedFault = Decode-RawFault $routeC_types (Decode-RawMessage $routeC_types $routeC_faultAction.Datagram)
    Assert-RawEqual 'ConflictingInput' $routeC_preservedFault.Reason.ToString() 'ASSERT peer-fault: reason was downgraded.'
    Assert-RawEqual 1 $routeC_preservedFault.PlayerIndex 'ASSERT peer-fault: player was not preserved.'
    Assert-RawEqual 42 $routeC_preservedFault.FrameID 'ASSERT peer-fault: frame was not preserved.'
}
$routeC_peerFaultRepeat = Invoke-RawProcess `
    $routeC_peerFault.Match $routeC_endpoint0 $routeC_peerFaultDatagram
Assert-RawTrue $routeC_peerFaultRepeat.IsTerminal 'ASSERT repeated-fault: terminal state was lost.'
Assert-RawEqual 'ConflictingInput' $routeC_peerFaultRepeat.FaultReason.ToString() 'ASSERT repeated-fault: reason was downgraded.'
Assert-RawEqual 0 $routeC_peerFaultRepeat.Actions.Length 'ASSERT repeated-fault: duplicate input triggered an unbounded immediate broadcast.'

Write-Output 'PASS: Raw UDP server protocol, immutable relay, late recovery, and terminal fault behavior are correct.'
