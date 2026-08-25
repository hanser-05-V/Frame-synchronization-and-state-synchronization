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
    if ($Expected -ne $Actual)
    {
        throw "$Message Expected=$Expected Actual=$Actual"
    }
}

function New-KcpBytes
{
    param([byte]$Start, [int]$Count)
    $routeC_bytes = [byte[]]::new($Count)
    for ($routeC_index = 0; $routeC_index -lt $Count; $routeC_index++)
    {
        $routeC_bytes[$routeC_index] = [byte]($Start + $routeC_index)
    }
    return $routeC_bytes
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

function New-KcpSessionId
{
    param($Types, [uint64]$High, [uint64]$Low)
    return [Activator]::CreateInstance($Types.SessionId, @($High, $Low))
}

function Invoke-KcpInitialHelloAt
{
    param($Types, $Router, [Net.IPEndPoint]$Endpoint, [byte[]]$Nonce, [uint32]$NowMs)
    $routeC_method = $Types.Router.GetMethod('HandleInitialHelloAt')
    Assert-KcpTrue ($null -ne $routeC_method) 'RED: HandleInitialHelloAt is missing.'
    $routeC_arguments = @($Endpoint, $Nonce, $NowMs, $null, $null)
    $routeC_disposition = $routeC_method.Invoke($Router, $routeC_arguments)
    return [pscustomobject]@{
        Disposition = $routeC_disposition
        Welcome = $routeC_arguments[3]
        Session = $routeC_arguments[4]
    }
}

function New-KcpReady
{
    param($Types, $Session)
    $routeC_payload = $Types.Codec.GetMethod('EncodeReady').Invoke(
        $null,
        @(-1, 0, -1))
    $routeC_messageType = [Enum]::Parse($Types.MessageType, 'Ready')
    return [Activator]::CreateInstance(
        $Types.Message,
        @(
            $routeC_messageType,
            $Session.SessionId,
            $Session.Generation,
            $routeC_payload))
}

function Decode-KcpEnvelope
{
    param($Types, [byte[]]$Datagram)
    $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
    $routeC_arguments = @($Datagram, $Datagram.Length, $null, $routeC_reason)
    $routeC_ok = $Types.Codec.GetMethod('TryDecode').Invoke(
        $null,
        $routeC_arguments)
    Assert-KcpTrue $routeC_ok 'ASSERT envelope: emitted bytes were invalid.'
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
    History = $routeC_assembly.GetType('FrameSyncServer.ServerInputHistory', $true)
    SessionId = $routeC_assembly.GetType('FrameSyncDemo.RouteCSessionId', $true)
    Message = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolMessage', $true)
    MessageType = $routeC_assembly.GetType('FrameSyncDemo.RouteCMessageType', $true)
    DropReason = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolDropReason', $true)
    Codec = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolCodec', $true)
}

$routeC_history = [Activator]::CreateInstance($routeC_types.History, @(4))
Assert-KcpEqual 'Accepted' $routeC_history.Record(7, [uint32]100).ToString() 'ASSERT history-first: input was not accepted.'
Assert-KcpEqual 'IdempotentDuplicate' $routeC_history.Record(7, [uint32]100).ToString() 'ASSERT history-idempotent: same value was not idempotent.'
Assert-KcpEqual 'ConflictingDuplicate' $routeC_history.Record(7, [uint32]200).ToString() 'ASSERT history-conflict: conflicting value was not rejected.'
$routeC_raw = [uint32]0
$routeC_tryGetArguments = @(7, $routeC_raw)
Assert-KcpTrue $routeC_history.GetType().GetMethod('TryGet').Invoke($routeC_history, $routeC_tryGetArguments) 'ASSERT history-first-value: retained input missing.'
Assert-KcpEqual 100 $routeC_tryGetArguments[1] 'ASSERT history-first-value: conflict overwrote first raw.'

$routeC_sessionId0 = New-KcpSessionId $routeC_types 101 102
$routeC_sessionId1 = New-KcpSessionId $routeC_types 201 202
$routeC_router = [Activator]::CreateInstance(
    $routeC_types.Router,
    @(
        ([Activator]::CreateInstance($routeC_types.Diagnostics)),
        (New-KcpFactory $routeC_types.SessionId @($routeC_sessionId0, $routeC_sessionId1)),
        (New-KcpFactory ([byte[]]) @((New-KcpBytes 21 32), (New-KcpBytes 61 32))),
        (New-KcpFactory ([uint32]) @([uint32]301, [uint32]302)),
        10))
$routeC_endpoint0 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 13001)
$routeC_endpoint1 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 13002)
$routeC_hello0 = Invoke-KcpInitialHelloAt $routeC_types $routeC_router $routeC_endpoint0 (New-KcpBytes 1 16) 0
$routeC_hello1 = Invoke-KcpInitialHelloAt $routeC_types $routeC_router $routeC_endpoint1 (New-KcpBytes 41 16) 0
Assert-KcpEqual 'Allocated' $routeC_hello0.Disposition.ToString() 'ASSERT setup-player0: allocation failed.'
Assert-KcpEqual 'Allocated' $routeC_hello1.Disposition.ToString() 'ASSERT setup-player1: allocation failed.'

