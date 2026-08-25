param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath
)

$ErrorActionPreference = 'Stop'

function Assert-KcpTrue
{
    param([bool]$Condition, [string]$Message)
    if (-not $Condition)
    {
        throw $Message
    }
}

function Assert-KcpEqual
{
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual)
    {
        throw "$Message Expected=$Expected Actual=$Actual"
    }
}

function New-KcpPayload
{
    param([uint32]$Conversation)
    $routeC_payload = [byte[]]::new(24)
    $routeC_payload[0] = [byte]($Conversation -band 0xFF)
    $routeC_payload[1] = [byte](($Conversation -shr 8) -band 0xFF)
    $routeC_payload[2] = [byte](($Conversation -shr 16) -band 0xFF)
    $routeC_payload[3] = [byte](($Conversation -shr 24) -band 0xFF)
    return $routeC_payload
}

function New-KcpMessage
{
    param(
        $Types,
        $SessionId,
        [uint32]$Generation,
        [byte[]]$Payload)
    $routeC_messageType = [Enum]::Parse($Types.MessageType, 'KcpData')
    return [Activator]::CreateInstance(
        $Types.Message,
        @($routeC_messageType, $SessionId, $Generation, $Payload))
}

function Invoke-KcpRoute
{
    param($Types, $Router, $Message, [Net.IPEndPoint]$Endpoint)
    $routeC_session = $null
    $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
    $routeC_arguments = @($Message, $Endpoint, $routeC_session, $routeC_reason)
    $routeC_ok = $Types.Router.GetMethod('TryRoute').Invoke(
        $Router,
        $routeC_arguments)
    return [pscustomobject]@{
        Ok = $routeC_ok
        Session = $routeC_arguments[2]
        Reason = $routeC_arguments[3]
    }
}

function New-KcpByteRange
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
    foreach ($routeC_value in $Values)
    {
        $routeC_queue.Enqueue($routeC_value)
    }
    $routeC_delegateType = [Func``1].MakeGenericType(@($ReturnType))
    $routeC_script = { return $routeC_queue.Dequeue() }.GetNewClosure()
    return [Management.Automation.LanguagePrimitives]::ConvertTo(
        $routeC_script,
        $routeC_delegateType)
}

function Invoke-KcpInitialHello
{
    param($Types, $Router, [Net.IPEndPoint]$Endpoint, [byte[]]$Nonce)
    $routeC_arguments = @($Endpoint, $Nonce, $null, $null)
    $routeC_disposition = $Types.Router.GetMethod('HandleInitialHello').Invoke(
        $Router,
        $routeC_arguments)
    return [pscustomobject]@{
        Disposition = $routeC_disposition
        Welcome = $routeC_arguments[2]
        Session = $routeC_arguments[3]
    }
}

function Invoke-KcpReconnectHello
{
    param(
        $Types,
        $Router,
        [Net.IPEndPoint]$Endpoint,
        $SessionId,
        [uint32]$Generation,
        [byte[]]$OldToken,
        [byte[]]$NewNonce)
    $routeC_arguments = @(
        $Endpoint,
        $SessionId,
        $Generation,
        $OldToken,
        $NewNonce,
        $null,
        $null)
    $routeC_method = $Types.Router.GetMethod('HandleReconnectHello')
    Assert-KcpTrue ($null -ne $routeC_method) 'RED: ServerSessionRouter.HandleReconnectHello is missing.'
    $routeC_disposition = $routeC_method.Invoke(
        $Router,
        $routeC_arguments)
    return [pscustomobject]@{
        Disposition = $routeC_disposition
        Welcome = $routeC_arguments[5]
        Session = $routeC_arguments[6]
    }
}

function Decode-KcpEnvelope
{
    param($Types, [byte[]]$Datagram)
    $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
    $routeC_arguments = @($Datagram, $Datagram.Length, $null, $routeC_reason)
    $routeC_ok = $Types.EnvelopeCodec.GetMethod('TryDecode').Invoke(
        $null,
        $routeC_arguments)
    Assert-KcpTrue $routeC_ok 'ASSERT welcome-envelope: server emitted invalid Route C bytes.'
    return $routeC_arguments[2]
}

