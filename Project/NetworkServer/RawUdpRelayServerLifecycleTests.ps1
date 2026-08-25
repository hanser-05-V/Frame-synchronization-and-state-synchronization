param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath
)

$ErrorActionPreference = 'Stop'

function Assert-LifeTrue
{
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-LifeEqual
{
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) { throw "$Message Expected=$Expected Actual=$Actual" }
}

function Wait-Life
{
    param([scriptblock]$Condition, [int]$TimeoutMs, [string]$Message)
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    while ($routeC_watch.ElapsedMilliseconds -lt $TimeoutMs)
    {
        if (& $Condition) { return }
        [Threading.Thread]::Yield() | Out-Null
    }
    throw $Message
}

function New-LifeNonce
{
    param([byte]$Start)
    $routeC_nonce = [byte[]]::new(16)
    for ($routeC_index = 0; $routeC_index -lt 16; $routeC_index++)
    {
        $routeC_nonce[$routeC_index] = [byte]($Start + $routeC_index)
    }
    return ,$routeC_nonce
}

function Invoke-LifeStatic
{
    param([Type]$Type, [string]$Method, [object[]]$Arguments)
    return $Type.GetMethod($Method).Invoke($null, $Arguments)
}

function New-LifeEnvelope
{
    param($Types, [string]$Name, $Session, [uint32]$Generation, [byte[]]$Payload)
    $routeC_kind = [Enum]::Parse($Types.MessageType, $Name)
    return Invoke-LifeStatic `
        $Types.EnvelopeCodec `
        'Encode' `
        @($routeC_kind, $Session, $Generation, $Payload)
}

function Send-LifeHello
{
    param($Types, [Net.Sockets.Socket]$Client, [byte[]]$Nonce)
    $routeC_payload = Invoke-LifeStatic $Types.RawCodec 'EncodeHello' @(,$Nonce)
    $routeC_zero = $Types.SessionId.GetField('Zero').GetValue($null)
    $routeC_datagram = New-LifeEnvelope $Types 'RawHello' $routeC_zero 0 $routeC_payload
    $Client.Send($routeC_datagram) | Out-Null
}

function Receive-LifeMessage
{
    param($Types, [Net.Sockets.Socket]$Client, [string]$ExpectedType)
    $routeC_buffer = [byte[]]::new(1200)
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    while ($routeC_watch.ElapsedMilliseconds -lt 2000)
    {
        if (-not $Client.Poll(100000, [Net.Sockets.SelectMode]::SelectRead)) { continue }
        $routeC_count = $Client.Receive($routeC_buffer)
        $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
        $routeC_arguments = @($routeC_buffer, $routeC_count, $null, $routeC_reason)
        if ($Types.EnvelopeCodec.GetMethod('TryDecode').Invoke($null, $routeC_arguments))
        {
            $routeC_message = $routeC_arguments[2]
            if ($routeC_message.MessageType.ToString() -eq $ExpectedType)
            {
                return $routeC_message
            }
        }
    }
    throw "Timed out waiting for $ExpectedType"
}

function Send-LifeReady
{
    param($Types, [Net.Sockets.Socket]$Client, $Session)
    $routeC_payload = [byte[]]::new(0)
    $Client.Send((New-LifeEnvelope $Types 'RawReady' $Session 1 $routeC_payload)) | Out-Null
}

function Send-LifeInput
{
    param(
        $Types,
        [Net.Sockets.Socket]$Client,
        $Session,
        [byte]$PlayerIndex,
        [uint32]$Generation = 1)
    $routeC_entries = [Array]::CreateInstance($Types.InputEntry, 1)
    $routeC_entries.SetValue(
        [Activator]::CreateInstance($Types.InputEntry, @([uint32]10, 0)),
        0)
    $routeC_payload = Invoke-LifeStatic $Types.RawCodec 'EncodeInput' @(
        $PlayerIndex,
        [byte]6,
        [uint32]0,
        0,
        $routeC_entries)
    $Client.Send((New-LifeEnvelope `
        $Types `
        'RawInput' `
        $Session `
        $Generation `
        $routeC_payload)) | Out-Null
}

