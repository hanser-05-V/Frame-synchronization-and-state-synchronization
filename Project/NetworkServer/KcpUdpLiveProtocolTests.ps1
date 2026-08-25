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

function Send-KcpEnvelope
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

function Receive-KcpMessage
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
        if (-not $Types.Codec.GetMethod('TryDecode').Invoke($null, $routeC_arguments))
        {
            continue
        }
        $routeC_message = $routeC_arguments[2]
        if ($routeC_message.MessageType.ToString() -eq $ExpectedType)
        {
            return $routeC_message
        }
    }
    throw "RED: timed out waiting for live $ExpectedType."
}

function Decode-KcpWelcome
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
    Assert-KcpTrue $Types.Codec.GetMethod('TryDecodeWelcome').Invoke($null, $routeC_arguments) 'ASSERT welcome: invalid payload.'
    return [pscustomobject]@{
        Nonce = [byte[]]$routeC_arguments[1]
        PlayerIndex = [byte]$routeC_arguments[2]
        Conversation = [uint32]$routeC_arguments[3]
        Token = [byte[]]$routeC_arguments[4]
        ResumeRequired = [bool]$routeC_arguments[7]
    }
}

function New-KcpSocket
{
    $routeC_socket = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_socket.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
    return $routeC_socket
}

if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf))
{
    throw "Server assembly does not exist: $ServerPath"
}

$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_types = [pscustomobject]@{
    Server = $routeC_assembly.GetType('FrameSyncServer.KcpUdpRelayServer', $true)
    SessionId = $routeC_assembly.GetType('FrameSyncDemo.RouteCSessionId', $true)
    MessageType = $routeC_assembly.GetType('FrameSyncDemo.RouteCMessageType', $true)
    DropReason = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolDropReason', $true)
    Codec = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolCodec', $true)
    Settings = $routeC_assembly.GetType('FrameSyncDemo.RouteCKcpSettings', $true)
    KcpSession = $routeC_assembly.GetType('FrameSyncDemo.RouteCKcpSession', $true)
}
$routeC_zeroSession = $routeC_types.SessionId.GetField('Zero').GetValue($null)
$routeC_server = [Activator]::CreateInstance($routeC_types.Server, @(10, 0))
$routeC_diagnosticsType = $routeC_server.Diagnostics.GetType()
Assert-KcpTrue `
    ($null -ne $routeC_diagnosticsType.GetMethod('GetActualUpdateGapTicks')) `
    'RED: server diagnostics omit actual KCP Update gap samples.'
Assert-KcpTrue `
    ($null -ne $routeC_diagnosticsType.GetMethod('GetWakeupErrorTicks')) `
    'RED: server diagnostics omit KCP wakeup error samples.'
Assert-KcpTrue `
    ($null -ne $routeC_diagnosticsType.GetMethod('GetEffectiveIntervalMsSamples')) `
    'RED: server diagnostics omit effective interval samples.'
Assert-KcpTrue `
    ($null -ne $routeC_diagnosticsType.GetProperty('WorkerCpuTimeTicks')) `
    'RED: server diagnostics omit worker CPU time.'