if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf))
{
    throw "Server assembly does not exist: $ServerPath"
}

$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_types = [pscustomobject]@{
    Session = $routeC_assembly.GetType('FrameSyncServer.KcpServerSession', $true)
    Router = $routeC_assembly.GetType('FrameSyncServer.ServerSessionRouter', $true)
    Diagnostics = $routeC_assembly.GetType('FrameSyncServer.KcpServerDiagnostics', $true)
    SessionId = $routeC_assembly.GetType('FrameSyncDemo.RouteCSessionId', $true)
    Message = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolMessage', $true)
    MessageType = $routeC_assembly.GetType('FrameSyncDemo.RouteCMessageType', $true)
    DropReason = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolDropReason', $true)
    EnvelopeCodec = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolCodec', $true)
    Crypto = $routeC_assembly.GetType('FrameSyncServer.CryptoRandomSource', $true)
    HandshakeDisposition = $routeC_assembly.GetType('FrameSyncServer.ServerHandshakeDisposition', $true)
}

$routeC_endpoint0 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 11001)
$routeC_endpoint1 = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 11002)
$routeC_wrongEndpoint = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 11999)
$routeC_sessionId0 = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]1, [uint64]2))
$routeC_sessionId1 = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]3, [uint64]4))
$routeC_unknownSessionId = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]9, [uint64]10))
$routeC_session0 = [Activator]::CreateInstance(
    $routeC_types.Session,
    @([byte]0, $routeC_sessionId0, [uint32]1, [uint32]101, $routeC_endpoint0))
$routeC_session1 = [Activator]::CreateInstance(
    $routeC_types.Session,
    @([byte]1, $routeC_sessionId1, [uint32]7, [uint32]202, $routeC_endpoint1))
$routeC_diagnostics = [Activator]::CreateInstance($routeC_types.Diagnostics)
$routeC_router = [Activator]::CreateInstance(
    $routeC_types.Router,
    @($routeC_session0, $routeC_session1, $routeC_diagnostics))

$routeC_cases = @(
    [pscustomobject]@{
        Name = 'unknown-session'
        Message = New-KcpMessage $routeC_types $routeC_unknownSessionId 1 (New-KcpPayload 101)
        Endpoint = $routeC_endpoint0
        Expected = 'SessionNotFound'
    },
    [pscustomobject]@{
        Name = 'stale-generation'
        Message = New-KcpMessage $routeC_types $routeC_sessionId0 2 (New-KcpPayload 101)
        Endpoint = $routeC_endpoint0
        Expected = 'StaleGeneration'
    },
    [pscustomobject]@{
        Name = 'wrong-endpoint'
        Message = New-KcpMessage $routeC_types $routeC_sessionId0 1 (New-KcpPayload 101)
        Endpoint = $routeC_wrongEndpoint
        Expected = 'WrongEndpoint'
    },
    [pscustomobject]@{
        Name = 'truncated-kcp-header'
        Message = New-KcpMessage $routeC_types $routeC_sessionId0 1 ([byte[]]::new(23))
        Endpoint = $routeC_endpoint0
        Expected = 'KcpPayloadTooShort'
    },
    [pscustomobject]@{
        Name = 'wrong-conversation'
        Message = New-KcpMessage $routeC_types $routeC_sessionId0 1 (New-KcpPayload 999)
        Endpoint = $routeC_endpoint0
        Expected = 'WrongConversation'
    }
)

foreach ($routeC_case in $routeC_cases)
{
    $routeC_result = Invoke-KcpRoute `
        $routeC_types `
        $routeC_router `
        $routeC_case.Message `
        $routeC_case.Endpoint
    Assert-KcpTrue (-not $routeC_result.Ok) "ASSERT $($routeC_case.Name): invalid route was accepted."
    Assert-KcpEqual $routeC_case.Expected $routeC_result.Reason.ToString() "ASSERT $($routeC_case.Name): wrong drop reason."
}

