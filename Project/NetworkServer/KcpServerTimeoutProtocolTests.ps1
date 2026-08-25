param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath
)

$ErrorActionPreference = 'Stop'

function Assert-KcpTrue
{
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-KcpEqual
{
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) { throw "$Message Expected=$Expected Actual=$Actual" }
}

function New-KcpBytes
{
    param([byte]$Start, [int]$Count)
    $routeC_bytes = [byte[]]::new($Count)
    for ($routeC_index = 0; $routeC_index -lt $Count; $routeC_index++)
    {
        $routeC_bytes[$routeC_index] = [byte]($Start + $routeC_index)
    }
    return ,$routeC_bytes
}

function New-KcpFactory
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

function New-KcpMessage
{
    param($Types, [string]$TypeName, $Session, [byte[]]$Payload)
    $routeC_type = [Enum]::Parse($Types.MessageType, $TypeName)
    return [Activator]::CreateInstance(
        $Types.Message,
        @($routeC_type, $Session.SessionId, $Session.Generation, $Payload))
}

function Decode-KcpEnvelope
{
    param($Types, [byte[]]$Datagram)
    $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
    $routeC_arguments = @($Datagram, $Datagram.Length, $null, $routeC_reason)
    $routeC_ok = $Types.Codec.GetMethod('TryDecode').Invoke($null, $routeC_arguments)
    Assert-KcpTrue $routeC_ok 'ASSERT terminal-envelope: invalid Route C output.'
    return $routeC_arguments[2]
}

if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf))
{
    throw "Server assembly does not exist: $ServerPath"
}

$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_types = [pscustomobject]@{
    Router = $routeC_assembly.GetType('FrameSyncServer.ServerSessionRouter', $true)
    Diagnostics = $routeC_assembly.GetType('FrameSyncServer.KcpServerDiagnostics', $true)
    SessionId = $routeC_assembly.GetType('FrameSyncDemo.RouteCSessionId', $true)
    Message = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolMessage', $true)
    MessageType = $routeC_assembly.GetType('FrameSyncDemo.RouteCMessageType', $true)
    DropReason = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolDropReason', $true)
    Codec = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolCodec', $true)
}

$routeC_sessionId0 = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]501, [uint64]502))
$routeC_sessionId1 = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]601, [uint64]602))
$routeC_router = [Activator]::CreateInstance(
    $routeC_types.Router,
    @(
        ([Activator]::CreateInstance($routeC_types.Diagnostics)),
        (New-KcpFactory $routeC_types.SessionId @($routeC_sessionId0, $routeC_sessionId1)),
        (New-KcpFactory ([byte[]]) @((New-KcpBytes 31 32), (New-KcpBytes 81 32))),
        (New-KcpFactory ([uint32]) @([uint32]801, [uint32]802)),
        10))
$routeC_endpoint0 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 15001)
$routeC_endpoint1 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 15002)
$routeC_hello = $routeC_types.Router.GetMethod('HandleInitialHelloAt')
$routeC_hello0Arguments = @($routeC_endpoint0, (New-KcpBytes 1 16), [uint32]0, $null, $null)
$routeC_hello1Arguments = @($routeC_endpoint1, (New-KcpBytes 41 16), [uint32]0, $null, $null)
$routeC_hello.Invoke($routeC_router, $routeC_hello0Arguments) | Out-Null
$routeC_hello.Invoke($routeC_router, $routeC_hello1Arguments) | Out-Null
$routeC_session0 = $routeC_hello0Arguments[4]
$routeC_session1 = $routeC_hello1Arguments[4]
$routeC_readyPayload = [byte[]]$routeC_types.Codec.GetMethod('EncodeReady').Invoke($null, @(-1, 0, -1))
$routeC_handleReady = $routeC_types.Router.GetMethod('HandleReady')
$routeC_handleReady.Invoke($routeC_router, @((New-KcpMessage $routeC_types 'Ready' $routeC_session0 $routeC_readyPayload), $routeC_endpoint0, [uint32]0)) | Out-Null
$routeC_handleReady.Invoke($routeC_router, @((New-KcpMessage $routeC_types 'Ready' $routeC_session1 $routeC_readyPayload), $routeC_endpoint1, [uint32]0)) | Out-Null
$routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()) | Out-Null

$routeC_tick = $routeC_types.Router.GetMethod('Tick')
$routeC_tick.Invoke($routeC_router, @([uint32]2998, [int64]29980000))
Assert-KcpEqual 'Running' $routeC_session0.State.ToString() 'ASSERT before-timeout-player0: state changed early.'
Assert-KcpEqual 'Running' $routeC_session1.State.ToString() 'ASSERT before-timeout-player1: state changed early.'