function Assert-LifeStop
{
    param($Server, [int]$LimitMs = 260)
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    $Server.RequestStop()
    Assert-LifeTrue ($Server.WaitForStop($LimitMs)) 'ASSERT stop-bound: server did not stop.'
    Assert-LifeTrue ($routeC_watch.ElapsedMilliseconds -le $LimitMs) 'ASSERT stop-bound: wait exceeded 260ms.'
    $Server.RequestStop()
    Assert-LifeTrue ($Server.WaitForStop($LimitMs)) 'ASSERT repeated-stop: second wait failed.'
}

Assert-LifeTrue (Test-Path -LiteralPath $ServerPath -PathType Leaf) 'Server assembly missing.'
$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_serverType = $routeC_assembly.GetType('FrameSyncServer.RawUdpRelayServer', $true)
$routeC_diagnosticsType = $routeC_assembly.GetType(
    'FrameSyncServer.RawUdpServerDiagnosticsSnapshot',
    $false)
$routeC_flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$routeC_fields = $routeC_serverType.GetFields($routeC_flags)
Assert-LifeEqual `
    1 `
    @($routeC_fields | Where-Object FieldType -eq ([Net.Sockets.Socket])).Count `
    'ASSERT one-socket: Raw server must own exactly one Socket field.'
Assert-LifeEqual `
    1 `
    @($routeC_fields | Where-Object FieldType -eq ([Threading.Thread])).Count `
    'ASSERT one-worker: Raw server must own exactly one Thread field.'
Assert-LifeTrue ($null -ne $routeC_diagnosticsType) 'ASSERT diagnostics: snapshot type missing.'

$routeC_source = Get-Content -LiteralPath (
    Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'RawUdpRelayServer.cs') -Raw
Assert-LifeTrue $routeC_source.Contains('Socket.Select') 'ASSERT select: Socket.Select missing.'
Assert-LifeTrue $routeC_source.Contains('RawMaximumSelectWaitMs') 'ASSERT select: 10ms bound missing.'
Assert-LifeTrue $routeC_source.Contains('MaximumDatagramsPerRound') 'ASSERT receive-budget: 64 bound missing.'
Assert-LifeTrue $routeC_source.Contains('MaximumMessagesPerSessionPerRound') 'ASSERT player-budget: 64 bound missing.'
Assert-LifeTrue $routeC_source.Contains('MaximumWorkerSliceMs') 'ASSERT fairness: 2ms recheck missing.'
$routeC_stopStart = $routeC_source.IndexOf('public void RequestStop()', [StringComparison]::Ordinal)
$routeC_stopEnd = $routeC_source.IndexOf('public bool WaitForStop', $routeC_stopStart, [StringComparison]::Ordinal)
$routeC_stopBody = $routeC_source.Substring($routeC_stopStart, $routeC_stopEnd - $routeC_stopStart)
Assert-LifeTrue (-not $routeC_stopBody.Contains('_socket')) 'ASSERT stop-ownership: caller touched Socket.'

$routeC_types = [pscustomobject]@{
    EnvelopeCodec = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolCodec', $true)
    RawCodec = $routeC_assembly.GetType('FrameSyncDemo.RawUdpProtocolCodec', $true)
    MessageType = $routeC_assembly.GetType('FrameSyncDemo.RouteCMessageType', $true)
    SessionId = $routeC_assembly.GetType('FrameSyncDemo.RouteCSessionId', $true)
    DropReason = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolDropReason', $true)
    Fault = $routeC_assembly.GetType('FrameSyncDemo.RawUdpFault', $true)
    InputEntry = $routeC_assembly.GetType('FrameSyncDemo.RawUdpInputEntry', $true)
}

$routeC_beforeStart = [Activator]::CreateInstance($routeC_serverType, @(0, 6))
try { Assert-LifeStop $routeC_beforeStart } finally { $routeC_beforeStart.Dispose() }

$routeC_occupiedSocket = [Net.Sockets.Socket]::new(
    [Net.Sockets.AddressFamily]::InterNetwork,
    [Net.Sockets.SocketType]::Dgram,
    [Net.Sockets.ProtocolType]::Udp)