$routeC_correct = Invoke-KcpRoute `
    $routeC_types `
    $routeC_router `
    (New-KcpMessage $routeC_types $routeC_sessionId0 1 (New-KcpPayload 101)) `
    $routeC_endpoint0
Assert-KcpTrue $routeC_correct.Ok 'ASSERT correct-route: valid route was rejected.'
Assert-KcpTrue ([object]::ReferenceEquals($routeC_session0, $routeC_correct.Session)) 'ASSERT correct-route: wrong session returned.'
Assert-KcpEqual 'None' $routeC_correct.Reason.ToString() 'ASSERT correct-route: nonzero drop reason.'

$routeC_getDropCount = $routeC_types.Diagnostics.GetMethod('GetDropCount')
foreach ($routeC_case in $routeC_cases)
{
    $routeC_reason = [Enum]::Parse($routeC_types.DropReason, $routeC_case.Expected)
    $routeC_count = $routeC_getDropCount.Invoke($routeC_diagnostics, @($routeC_reason))
    Assert-KcpEqual 1 $routeC_count "ASSERT $($routeC_case.Name): drop counter was not independent."
}
Assert-KcpEqual 5 $routeC_diagnostics.TotalDropCount 'ASSERT total-drops: aggregate count mismatch.'

$routeC_nonce0 = New-KcpByteRange 1 16
$routeC_nonce1 = New-KcpByteRange 33 16
$routeC_nonce2 = New-KcpByteRange 65 16
$routeC_token0 = New-KcpByteRange 97 32
$routeC_token1 = New-KcpByteRange 129 32
$routeC_handshakeDiagnostics = [Activator]::CreateInstance($routeC_types.Diagnostics)
$routeC_sessionFactory = New-KcpFactory `
    $routeC_types.SessionId `
    @($routeC_sessionId0, $routeC_sessionId1)
$routeC_tokenFactory = New-KcpFactory ([byte[]]) @($routeC_token0, $routeC_token1)
$routeC_conversationFactory = New-KcpFactory ([uint32]) @(
    [uint32]0,
    [uint32]101,
    [uint32]101,
    [uint32]202)
$routeC_handshakeRouter = [Activator]::CreateInstance(
    $routeC_types.Router,
    @(
        $routeC_handshakeDiagnostics,
        $routeC_sessionFactory,
        $routeC_tokenFactory,
        $routeC_conversationFactory))

$routeC_firstHello = Invoke-KcpInitialHello `
    $routeC_types `
    $routeC_handshakeRouter `
    $routeC_endpoint0 `
    $routeC_nonce0
Assert-KcpEqual 'Allocated' $routeC_firstHello.Disposition.ToString() 'ASSERT first-hello: session was not allocated.'
Assert-KcpTrue ($null -ne $routeC_firstHello.Welcome) 'ASSERT first-hello: Welcome bytes missing.'
Assert-KcpEqual 1 $routeC_handshakeRouter.ActiveSessionCount 'ASSERT first-hello: wrong active Session count.'

$routeC_retryHello = Invoke-KcpInitialHello `
    $routeC_types `
    $routeC_handshakeRouter `
    $routeC_endpoint0 `
    $routeC_nonce0
Assert-KcpEqual 'IdempotentRetry' $routeC_retryHello.Disposition.ToString() 'ASSERT hello-retry: duplicate did not take idempotent path.'
Assert-KcpTrue ([object]::ReferenceEquals($routeC_firstHello.Session, $routeC_retryHello.Session)) 'ASSERT hello-retry: duplicate created a new Session.'
Assert-KcpTrue ([Linq.Enumerable]::SequenceEqual([byte[]]$routeC_firstHello.Welcome, [byte[]]$routeC_retryHello.Welcome)) 'ASSERT hello-retry: Welcome was not byte-identical.'
Assert-KcpEqual 1 $routeC_handshakeRouter.ActiveSessionCount 'ASSERT hello-retry: Session count changed.'