$routeC_handleReady = $routeC_types.Router.GetMethod('HandleReady')
Assert-KcpTrue ($null -ne $routeC_handleReady) 'RED: HandleReady is missing.'
Assert-KcpTrue $routeC_handleReady.Invoke(
    $routeC_router,
    @((New-KcpReady $routeC_types $routeC_hello0.Session), $routeC_endpoint0, [uint32]10)) `
    'ASSERT ready-player0: valid Ready was rejected.'
$routeC_drainActions = $routeC_types.Router.GetMethod('DrainActions')
Assert-KcpTrue ($null -ne $routeC_drainActions) 'RED: DrainActions is missing.'
Assert-KcpEqual 0 @($routeC_drainActions.Invoke($routeC_router, @())).Count 'ASSERT one-ready: Start was sent before both Ready.'

Assert-KcpTrue $routeC_handleReady.Invoke(
    $routeC_router,
    @((New-KcpReady $routeC_types $routeC_hello1.Session), $routeC_endpoint1, [uint32]11)) `
    'ASSERT ready-player1: valid Ready was rejected.'
$routeC_starts = @($routeC_drainActions.Invoke($routeC_router, @()))
Assert-KcpEqual 2 $routeC_starts.Count 'ASSERT start-barrier: both players did not receive Start.'
$routeC_start0 = $routeC_starts | Where-Object { $_.Endpoint.Equals($routeC_endpoint0) } | Select-Object -First 1
$routeC_start1 = $routeC_starts | Where-Object { $_.Endpoint.Equals($routeC_endpoint1) } | Select-Object -First 1
Assert-KcpTrue ($null -ne $routeC_start0) 'ASSERT start-player0: action missing.'
Assert-KcpTrue ($null -ne $routeC_start1) 'ASSERT start-player1: action missing.'
foreach ($routeC_start in $routeC_starts)
{
    $routeC_startMessage = Decode-KcpEnvelope $routeC_types $routeC_start.Datagram
    Assert-KcpEqual 'Start' $routeC_startMessage.MessageType.ToString() 'ASSERT start-type: wrong message.'
    $routeC_startFrameArguments = @($routeC_startMessage.Payload, 99)
    Assert-KcpTrue $routeC_types.Codec.GetMethod('TryDecodeStart').Invoke($null, $routeC_startFrameArguments) 'ASSERT start-payload: invalid payload.'
    Assert-KcpEqual 0 $routeC_startFrameArguments[1] 'ASSERT start-frame: canonical start changed.'
}

Assert-KcpEqual 'Running' $routeC_hello0.Session.State.ToString() 'ASSERT state-player0: did not enter Running.'
Assert-KcpEqual 'Running' $routeC_hello1.Session.State.ToString() 'ASSERT state-player1: did not enter Running.'
Assert-KcpTrue $routeC_handleReady.Invoke(
    $routeC_router,
    @((New-KcpReady $routeC_types $routeC_hello0.Session), $routeC_endpoint0, [uint32]12)) `
    'ASSERT duplicate-ready: valid duplicate was rejected.'
$routeC_retryStarts = @($routeC_drainActions.Invoke($routeC_router, @()))
Assert-KcpEqual 1 $routeC_retryStarts.Count 'ASSERT duplicate-ready: retry did not target one player.'
Assert-KcpTrue ([Linq.Enumerable]::SequenceEqual([byte[]]$routeC_start0.Datagram, [byte[]]$routeC_retryStarts[0].Datagram)) 'ASSERT duplicate-ready: Start bytes were not stable.'

Write-Output 'PASS: KCP server immutable history and idempotent Ready/Start barrier are correct.'
