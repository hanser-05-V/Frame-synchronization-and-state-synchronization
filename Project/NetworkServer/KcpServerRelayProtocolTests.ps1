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

function New-KcpOutputAction
{
    param([Collections.Queue]$Queue)
    $routeC_actionType = [Action``2].MakeGenericType(@([byte[]], [int]))
    $routeC_script = {
        param([byte[]]$Bytes, [int]$Count)
        $routeC_copy = [byte[]]::new($Count)
        [Array]::Copy($Bytes, 0, $routeC_copy, 0, $Count)
        $Queue.Enqueue($routeC_copy)
    }.GetNewClosure()
    return [Management.Automation.LanguagePrimitives]::ConvertTo(
        $routeC_script,
        $routeC_actionType)
}

function Decode-KcpEnvelope
{
    param($Types, [byte[]]$Datagram)
    $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
    $routeC_arguments = @($Datagram, $Datagram.Length, $null, $routeC_reason)
    $routeC_ok = $Types.Codec.GetMethod('TryDecode').Invoke($null, $routeC_arguments)
    Assert-KcpTrue $routeC_ok 'ASSERT envelope: invalid Route C output.'
    return $routeC_arguments[2]
}

function New-KcpMessage
{
    param($Types, [string]$TypeName, $Session, [byte[]]$Payload)
    $routeC_type = [Enum]::Parse($Types.MessageType, $TypeName)
    return [Activator]::CreateInstance(
        $Types.Message,
        @($routeC_type, $Session.SessionId, $Session.Generation, $Payload))
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
    Header = $routeC_assembly.GetType('FrameSyncDemo.RouteCKcpHeader', $true)
    Settings = $routeC_assembly.GetType('FrameSyncDemo.RouteCKcpSettings', $true)
    KcpSession = $routeC_assembly.GetType('FrameSyncDemo.RouteCKcpSession', $true)
}

$routeC_sessionId0 = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]301, [uint64]302))
$routeC_sessionId1 = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]401, [uint64]402))
$routeC_diagnostics = [Activator]::CreateInstance($routeC_types.Diagnostics)
$routeC_router = [Activator]::CreateInstance(
    $routeC_types.Router,
    @(
        $routeC_diagnostics,
        (New-KcpFactory $routeC_types.SessionId @($routeC_sessionId0, $routeC_sessionId1)),
        (New-KcpFactory ([byte[]]) @((New-KcpBytes 31 32), (New-KcpBytes 91 32))),
        (New-KcpFactory ([uint32]) @([uint32]701, [uint32]702)),
        10))
$routeC_endpoint0 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 14001)
$routeC_endpoint1 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 14002)
$routeC_helloMethod = $routeC_types.Router.GetMethod('HandleInitialHelloAt')
$routeC_hello0Arguments = @($routeC_endpoint0, (New-KcpBytes 1 16), [uint32]0, $null, $null)
$routeC_hello1Arguments = @($routeC_endpoint1, (New-KcpBytes 51 16), [uint32]0, $null, $null)
$routeC_helloMethod.Invoke($routeC_router, $routeC_hello0Arguments) | Out-Null
$routeC_helloMethod.Invoke($routeC_router, $routeC_hello1Arguments) | Out-Null
$routeC_serverSession0 = $routeC_hello0Arguments[4]
$routeC_serverSession1 = $routeC_hello1Arguments[4]

$routeC_readyPayload = [byte[]]$routeC_types.Codec.GetMethod('EncodeReady').Invoke(
    $null,
    @(-1, 0, -1))
$routeC_handleReady = $routeC_types.Router.GetMethod('HandleReady')
$routeC_handleReady.Invoke(
    $routeC_router,
    @((New-KcpMessage $routeC_types 'Ready' $routeC_serverSession0 $routeC_readyPayload), $routeC_endpoint0, [uint32]0)) | Out-Null
$routeC_handleReady.Invoke(
    $routeC_router,
    @((New-KcpMessage $routeC_types 'Ready' $routeC_serverSession1 $routeC_readyPayload), $routeC_endpoint1, [uint32]0)) | Out-Null
$routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()) | Out-Null

$routeC_settings = [Activator]::CreateInstance($routeC_types.Settings, @(10))
$routeC_client0Output = [Collections.Queue]::new()
$routeC_client1Output = [Collections.Queue]::new()
$routeC_client0 = [Activator]::CreateInstance(
    $routeC_types.KcpSession,
    @([uint32]701, $routeC_settings, (New-KcpOutputAction $routeC_client0Output)))
$routeC_client1 = [Activator]::CreateInstance(
    $routeC_types.KcpSession,
    @([uint32]702, $routeC_settings, (New-KcpOutputAction $routeC_client1Output)))