$routeC_socket0 = New-KcpSocket
$routeC_socket1 = New-KcpSocket
$routeC_reconnectSocket = $null
try
{
    $routeC_server.Run()
    $routeC_bindWatch = [Diagnostics.Stopwatch]::StartNew()
    while ($routeC_server.BoundPort -le 0 -and $routeC_bindWatch.ElapsedMilliseconds -lt 1000)
    {
        Start-Sleep -Milliseconds 5
    }
    Assert-KcpTrue ($routeC_server.BoundPort -gt 0) 'ASSERT live-bind: server did not bind.'
    $routeC_serverEndpoint = [Net.IPEndPoint]::new(
        [Net.IPAddress]::Loopback,
        $routeC_server.BoundPort)
    $routeC_socket0.Connect($routeC_serverEndpoint)
    $routeC_socket1.Connect($routeC_serverEndpoint)

    $routeC_nonce0 = New-KcpBytes 1 16
    $routeC_nonce1 = New-KcpBytes 41 16
    $routeC_hello0Payload = [byte[]]$routeC_types.Codec.GetMethod('EncodeInitialHello').Invoke($null, @(,$routeC_nonce0))
    $routeC_hello1Payload = [byte[]]$routeC_types.Codec.GetMethod('EncodeInitialHello').Invoke($null, @(,$routeC_nonce1))
    Send-KcpEnvelope $routeC_types $routeC_socket0 'Hello' $routeC_zeroSession 0 $routeC_hello0Payload
    Send-KcpEnvelope $routeC_types $routeC_socket1 'Hello' $routeC_zeroSession 0 $routeC_hello1Payload
    $routeC_welcomeMessage0 = Receive-KcpMessage $routeC_types $routeC_socket0 'Welcome' 1500
    $routeC_welcomeMessage1 = Receive-KcpMessage $routeC_types $routeC_socket1 'Welcome' 1500
    $routeC_welcome0 = Decode-KcpWelcome $routeC_types $routeC_welcomeMessage0
    $routeC_welcome1 = Decode-KcpWelcome $routeC_types $routeC_welcomeMessage1
    Assert-KcpTrue ($routeC_welcome0.PlayerIndex -ne $routeC_welcome1.PlayerIndex) 'ASSERT live-player: player indices collided.'
    Assert-KcpTrue ($routeC_welcome0.Conversation -ne $routeC_welcome1.Conversation) 'ASSERT live-conv: active Conv values collided.'
    Assert-KcpTrue (-not $routeC_welcome0.ResumeRequired) 'ASSERT live-initial-resume: initial Welcome requires resume.'

    $routeC_readyPayload = [byte[]]$routeC_types.Codec.GetMethod('EncodeReady').Invoke($null, @(-1, 0, -1))
    Send-KcpEnvelope $routeC_types $routeC_socket0 'Ready' $routeC_welcomeMessage0.SessionId $routeC_welcomeMessage0.Generation $routeC_readyPayload
    Send-KcpEnvelope $routeC_types $routeC_socket1 'Ready' $routeC_welcomeMessage1.SessionId $routeC_welcomeMessage1.Generation $routeC_readyPayload
    $routeC_start0 = Receive-KcpMessage $routeC_types $routeC_socket0 'Start' 1500
    $routeC_start1 = Receive-KcpMessage $routeC_types $routeC_socket1 'Start' 1500
    Assert-KcpEqual 1 $routeC_start0.Generation 'ASSERT live-start0: wrong generation.'
    Assert-KcpEqual 1 $routeC_start1.Generation 'ASSERT live-start1: wrong generation.'

    $routeC_settings = [Activator]::CreateInstance($routeC_types.Settings, @(10))
    $routeC_clientOutput = [Collections.Queue]::new()
    $routeC_client0Kcp = [Activator]::CreateInstance(
        $routeC_types.KcpSession,
        @($routeC_welcome0.Conversation, $routeC_settings, (New-KcpOutputAction $routeC_clientOutput)))
    $routeC_client1Kcp = [Activator]::CreateInstance(
        $routeC_types.KcpSession,
        @($routeC_welcome1.Conversation, $routeC_settings, (New-KcpOutputAction ([Collections.Queue]::new()))))
    $routeC_expectedRaw = [uint32]3405691582
    $routeC_client0Kcp.SendBusinessInput($routeC_expectedRaw, 77) | Out-Null
    $routeC_client0Kcp.Update([uint32]0, [int64]0)
    $routeC_client0Kcp.Update([uint32]10, [int64]100000)
    while ($routeC_clientOutput.Count -gt 0)
    {
        Send-KcpEnvelope $routeC_types $routeC_socket0 'KcpData' $routeC_welcomeMessage0.SessionId 1 ([byte[]]$routeC_clientOutput.Dequeue())
    }

    $routeC_relayDeadline = [Diagnostics.Stopwatch]::StartNew()
    $routeC_received = $false
    while (-not $routeC_received -and $routeC_relayDeadline.ElapsedMilliseconds -lt 2000)
    {
        if (-not $routeC_socket1.Poll(10000, [Net.Sockets.SelectMode]::SelectRead)) { continue }
        $routeC_buffer = [byte[]]::new(1200)
        $routeC_count = $routeC_socket1.Receive($routeC_buffer)
        $routeC_reason = [Enum]::ToObject($routeC_types.DropReason, 0)
        $routeC_decodeArguments = @($routeC_buffer, $routeC_count, $null, $routeC_reason)
        if (-not $routeC_types.Codec.GetMethod('TryDecode').Invoke($null, $routeC_decodeArguments)) { continue }
        $routeC_message = $routeC_decodeArguments[2]
        if ($routeC_message.MessageType.ToString() -ne 'KcpData') { continue }
        $routeC_client1Kcp.InputDatagram($routeC_message.Payload, 0, $routeC_message.Payload.Length) | Out-Null
        $routeC_receiveArguments = @([uint32]0, 0)
        if ($routeC_client1Kcp.GetType().GetMethod('TryReceiveBusinessInput').Invoke($routeC_client1Kcp, $routeC_receiveArguments))
        {
            Assert-KcpEqual $routeC_expectedRaw $routeC_receiveArguments[0] 'ASSERT live-relay-raw: raw changed.'
            Assert-KcpEqual 77 $routeC_receiveArguments[1] 'ASSERT live-relay-frame: frame changed.'
            $routeC_received = $true
        }
    }
    Assert-KcpTrue $routeC_received 'ASSERT live-relay: receiver did not decode business input.'

    $routeC_reconnectSocket = New-KcpSocket
    $routeC_reconnectSocket.Connect($routeC_serverEndpoint)
    $routeC_reconnectNonce = New-KcpBytes 91 16
    $routeC_reconnectPayload = [byte[]]$routeC_types.Codec.GetMethod('EncodeReconnectHello').Invoke(
        $null,
        @($routeC_reconnectNonce, $routeC_welcome0.Token))
    Send-KcpEnvelope $routeC_types $routeC_reconnectSocket 'Hello' $routeC_welcomeMessage0.SessionId 1 $routeC_reconnectPayload
    $routeC_reconnectWelcomeMessage = Receive-KcpMessage $routeC_types $routeC_reconnectSocket 'Welcome' 1500
    $routeC_reconnectWelcome = Decode-KcpWelcome $routeC_types $routeC_reconnectWelcomeMessage
    Assert-KcpEqual 2 $routeC_reconnectWelcomeMessage.Generation 'ASSERT live-reconnect-generation: switch failed.'
    Assert-KcpTrue ($routeC_reconnectWelcome.Conversation -ne $routeC_welcome0.Conversation) 'ASSERT live-reconnect-conv: old Conv survived.'
    Assert-KcpTrue $routeC_reconnectWelcome.ResumeRequired 'ASSERT live-reconnect-resume: reconnect did not enter resume foundation.'

    $routeC_junk = [byte[]]::new(28)
    for ($routeC_index = 0; $routeC_index -lt 256; $routeC_index++)
    {
        $routeC_socket1.Send($routeC_junk) | Out-Null
    }
    Start-Sleep -Milliseconds 50
    Assert-KcpTrue (-not $routeC_server.Diagnostics.WorkerFault) 'ASSERT live-flood: malformed flood faulted the worker.'
}
finally
{
    if ($routeC_reconnectSocket) { $routeC_reconnectSocket.Dispose() }
    $routeC_socket0.Dispose()
    $routeC_socket1.Dispose()
    $routeC_server.RequestStop()
    Assert-KcpTrue $routeC_server.WaitForStop(500) 'ASSERT live-stop: worker did not stop.'
    $routeC_server.Dispose()
}