$routeC_secondHello = Invoke-KcpInitialHello `
    $routeC_types `
    $routeC_handshakeRouter `
    $routeC_endpoint0 `
    $routeC_nonce1
Assert-KcpEqual 'Allocated' $routeC_secondHello.Disposition.ToString() 'ASSERT second-player: free player was not allocated.'
Assert-KcpEqual 2 $routeC_handshakeRouter.ActiveSessionCount 'ASSERT second-player: wrong active Session count.'
Assert-KcpTrue ($routeC_firstHello.Session.Conversation -ne 0) 'ASSERT first-conv: Conv must be nonzero.'
Assert-KcpTrue ($routeC_secondHello.Session.Conversation -ne 0) 'ASSERT second-conv: Conv must be nonzero.'
Assert-KcpTrue ($routeC_firstHello.Session.Conversation -ne $routeC_secondHello.Session.Conversation) 'ASSERT conv-collision: active Conv values must differ.'

$routeC_thirdHello = Invoke-KcpInitialHello `
    $routeC_types `
    $routeC_handshakeRouter `
    $routeC_wrongEndpoint `
    $routeC_nonce2
Assert-KcpEqual 'MatchFull' $routeC_thirdHello.Disposition.ToString() 'ASSERT match-full: third active allocation was not rejected.'
Assert-KcpTrue ($null -eq $routeC_thirdHello.Welcome) 'ASSERT match-full: rejected allocation emitted Welcome.'
Assert-KcpTrue ($null -eq $routeC_thirdHello.Session) 'ASSERT match-full: rejected allocation returned Session.'
Assert-KcpEqual 2 $routeC_handshakeRouter.ActiveSessionCount 'ASSERT match-full: Session count changed.'

$routeC_firstWelcome = Decode-KcpEnvelope $routeC_types $routeC_firstHello.Welcome
Assert-KcpEqual 'Welcome' $routeC_firstWelcome.MessageType.ToString() 'ASSERT welcome-type: wrong message type.'
Assert-KcpEqual 1 $routeC_firstWelcome.Generation 'ASSERT welcome-generation: initial Generation must be one.'
$routeC_welcomeArguments = @(
    $routeC_firstWelcome.Payload,
    $null,
    [byte]0,
    [uint32]0,
    $null,
    0,
    0,
    $false)
$routeC_welcomeOk = $routeC_types.EnvelopeCodec.GetMethod('TryDecodeWelcome').Invoke(
    $null,
    $routeC_welcomeArguments)
Assert-KcpTrue $routeC_welcomeOk 'ASSERT welcome-payload: payload did not decode.'
Assert-KcpTrue ([Linq.Enumerable]::SequenceEqual([byte[]]$routeC_nonce0, [byte[]]$routeC_welcomeArguments[1])) 'ASSERT welcome-nonce: nonce echo changed.'
Assert-KcpEqual 0 $routeC_welcomeArguments[2] 'ASSERT welcome-player: first player index changed.'
Assert-KcpEqual 101 $routeC_welcomeArguments[3] 'ASSERT welcome-conv: collision retry did not preserve selected Conv.'
Assert-KcpTrue ([Linq.Enumerable]::SequenceEqual([byte[]]$routeC_token0, [byte[]]$routeC_welcomeArguments[4])) 'ASSERT welcome-token: reconnect token changed.'
Assert-KcpEqual 1000 $routeC_welcomeArguments[5] 'ASSERT welcome-heartbeat: locked heartbeat changed.'
Assert-KcpEqual 3000 $routeC_welcomeArguments[6] 'ASSERT welcome-timeout: locked timeout changed.'
Assert-KcpTrue (-not $routeC_welcomeArguments[7]) 'ASSERT welcome-resume: initial session cannot require resume.'