$routeC_occupiedSocket.ExclusiveAddressUse = $true
$routeC_occupiedSocket.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, 0))
$routeC_occupiedPort = ([Net.IPEndPoint]$routeC_occupiedSocket.LocalEndPoint).Port
$routeC_faultedServer = [Activator]::CreateInstance(
    $routeC_serverType,
    @($routeC_occupiedPort, 6))
try
{
    $routeC_faultedServer.Run()
    Assert-LifeTrue ($routeC_faultedServer.WaitForStop(1000)) `
        'ASSERT worker-fault: bind failure did not stop worker.'
    Assert-LifeTrue $routeC_faultedServer.Diagnostics.WorkerFault `
        'ASSERT worker-fault: public diagnostics did not report failure.'
}
finally
{
    $routeC_faultedServer.Dispose()
    $routeC_occupiedSocket.Dispose()
}

$routeC_handshakeServer = [Activator]::CreateInstance($routeC_serverType, @(0, 6))
$routeC_handshakeClient = $null
try
{
    $routeC_handshakeServer.Run()
    Wait-Life { $routeC_handshakeServer.Diagnostics.BoundPort -gt 0 } 1000 `
        'ASSERT handshake-timeout: server did not bind.'
    $routeC_handshakeClient = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_handshakeClient.Connect(
        [Net.IPAddress]::Loopback,
        $routeC_handshakeServer.Diagnostics.BoundPort)
    Send-LifeHello $routeC_types $routeC_handshakeClient (New-LifeNonce 96)
    Receive-LifeMessage $routeC_types $routeC_handshakeClient 'RawWelcome' | Out-Null
    Wait-Life { $routeC_handshakeServer.Diagnostics.IsTerminal } 3500 `
        'ASSERT handshake-timeout: half-open match did not terminate at 3000ms.'
    Assert-LifeEqual `
        'HandshakeTimeout' `
        $routeC_handshakeServer.Diagnostics.TerminalReason.ToString() `
        'ASSERT handshake-timeout: diagnostic reason drifted.'
    Assert-LifeEqual `
        255 `
        $routeC_handshakeServer.Diagnostics.TerminalPlayerIndex `
        'ASSERT handshake-timeout: single-slot timeout must be unassigned.'
    Assert-LifeTrue ($routeC_handshakeServer.WaitForStop(1000)) `
        'ASSERT handshake-timeout: terminal repeats did not finish.'
    Assert-LifeTrue `
        ($routeC_handshakeServer.Diagnostics.TerminalFaultRepeatDatagramsSent -eq 5) `
        'ASSERT fault-repeat: handshake fault was not repeated exactly five times.'
}
finally
{
    if ($routeC_handshakeClient) { $routeC_handshakeClient.Dispose() }
    $routeC_handshakeServer.Dispose()
}

$routeC_server = [Activator]::CreateInstance($routeC_serverType, @(0, 6))
$routeC_client0 = $null
$routeC_client1 = $null
try
{
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    $routeC_server.Run()
    Assert-LifeTrue ($routeC_watch.ElapsedMilliseconds -lt 100) 'ASSERT run-immediate: Run blocked caller.'
    Wait-Life { $routeC_server.Diagnostics.BoundPort -gt 0 } 1000 'ASSERT bind: port was not published.'
    $routeC_port = $routeC_server.Diagnostics.BoundPort
    $routeC_client0 = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_client1 = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_client0.Connect([Net.IPAddress]::Loopback, $routeC_port)
    $routeC_client1.Connect([Net.IPAddress]::Loopback, $routeC_port)
    Send-LifeHello $routeC_types $routeC_client0 (New-LifeNonce 16)
    Send-LifeHello $routeC_types $routeC_client1 (New-LifeNonce 48)
    $routeC_welcome0 = Receive-LifeMessage $routeC_types $routeC_client0 'RawWelcome'
    $routeC_welcome1 = Receive-LifeMessage $routeC_types $routeC_client1 'RawWelcome'
    Send-LifeReady $routeC_types $routeC_client0 $routeC_welcome0.SessionId
    Send-LifeReady $routeC_types $routeC_client1 $routeC_welcome1.SessionId
    Receive-LifeMessage $routeC_types $routeC_client0 'RawStart' | Out-Null
    Receive-LifeMessage $routeC_types $routeC_client1 'RawStart' | Out-Null
    Wait-Life { $routeC_server.Diagnostics.PlayerCount -eq 2 } 1000 'ASSERT players: stable 1v1 slots missing.'
    Assert-LifeTrue ($routeC_server.Diagnostics.MaximumDatagramsReceivedPerRound -le 64) 'ASSERT receive-budget: exceeded 64.'
    Assert-LifeTrue ($routeC_server.Diagnostics.MaximumPlayerWorkItemsPerRound -le 64) 'ASSERT player-budget: exceeded 64.'
    Assert-LifeTrue ($routeC_server.Diagnostics.MaximumSelectWaitMilliseconds -le 10) 'ASSERT select: exceeded 10ms.'
    Assert-LifeStop $routeC_server
}
finally
{
    if ($routeC_client0) { $routeC_client0.Dispose() }
    if ($routeC_client1) { $routeC_client1.Dispose() }
    $routeC_server.Dispose()
}

$routeC_preStartServer = [Activator]::CreateInstance($routeC_serverType, @(0, 6))
$routeC_preStartClient0 = $null
$routeC_preStartClient1 = $null
try
{
    $routeC_preStartServer.Run()
    Wait-Life { $routeC_preStartServer.Diagnostics.BoundPort -gt 0 } 1000 `
        'ASSERT pre-start-input: server did not bind.'
    $routeC_preStartClient0 = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_preStartClient1 = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_preStartClient0.Connect(
        [Net.IPAddress]::Loopback,
        $routeC_preStartServer.Diagnostics.BoundPort)
    $routeC_preStartClient1.Connect(
        [Net.IPAddress]::Loopback,
        $routeC_preStartServer.Diagnostics.BoundPort)
    Send-LifeHello $routeC_types $routeC_preStartClient0 (New-LifeNonce 64)
    Send-LifeHello $routeC_types $routeC_preStartClient1 (New-LifeNonce 80)
    $routeC_preStartWelcome0 = Receive-LifeMessage `
        $routeC_types $routeC_preStartClient0 'RawWelcome'
    $routeC_preStartWelcome1 = Receive-LifeMessage `
        $routeC_types $routeC_preStartClient1 'RawWelcome'
    Send-LifeReady `
        $routeC_types `
        $routeC_preStartClient1 `
        $routeC_preStartWelcome1.SessionId
    [Threading.Thread]::Sleep(20)
    $routeC_zeroField = $routeC_types.SessionId.GetField('Zero')
    $routeC_zeroSession = $routeC_zeroField.GetValue($null)
    Send-LifeInput `
        $routeC_types `
        $routeC_preStartClient1 `
        $routeC_zeroSession `
        1
    Send-LifeInput `
        $routeC_types `
        $routeC_preStartClient1 `
        $routeC_preStartWelcome1.SessionId `
        1 `
        2
    Send-LifeInput `
        $routeC_types `
        $routeC_preStartClient1 `
        $routeC_preStartWelcome0.SessionId `
        1
    [Threading.Thread]::Sleep(20)
    Assert-LifeTrue (-not $routeC_preStartServer.Diagnostics.IsTerminal) `
        'ASSERT pre-start-input: silent rejection matrix terminated the match.'
    Send-LifeInput `
        $routeC_types `
        $routeC_preStartClient1 `
        $routeC_preStartWelcome1.SessionId `
        1
    Send-LifeReady `
        $routeC_types `
        $routeC_preStartClient0 `
        $routeC_preStartWelcome0.SessionId
    Wait-Life { $routeC_preStartServer.Diagnostics.IsTerminal } 1000 `
        'ASSERT pre-start-input: input received before relay enable was accepted.'
    Assert-LifeEqual `
        'ProtocolViolation' `
        $routeC_preStartServer.Diagnostics.TerminalReason.ToString() `
        'ASSERT pre-start-input: wrong terminal reason.'
    Assert-LifeEqual `
        1 `
        $routeC_preStartServer.Diagnostics.TerminalPlayerIndex `
        'ASSERT pre-start-input: wrong terminal player.'
    Assert-LifeTrue ($routeC_preStartServer.WaitForStop(1000)) `
        'ASSERT pre-start-input: terminal repeats did not finish.'
}
finally
{
    if ($routeC_preStartClient0) { $routeC_preStartClient0.Dispose() }
    if ($routeC_preStartClient1) { $routeC_preStartClient1.Dispose() }
    $routeC_preStartServer.Dispose()
}