Assert-KcpTrue (-not $routeC_server.Diagnostics.WorkerFault) 'ASSERT live-worker-fault: clean protocol run faulted.'
$routeC_actualGaps = $routeC_server.Diagnostics.GetActualUpdateGapTicks(0)
$routeC_wakeupErrors = $routeC_server.Diagnostics.GetWakeupErrorTicks(0)
$routeC_effectiveIntervals = $routeC_server.Diagnostics.GetEffectiveIntervalMsSamples(0)
Assert-KcpTrue ($routeC_actualGaps.Count -gt 0) 'ASSERT live-diagnostics: actual Update gap samples are empty.'
Assert-KcpEqual $routeC_actualGaps.Count $routeC_wakeupErrors.Count 'ASSERT live-diagnostics: timing sample counts differ.'
Assert-KcpTrue ($routeC_effectiveIntervals.Count -gt 0) 'ASSERT live-diagnostics: effective interval samples are empty.'
Assert-KcpTrue ($routeC_effectiveIntervals -contains 10) 'ASSERT live-diagnostics: 10ms effective interval was not observed.'
Assert-KcpTrue ($routeC_server.Diagnostics.WorkerCpuTimeTicks -ge 0) 'ASSERT live-diagnostics: worker CPU time is negative.'
Assert-KcpEqual 10 $routeC_server.Diagnostics.GetCoreIntervalMs(0) `
    'ASSERT live-diagnostics: actual server KCP core interval was not retained.'
$routeC_measurementJson = $routeC_server.Diagnostics.ToMeasurementJson()
$routeC_measurement = $routeC_measurementJson | ConvertFrom-Json
Assert-KcpEqual 'route-c-kcp-server-diagnostics-v1' $routeC_measurement.schema `
    'ASSERT live-diagnostics: machine-readable server schema changed.'
Assert-KcpTrue (-not $routeC_measurement.workerFault) `
    'ASSERT live-diagnostics: measurement reported a worker fault.'
Assert-KcpEqual '' $routeC_measurement.workerFaultCategory `
    'ASSERT live-diagnostics: clean measurement retained a fault category.'
Assert-KcpEqual 10 $routeC_measurement.players[0].coreEffectiveIntervalMs `
    'ASSERT live-diagnostics: machine-readable core interval is not actual KCP state.'
Assert-KcpTrue ($routeC_measurement.players[0].kcpUpdateCalls -gt 0) `
    'ASSERT live-diagnostics: machine-readable Update count is empty.'
Assert-KcpTrue ($routeC_measurement.players[0].actualUpdateGapTicks.Count -gt 0) `
    'ASSERT live-diagnostics: machine-readable gap samples are empty.'
Write-Output 'PASS: live KCP sockets complete handshake, Start, relay, reconnect generation switch, flood tolerance, and bounded stop.'