$routeC_crypto = [Activator]::CreateInstance($routeC_types.Crypto)
Assert-KcpEqual 16 $routeC_crypto.CreateNonce().Length 'ASSERT crypto-nonce: wrong byte length.'
Assert-KcpEqual 32 $routeC_crypto.CreateReconnectToken().Length 'ASSERT crypto-token: wrong byte length.'
Assert-KcpTrue (-not $routeC_crypto.CreateSessionId().IsZero) 'ASSERT crypto-session: SessionID must be nonzero.'
Assert-KcpTrue ($routeC_crypto.CreateConversation() -ne 0) 'ASSERT crypto-conv: Conv must be nonzero.'

$routeC_reconnectEndpoint0 = [Net.IPEndPoint]::new(
    [Net.IPAddress]::Loopback,
    12001)
$routeC_reconnectEndpoint1 = [Net.IPEndPoint]::new(
    [Net.IPAddress]::Loopback,
    12099)
$routeC_reconnectNonce0 = New-KcpByteRange 11 16
$routeC_reconnectNonce1 = New-KcpByteRange 44 16
$routeC_reconnectNonce2 = New-KcpByteRange 77 16
$routeC_oldToken = New-KcpByteRange 21 32
$routeC_newToken = New-KcpByteRange 121 32
$routeC_wrongToken = New-KcpByteRange 171 32
$routeC_reconnectSessionId = [Activator]::CreateInstance(
    $routeC_types.SessionId,
    @([uint64]41, [uint64]42))
$routeC_reconnectRouter = [Activator]::CreateInstance(
    $routeC_types.Router,
    @(
        ([Activator]::CreateInstance($routeC_types.Diagnostics)),
        (New-KcpFactory $routeC_types.SessionId @($routeC_reconnectSessionId)),
        (New-KcpFactory ([byte[]]) @($routeC_oldToken, $routeC_newToken)),
        (New-KcpFactory ([uint32]) @([uint32]501, [uint32]501, [uint32]502))))
$routeC_reconnectInitial = Invoke-KcpInitialHello `
    $routeC_types `
    $routeC_reconnectRouter `
    $routeC_reconnectEndpoint0 `
    $routeC_reconnectNonce0
Assert-KcpEqual 'Allocated' $routeC_reconnectInitial.Disposition.ToString() 'ASSERT reconnect-setup: initial allocation failed.'

$routeC_reconnect = Invoke-KcpReconnectHello `
    $routeC_types `
    $routeC_reconnectRouter `
    $routeC_reconnectEndpoint1 `
    $routeC_reconnectSessionId `
    1 `
    $routeC_oldToken `
    $routeC_reconnectNonce1
Assert-KcpEqual 'Reconnected' $routeC_reconnect.Disposition.ToString() 'ASSERT reconnect-first: generation switch was rejected.'
Assert-KcpEqual 2 $routeC_reconnect.Session.Generation 'ASSERT reconnect-generation: generation did not increment exactly once.'
Assert-KcpEqual 502 $routeC_reconnect.Session.Conversation 'ASSERT reconnect-conv: zero/collision-safe new Conv was not installed.'
Assert-KcpTrue $routeC_reconnect.Session.Endpoint.Equals($routeC_reconnectEndpoint1) 'ASSERT reconnect-endpoint: new Endpoint was not installed.'

$routeC_reconnectRetry = Invoke-KcpReconnectHello `
    $routeC_types `
    $routeC_reconnectRouter `
    $routeC_reconnectEndpoint1 `
    $routeC_reconnectSessionId `
    1 `
    $routeC_oldToken `
    $routeC_reconnectNonce1
Assert-KcpEqual 'IdempotentRetry' $routeC_reconnectRetry.Disposition.ToString() 'ASSERT reconnect-retry: exact retry was not idempotent.'
Assert-KcpTrue ([object]::ReferenceEquals($routeC_reconnect.Session, $routeC_reconnectRetry.Session)) 'ASSERT reconnect-retry: retry replaced Session again.'
Assert-KcpTrue ([Linq.Enumerable]::SequenceEqual([byte[]]$routeC_reconnect.Welcome, [byte[]]$routeC_reconnectRetry.Welcome)) 'ASSERT reconnect-retry: Welcome was not byte-identical.'
Assert-KcpEqual 2 $routeC_reconnectRetry.Session.Generation 'ASSERT reconnect-retry: generation incremented twice.'