Assert-KcpEqual 0 $routeC_client0.SendBusinessInput([uint32]0x12345678, 42) 'ASSERT client-send: KCP rejected business input.'
$routeC_client0.Update([uint32]0, [int64]0)
$routeC_client0.Update([uint32]10, [int64]100000)
Assert-KcpTrue ($routeC_client0Output.Count -gt 0) 'ASSERT client-output: sender produced no KCP datagram.'
$routeC_senderPayload = [byte[]]$routeC_client0Output.Dequeue()
$routeC_handleKcp = $routeC_types.Router.GetMethod('HandleKcpData')
Assert-KcpTrue ($null -ne $routeC_handleKcp) 'RED: ServerSessionRouter.HandleKcpData is missing.'
$routeC_senderMessage = New-KcpMessage $routeC_types 'KcpData' $routeC_serverSession0 $routeC_senderPayload
Assert-KcpTrue $routeC_handleKcp.Invoke(
    $routeC_router,
    @($routeC_senderMessage, $routeC_endpoint0, [uint32]0)) `
    'ASSERT server-input: valid sender KCP data was rejected.'

$routeC_tick = $routeC_types.Router.GetMethod('Tick')
Assert-KcpTrue ($null -ne $routeC_tick) 'RED: ServerSessionRouter.Tick is missing.'
$routeC_tick.Invoke($routeC_router, @([uint32]0, [int64]0))
$routeC_tick.Invoke($routeC_router, @([uint32]10, [int64]100000))
Assert-KcpEqual 0 $routeC_diagnostics.LastTickFirstProcessedPlayerIndex 'RED: same-deadline processing did not start with player 0.'
Assert-KcpEqual 1 $routeC_diagnostics.LastTickSecondProcessedPlayerIndex 'RED: same-deadline processing did not continue with player 1.'
$routeC_actions = @($routeC_types.Router.GetMethod('DrainActions').Invoke($routeC_router, @()))
Assert-KcpTrue ($routeC_actions.Count -gt 0) 'ASSERT server-output: relay produced no KCP output.'

$routeC_receiverPayload = $null
foreach ($routeC_action in $routeC_actions)
{
    $routeC_message = Decode-KcpEnvelope $routeC_types $routeC_action.Datagram
    if ($routeC_message.MessageType.ToString() -ne 'KcpData') { continue }
    $routeC_convArguments = @($routeC_message.Payload, [uint32]0)
    if ($routeC_types.Header.GetMethod('TryReadConversation').Invoke($null, $routeC_convArguments) -and
        $routeC_convArguments[1] -eq 702)
    {
        Assert-KcpTrue $routeC_action.Endpoint.Equals($routeC_endpoint1) 'ASSERT receiver-endpoint: relay targeted wrong player.'
        $routeC_receiverPayload = $routeC_message.Payload
        break
    }
}
Assert-KcpTrue ($null -ne $routeC_receiverPayload) 'ASSERT receiver-conv: no receiver-Conv KCP output was found.'
Assert-KcpTrue (-not [Linq.Enumerable]::SequenceEqual([byte[]]$routeC_senderPayload, [byte[]]$routeC_receiverPayload)) 'ASSERT direct-forward: sender KCP payload was forwarded unchanged.'

Assert-KcpEqual 0 $routeC_client1.InputDatagram($routeC_receiverPayload, 0, $routeC_receiverPayload.Length) 'ASSERT client1-input: receiver KCP rejected relay.'
$routeC_receiveArguments = @([uint32]0, 0)
Assert-KcpTrue $routeC_client1.GetType().GetMethod('TryReceiveBusinessInput').Invoke($routeC_client1, $routeC_receiveArguments) 'ASSERT client1-receive: relayed business input missing.'
Assert-KcpEqual ([uint32]0x12345678) $routeC_receiveArguments[0] 'ASSERT relay-raw: raw changed.'
Assert-KcpEqual 42 $routeC_receiveArguments[1] 'ASSERT relay-frame: frame changed.'

$routeC_historyRaw = [uint32]0
$routeC_historyArguments = @(42, $routeC_historyRaw)
Assert-KcpTrue $routeC_serverSession0.History.GetType().GetMethod('TryGet').Invoke($routeC_serverSession0.History, $routeC_historyArguments) 'ASSERT sender-history: Actual was not recorded.'
Assert-KcpEqual ([uint32]0x12345678) $routeC_historyArguments[1] 'ASSERT sender-history: recorded raw changed.'

Write-Output 'PASS: KCP server decodes business input and re-sends through the receiver Conv.'