$routeC_runningTimeoutServer = [Activator]::CreateInstance($routeC_serverType, @(0, 6))
$routeC_timeoutClient0 = $null
$routeC_timeoutClient1 = $null
try
{
    $routeC_runningTimeoutServer.Run()
    Wait-Life { $routeC_runningTimeoutServer.Diagnostics.BoundPort -gt 0 } 1000 `
        'ASSERT running-timeout: server did not bind.'
    $routeC_timeoutClient0 = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_timeoutClient1 = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_timeoutClient0.Connect(
        [Net.IPAddress]::Loopback,
        $routeC_runningTimeoutServer.Diagnostics.BoundPort)
    $routeC_timeoutClient1.Connect(
        [Net.IPAddress]::Loopback,
        $routeC_runningTimeoutServer.Diagnostics.BoundPort)
    Send-LifeHello $routeC_types $routeC_timeoutClient0 (New-LifeNonce 112)
    Send-LifeHello $routeC_types $routeC_timeoutClient1 (New-LifeNonce 144)
    $routeC_timeoutWelcome0 = Receive-LifeMessage $routeC_types $routeC_timeoutClient0 'RawWelcome'
    $routeC_timeoutWelcome1 = Receive-LifeMessage $routeC_types $routeC_timeoutClient1 'RawWelcome'
    Send-LifeReady $routeC_types $routeC_timeoutClient0 $routeC_timeoutWelcome0.SessionId
    Send-LifeReady $routeC_types $routeC_timeoutClient1 $routeC_timeoutWelcome1.SessionId
    Receive-LifeMessage $routeC_types $routeC_timeoutClient0 'RawStart' | Out-Null
    Receive-LifeMessage $routeC_types $routeC_timeoutClient1 'RawStart' | Out-Null
    $routeC_keepAliveWatch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $routeC_runningTimeoutServer.Diagnostics.IsTerminal -and
        $routeC_keepAliveWatch.ElapsedMilliseconds -lt 3500)
    {
        Send-LifeReady `
            $routeC_types `
            $routeC_timeoutClient1 `
            $routeC_timeoutWelcome1.SessionId
        [Threading.Thread]::Sleep(25)
    }
    Assert-LifeTrue $routeC_runningTimeoutServer.Diagnostics.IsTerminal `
        'ASSERT running-timeout: silent player zero did not terminate match.'
    Assert-LifeEqual `
        'ConnectionTimedOut' `
        $routeC_runningTimeoutServer.Diagnostics.TerminalReason.ToString() `
        'ASSERT running-timeout: diagnostic reason drifted.'
    Assert-LifeEqual `
        0 `
        $routeC_runningTimeoutServer.Diagnostics.TerminalPlayerIndex `
        'ASSERT running-timeout: fault was not attributed to silent player zero.'
    Assert-LifeTrue ($routeC_runningTimeoutServer.WaitForStop(1000)) `
        'ASSERT running-timeout: terminal repeats did not finish.'
    Assert-LifeEqual `
        10 `
        $routeC_runningTimeoutServer.Diagnostics.TerminalFaultRepeatDatagramsSent `
        'ASSERT running-timeout: terminal fault repeat count drifted.'
}
finally
{
    if ($routeC_timeoutClient0) { $routeC_timeoutClient0.Dispose() }
    if ($routeC_timeoutClient1) { $routeC_timeoutClient1.Dispose() }
    $routeC_runningTimeoutServer.Dispose()
}

$routeC_playerOneTimeoutServer = [Activator]::CreateInstance($routeC_serverType, @(0, 6))
$routeC_activeClient0 = $null
$routeC_silentClient1 = $null
try
{
    $routeC_playerOneTimeoutServer.Run()
    Wait-Life { $routeC_playerOneTimeoutServer.Diagnostics.BoundPort -gt 0 } 1000 `
        'ASSERT player1-timeout: server did not bind.'
    $routeC_activeClient0 = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_silentClient1 = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_activeClient0.Connect(
        [Net.IPAddress]::Loopback,
        $routeC_playerOneTimeoutServer.Diagnostics.BoundPort)
    $routeC_silentClient1.Connect(
        [Net.IPAddress]::Loopback,
        $routeC_playerOneTimeoutServer.Diagnostics.BoundPort)
    Send-LifeHello $routeC_types $routeC_activeClient0 (New-LifeNonce 216)
    Send-LifeHello $routeC_types $routeC_silentClient1 (New-LifeNonce 232)
    $routeC_activeWelcome0 = Receive-LifeMessage $routeC_types $routeC_activeClient0 'RawWelcome'
    $routeC_silentWelcome1 = Receive-LifeMessage $routeC_types $routeC_silentClient1 'RawWelcome'
    Send-LifeReady $routeC_types $routeC_activeClient0 $routeC_activeWelcome0.SessionId
    Send-LifeReady $routeC_types $routeC_silentClient1 $routeC_silentWelcome1.SessionId
    Receive-LifeMessage $routeC_types $routeC_activeClient0 'RawStart' | Out-Null
    Receive-LifeMessage $routeC_types $routeC_silentClient1 'RawStart' | Out-Null
    $routeC_playerOneWatch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $routeC_playerOneTimeoutServer.Diagnostics.IsTerminal -and
        $routeC_playerOneWatch.ElapsedMilliseconds -lt 3500)
    {
        Send-LifeReady $routeC_types $routeC_activeClient0 $routeC_activeWelcome0.SessionId
        [Threading.Thread]::Sleep(25)
    }
    Assert-LifeTrue $routeC_playerOneTimeoutServer.Diagnostics.IsTerminal `
        'ASSERT player1-timeout: silent player one did not terminate match.'
    Assert-LifeEqual `
        1 `
        $routeC_playerOneTimeoutServer.Diagnostics.TerminalPlayerIndex `
        'ASSERT player1-timeout: fault was not attributed to silent player one.'
    Assert-LifeTrue ($routeC_playerOneTimeoutServer.WaitForStop(1000)) `
        'ASSERT player1-timeout: terminal repeats did not finish.'
}
finally
{
    if ($routeC_activeClient0) { $routeC_activeClient0.Dispose() }
    if ($routeC_silentClient1) { $routeC_silentClient1.Dispose() }
    $routeC_playerOneTimeoutServer.Dispose()
}

$routeC_repeatStopServer = [Activator]::CreateInstance($routeC_serverType, @(0, 6))
$routeC_repeatStopClient = $null
try
{
    $routeC_repeatStopServer.Run()
    Wait-Life { $routeC_repeatStopServer.Diagnostics.BoundPort -gt 0 } 1000 `
        'ASSERT repeat-stop: server did not bind.'
    $routeC_repeatStopClient = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram,
        [Net.Sockets.ProtocolType]::Udp)
    $routeC_repeatStopClient.Connect(
        [Net.IPAddress]::Loopback,
        $routeC_repeatStopServer.Diagnostics.BoundPort)
    Send-LifeHello $routeC_types $routeC_repeatStopClient (New-LifeNonce 176)
    Receive-LifeMessage $routeC_types $routeC_repeatStopClient 'RawWelcome' | Out-Null
    Send-LifeHello $routeC_types $routeC_repeatStopClient (New-LifeNonce 192)
    Wait-Life { $routeC_repeatStopServer.Diagnostics.IsTerminal } 1000 `
        'ASSERT repeat-stop: protocol fault did not enter terminal state.'
    Assert-LifeStop $routeC_repeatStopServer
    Assert-LifeTrue `
        ($routeC_repeatStopServer.Diagnostics.TerminalFaultRepeatDatagramsSent -lt 5) `
        'ASSERT repeat-stop: stop request did not interrupt terminal repeats.'
}
finally
{
    if ($routeC_repeatStopClient) { $routeC_repeatStopClient.Dispose() }
    $routeC_repeatStopServer.Dispose()
}

Write-Output 'PASS: Raw UDP relay server one-worker lifecycle, budgets, and bounded stop are correct.'