$routeC_oldTokenRejected = Invoke-KcpReconnectHello `
    $routeC_types `
    $routeC_reconnectRouter `
    $routeC_reconnectEndpoint1 `
    $routeC_reconnectSessionId `
    2 `
    $routeC_oldToken `
    $routeC_reconnectNonce2
Assert-KcpEqual 'Rejected' $routeC_oldTokenRejected.Disposition.ToString() 'ASSERT old-token: obsolete token was still accepted.'
Assert-KcpTrue ($null -eq $routeC_oldTokenRejected.Welcome) 'ASSERT old-token: obsolete token received Welcome.'

$routeC_oldGenerationRoute = Invoke-KcpRoute `
    $routeC_types `
    $routeC_reconnectRouter `
    (New-KcpMessage $routeC_types $routeC_reconnectSessionId 1 (New-KcpPayload 501)) `
    $routeC_reconnectEndpoint0
Assert-KcpEqual 'StaleGeneration' $routeC_oldGenerationRoute.Reason.ToString() 'ASSERT old-generation: obsolete generation was not rejected first.'
$routeC_oldEndpointRoute = Invoke-KcpRoute `
    $routeC_types `
    $routeC_reconnectRouter `
    (New-KcpMessage $routeC_types $routeC_reconnectSessionId 2 (New-KcpPayload 502)) `
    $routeC_reconnectEndpoint0
Assert-KcpEqual 'WrongEndpoint' $routeC_oldEndpointRoute.Reason.ToString() 'ASSERT old-endpoint: obsolete Endpoint remained valid.'
$routeC_oldConvRoute = Invoke-KcpRoute `
    $routeC_types `
    $routeC_reconnectRouter `
    (New-KcpMessage $routeC_types $routeC_reconnectSessionId 2 (New-KcpPayload 501)) `
    $routeC_reconnectEndpoint1
Assert-KcpEqual 'WrongConversation' $routeC_oldConvRoute.Reason.ToString() 'ASSERT old-conv: obsolete Conv remained valid.'
$routeC_newRoute = Invoke-KcpRoute `
    $routeC_types `
    $routeC_reconnectRouter `
    (New-KcpMessage $routeC_types $routeC_reconnectSessionId 2 (New-KcpPayload 502)) `
    $routeC_reconnectEndpoint1
Assert-KcpTrue $routeC_newRoute.Ok 'ASSERT reconnect-route: new identity did not route.'

$routeC_reconnectWelcome = Decode-KcpEnvelope $routeC_types $routeC_reconnect.Welcome
$routeC_reconnectWelcomeArguments = @(
    $routeC_reconnectWelcome.Payload,
    $null,
    [byte]0,
    [uint32]0,
    $null,
    0,
    0,
    $false)
$routeC_reconnectWelcomeOk = $routeC_types.EnvelopeCodec.GetMethod('TryDecodeWelcome').Invoke(
    $null,
    $routeC_reconnectWelcomeArguments)
Assert-KcpTrue $routeC_reconnectWelcomeOk 'ASSERT reconnect-welcome: payload did not decode.'
Assert-KcpTrue ([Linq.Enumerable]::SequenceEqual([byte[]]$routeC_reconnectNonce1, [byte[]]$routeC_reconnectWelcomeArguments[1])) 'ASSERT reconnect-welcome: new nonce echo changed.'
Assert-KcpTrue ([Linq.Enumerable]::SequenceEqual([byte[]]$routeC_newToken, [byte[]]$routeC_reconnectWelcomeArguments[4])) 'ASSERT reconnect-welcome: new token was not installed.'
Assert-KcpTrue $routeC_reconnectWelcomeArguments[7] 'ASSERT reconnect-welcome: reconnect must require resume.'

Write-Output 'PASS: KCP server routing, handshake, and reconnect generation isolation are correct.'