$routeC_handleHeartbeat = $routeC_types.Router.GetMethod('HandleHeartbeat')
Assert-KcpTrue ($null -ne $routeC_handleHeartbeat) 'RED: HandleHeartbeat is missing.'
$routeC_empty = [byte[]]::new(0)
Assert-KcpTrue $routeC_handleHeartbeat.Invoke(
    $routeC_router,
    @((New-KcpMessage $routeC_types 'Heartbeat' $routeC_session0 $routeC_empty), $routeC_endpoint0, [uint32]2999)) `
    'ASSERT heartbeat-player0: valid Heartbeat was rejected.'
Assert-KcpTrue $routeC_handleHeartbeat.Invoke(
    $routeC_router,
    @((New-KcpMessage $routeC_types 'Heartbeat' $routeC_session1 $routeC_empty), $routeC_endpoint1, [uint32]2999)) `
    'ASSERT heartbeat-player1: valid Heartbeat was rejected.'

$routeC_handleDisconnect = $routeC_types.Router.GetMethod('HandleDisconnect')
Assert-KcpTrue ($null -ne $routeC_handleDisconnect) 'RED: HandleDisconnect is missing.'
$routeC_disconnectPayload = [byte[]]@(0, 1)
Assert-KcpTrue $routeC_handleDisconnect.Invoke(
    $routeC_router,
    @((New-KcpMessage $routeC_types 'Disconnect' $routeC_session0 $routeC_disconnectPayload), $routeC_endpoint0, [uint32]3000)) `
    'ASSERT disconnect: valid advisory was rejected.'
Assert-KcpEqual 'Running' $routeC_session0.State.ToString() 'ASSERT disconnect: advisory changed state.'

$routeC_tick.Invoke($routeC_router, @([uint32]5998, [int64]59980000))
Assert-KcpEqual 'Running' $routeC_session0.State.ToString() 'ASSERT refreshed-player0: state changed before 3000ms.'
Assert-KcpEqual 'Running' $routeC_session1.State.ToString() 'ASSERT refreshed-player1: state changed before 3000ms.'
$routeC_tick.Invoke($routeC_router, @([uint32]5999, [int64]59990000))
Assert-KcpEqual 'Running' $routeC_session0.State.ToString() 'ASSERT disconnect-refresh: advisory did not refresh activity.'
Assert-KcpEqual 'Reconnecting' $routeC_session1.State.ToString() 'ASSERT timeout-3000: player1 did not leave Running.'
Assert-KcpEqual 2 $routeC_router.ActiveSessionCount 'ASSERT timeout-retention: Session memory was discarded at 3000ms.'
$routeC_tick.Invoke($routeC_router, @([uint32]6000, [int64]60000000))
Assert-KcpEqual 'Reconnecting' $routeC_session0.State.ToString() 'ASSERT timeout-player0: did not leave Running at 3000ms.'

$routeC_tick.Invoke($routeC_router, @([uint32]7998, [int64]79980000))
Assert-KcpEqual 'Reconnecting' $routeC_session0.State.ToString() 'ASSERT grace-before: player0 terminated early.'
Assert-KcpEqual 'Reconnecting' $routeC_session1.State.ToString() 'ASSERT grace-before: player1 terminated early.'
$routeC_tick.Invoke($routeC_router, @([uint32]7999, [int64]79990000))
Assert-KcpEqual 'Terminated' $routeC_session0.State.ToString() 'ASSERT grace-expired-player0: match did not terminate.'
Assert-KcpEqual 'Terminated' $routeC_session1.State.ToString() 'ASSERT grace-expired-player1: match did not terminate.'

$routeC_terminalActions = @($routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()))
Assert-KcpEqual 2 $routeC_terminalActions.Count 'ASSERT terminal-broadcast: both players did not receive reason.'
foreach ($routeC_action in $routeC_terminalActions)
{
    $routeC_message = Decode-KcpEnvelope $routeC_types $routeC_action.Datagram
    Assert-KcpEqual 'ResumeRejected' $routeC_message.MessageType.ToString() 'ASSERT terminal-type: wrong terminal control.'
    $routeC_rejectedArguments = @($routeC_message.Payload, $null, [uint16]0)
    Assert-KcpTrue $routeC_types.Codec.GetMethod('TryDecodeResumeRejected').Invoke($null, $routeC_rejectedArguments) 'ASSERT terminal-payload: invalid ResumeRejected.'
    Assert-KcpTrue ([Linq.Enumerable]::SequenceEqual([byte[]]::new(16), [byte[]]$routeC_rejectedArguments[1])) 'ASSERT terminal-attempt: Task8 AttemptID must be all zero.'
    Assert-KcpEqual 6 $routeC_rejectedArguments[2] 'ASSERT terminal-reason: approved ResumeGraceExpired reason changed.'
}

$routeC_tick.Invoke($routeC_router, @([uint32]9000, [int64]90000000))
Assert-KcpEqual 0 @($routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @())).Count 'ASSERT terminal-idempotent: reason broadcast repeated without bound.'

Write-Output 'PASS: KCP server advisory activity, 3000ms timeout, 5000ms grace, and terminal reason are correct.'
