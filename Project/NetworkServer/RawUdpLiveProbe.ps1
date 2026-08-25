param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath,
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        'clean',
        'first-control-loss',
        'drop-1-through-5',
        'aligned-6-uplink',
        'aligned-6-downlink',
        'mixed-faults',
        'foreign-injection',
        'bound-conflict',
        'endpoint-change-silence',
        'window-16',
        'bounded-stop',
        'all')]
    [string]$Scenario,
    [ValidateRange(1, 16)]
    [int]$WindowSize = 6,
    [ValidateRange(1, 100000)]
    [int]$FramesPerClient = 900,
    [uint32]$Seed = 20260820,
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'

function Assert-Probe
{
    param([bool]$Condition, [string]$Stage, [string]$Message)
    if (-not $Condition) { throw "STAGE ${Stage}: $Message" }
}

function Invoke-Static
{
    param([Type]$Type, [string]$Method, [object[]]$Arguments)
    return $Type.GetMethod($Method).Invoke($null, $Arguments)
}

function New-Nonce
{
    param([byte]$Start)
    $routeC_nonce = [byte[]]::new(16)
    for ($routeC_index = 0; $routeC_index -lt 16; $routeC_index++)
    {
        $routeC_nonce[$routeC_index] = [byte]($Start + $routeC_index)
    }
    return ,$routeC_nonce
}

function New-Envelope
{
    param($Types, [string]$Name, $Session, [uint32]$Generation, [byte[]]$Payload)
    $routeC_kind = [Enum]::Parse($Types.MessageType, $Name)
    return Invoke-Static $Types.EnvelopeCodec 'Encode' @(
        $routeC_kind,
        $Session,
        $Generation,
        $Payload)
}

function Decode-Envelope
{
    param($Types, [byte[]]$Datagram, [int]$Count)
    $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
    $routeC_arguments = @($Datagram, $Count, $null, $routeC_reason)
    $routeC_ok = Invoke-Static $Types.EnvelopeCodec 'TryDecode' $routeC_arguments
    Assert-Probe $routeC_ok 'decode' 'received an invalid Route C envelope.'
    return $routeC_arguments[2]
}

function Receive-Message
{
    param(
        $Types,
        [Net.Sockets.Socket]$Client,
        [int]$TimeoutMs,
        [string]$ExpectedType = '')
    $routeC_buffer = [byte[]]::new(1200)
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    while ($routeC_watch.ElapsedMilliseconds -lt $TimeoutMs)
    {
        if (-not $Client.Poll(10000, [Net.Sockets.SelectMode]::SelectRead))
        {
            continue
        }
        $routeC_count = $Client.Receive($routeC_buffer)
        $routeC_message = Decode-Envelope $Types $routeC_buffer $routeC_count
        if ([string]::IsNullOrEmpty($ExpectedType) -or
            $routeC_message.MessageType.ToString() -eq $ExpectedType)
        {
            return [pscustomobject]@{
                Message = $routeC_message
                Bytes = $routeC_count
            }
        }
    }
    return $null
}

function Send-Hello
{
    param($Types, [Net.Sockets.Socket]$Client, [byte[]]$Nonce)
    $Client.Send((New-HelloDatagram $Types $Nonce)) | Out-Null
}

function New-HelloDatagram
{
    param($Types, [byte[]]$Nonce)
    $routeC_payload = Invoke-Static $Types.RawCodec 'EncodeHello' @(,$Nonce)
    $routeC_zero = $Types.SessionId.GetField('Zero').GetValue($null)
    return New-Envelope $Types 'RawHello' $routeC_zero 0 $routeC_payload
}

function Send-Ready
{
    param($Types, [Net.Sockets.Socket]$Client, $Session)
    $Client.Send((New-ReadyDatagram $Types $Session)) | Out-Null
}

function New-ReadyDatagram
{
    param($Types, $Session)
    return New-Envelope $Types 'RawReady' $Session 1 ([byte[]]::new(0))
}

function New-InputDatagram
{
    param(
        $Types,
        $Session,
        [byte]$PlayerIndex,
        [uint32]$Sequence,
        [int]$LatestFrameID,
        [byte]$Window,
        [int]$ConflictingFrameID = -1)
    $routeC_count = [Math]::Min([int]$Window, $LatestFrameID + 1)
    $routeC_first = $LatestFrameID - $routeC_count + 1
    $routeC_entries = [Array]::CreateInstance($Types.InputEntry, $routeC_count)
    for ($routeC_index = 0; $routeC_index -lt $routeC_count; $routeC_index++)
    {
        $routeC_frame = $routeC_first + $routeC_index
        $routeC_raw = [uint32](1 + ($PlayerIndex * 1000000) + $routeC_frame)
        if ($routeC_frame -eq $ConflictingFrameID)
        {
            $routeC_raw = [uint32]($routeC_raw + 77)
        }
        $routeC_entry = [Activator]::CreateInstance(
            $Types.InputEntry,
            @($routeC_raw, $routeC_frame))
        $routeC_entries.SetValue($routeC_entry, $routeC_index)
    }
    $routeC_payload = Invoke-Static $Types.RawCodec 'EncodeInput' @(
        $PlayerIndex,
        $Window,
        $Sequence,
        $LatestFrameID,
        $routeC_entries)
    return New-Envelope $Types 'RawInput' $Session 1 $routeC_payload
}

function Send-Fault
{
    param(
        $Types,
        [Net.Sockets.Socket]$Client,
        $Session,
        [string]$Reason,
        [byte]$PlayerIndex,
        [int]$FrameID,
        [int]$ObservedLatest)
    $routeC_reason = [Enum]::Parse($Types.FaultReason, $Reason)
    $routeC_fault = [Activator]::CreateInstance(
        $Types.Fault,
        @($routeC_reason, $PlayerIndex, $FrameID, $ObservedLatest))
    $routeC_payload = Invoke-Static $Types.RawCodec 'EncodeFault' @($routeC_fault)
    $Client.Send((New-Envelope $Types 'RawFault' $Session 1 $routeC_payload)) |
        Out-Null
}

function Decode-Input
{
    param($Types, $Message, [byte]$Window)
    $routeC_reason = [Enum]::ToObject($Types.DropReason, 0)
    $routeC_arguments = @($Message.Payload, $Window, $null, $routeC_reason)
    $routeC_ok = Invoke-Static $Types.RawCodec 'TryDecodeInput' $routeC_arguments
    Assert-Probe $routeC_ok 'input-decode' 'server relayed an invalid RawInput.'
    return $routeC_arguments[2]
}

function Decode-Fault
{
    param($Types, $Message)
    $routeC_fault = [Activator]::CreateInstance($Types.Fault)
    $routeC_arguments = @($Message.Payload, $routeC_fault)
    $routeC_ok = Invoke-Static $Types.RawCodec 'TryDecodeFault' $routeC_arguments
    Assert-Probe $routeC_ok 'fault-decode' 'server sent an invalid RawFault.'
    return $routeC_arguments[1]
}

function New-ClientState
{
    param([int]$Index, $Session, [int]$Frames)
    return [pscustomobject]@{
        Index = $Index
        Session = $Session
        Accepted = @{}
        Publications = [int[]]::new($Frames)
        DatagramCount = 0L
        ByteCount = 0L
        SequenceHighWater = -1L
        DuplicateWindows = 0L
        ReorderedWindows = 0L
        DroppedWindows = 0L
        DelayedWindows = 0L
        LastSequence = -1L
        DefaultSizeObserved = $false
        Fault = $null
        Held = $null
    }
}

function Accept-Window
{
    param($State, $Window, [string]$ScenarioName, [int]$Frames)
    $routeC_sequence = [uint32]$Window.PacketSequence
    if ($State.LastSequence -ge 0 -and $routeC_sequence -lt $State.LastSequence)
    {
        $State.ReorderedWindows++
    }
    if ($routeC_sequence -gt $State.SequenceHighWater)
    {
        $State.SequenceHighWater = [long]$routeC_sequence
    }
    $State.LastSequence = [long]$routeC_sequence

    $routeC_expectedPlayer = 1 - $State.Index
    Assert-Probe `
        ($Window.PlayerIndex -eq $routeC_expectedPlayer) `
        'relay-player' `
        'relay changed the original payload PlayerIndex.'
    foreach ($routeC_entry in $Window.Entries)
    {
        $routeC_frame = [int]$routeC_entry.FrameID
        $routeC_raw = [uint32]$routeC_entry.Raw
        if ($State.Accepted.ContainsKey($routeC_frame))
        {
            Assert-Probe `
                ([uint32]$State.Accepted[$routeC_frame] -eq $routeC_raw) `
                'immutable-input' `
                'same frame arrived with a different raw.'
            continue
        }
        $State.Accepted[$routeC_frame] = $routeC_raw
        if ($routeC_frame -lt $Frames)
        {
            $State.Publications[$routeC_frame]++
        }
    }
}

function Drain-Client
{
    param(
        $Types,
        [Net.Sockets.Socket]$Client,
        $State,
        [byte]$Window,
        [string]$ScenarioName,
        [int]$Frames)
    $routeC_buffer = [byte[]]::new(1200)
    while ($Client.Poll(0, [Net.Sockets.SelectMode]::SelectRead))
    {
        $routeC_count = $Client.Receive($routeC_buffer)
        $routeC_message = Decode-Envelope $Types $routeC_buffer $routeC_count
        if ($routeC_message.MessageType.ToString() -eq 'RawInput')
        {
            Assert-Probe `
                $routeC_message.SessionId.Equals($State.Session) `
                'relay-session' `
                'server did not use the receiver SessionID.'
            Assert-Probe ($routeC_message.Generation -eq 1) 'relay-generation' 'generation changed.'
            $routeC_window = Decode-Input $Types $routeC_message $Window

            if ($ScenarioName -eq 'aligned-6-downlink' -and
                $State.Index -eq 1 -and
                $routeC_window.PacketSequence -le 5)
            {
                continue
            }

            if ($ScenarioName -eq 'mixed-faults')
            {
                $routeC_faultRoll = [uint32]($routeC_window.PacketSequence + $Seed)
                if (($routeC_faultRoll % 17) -eq 0)
                {
                    $State.DroppedWindows++
                    continue
                }
                [Threading.Thread]::Sleep([int](1 + ($routeC_faultRoll % 3)))
                $State.DelayedWindows++
            }

            if ($ScenarioName -eq 'mixed-faults' -and
                (($routeC_window.PacketSequence + $Seed) % 7) -eq 0)
            {
                if ($null -eq $State.Held)
                {
                    $State.Held = $routeC_window
                    continue
                }
            }

            Accept-Window $State $routeC_window $ScenarioName $Frames
            if ($ScenarioName -eq 'mixed-faults' -and
                (($routeC_window.PacketSequence + $Seed) % 5) -eq 0)
            {
                Accept-Window $State $routeC_window $ScenarioName $Frames
                $State.DuplicateWindows++
            }
            if ($null -ne $State.Held)
            {
                Accept-Window $State $State.Held $ScenarioName $Frames
                $State.Held = $null
            }
            $State.DatagramCount++
            $State.ByteCount += $routeC_count
            $routeC_expectedSize = if ($Window -eq 16) { 168 } else { 88 }
            if ($routeC_count -eq $routeC_expectedSize)
            {
                $State.DefaultSizeObserved = $true
            }
        }
        elseif ($routeC_message.MessageType.ToString() -eq 'RawFault')
        {
            $State.Fault = Decode-Fault $Types $routeC_message
        }
    }
}

function New-LiveFixture
{
    param($Types, [int]$Window, [bool]$DropFirstControls)
    $routeC_server = [Activator]::CreateInstance($Types.Server, @(0, $Window))
    $routeC_server.Run()
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    while ($routeC_server.Diagnostics.BoundPort -le 0 -and
        $routeC_watch.ElapsedMilliseconds -lt 2000)
    {
        [Threading.Thread]::Yield() | Out-Null
    }
    Assert-Probe ($routeC_server.Diagnostics.BoundPort -gt 0) 'bind' 'server did not bind.'

    $routeC_clients = @()
    for ($routeC_index = 0; $routeC_index -lt 2; $routeC_index++)
    {
        $routeC_client = [Net.Sockets.Socket]::new(
            [Net.Sockets.AddressFamily]::InterNetwork,
            [Net.Sockets.SocketType]::Dgram,
            [Net.Sockets.ProtocolType]::Udp)
        $routeC_client.Connect(
            [Net.IPAddress]::Loopback,
            $routeC_server.Diagnostics.BoundPort)
        $routeC_clients += $routeC_client
    }

    if ($DropFirstControls)
    {
        $null = New-HelloDatagram $Types (New-Nonce 16)
        $null = New-HelloDatagram $Types (New-Nonce 48)
        [Threading.Thread]::Sleep(50)
    }
    Send-Hello $Types $routeC_clients[0] (New-Nonce 16)
    Send-Hello $Types $routeC_clients[1] (New-Nonce 48)

    $routeC_welcome0 = Receive-Message $Types $routeC_clients[0] 2000 'RawWelcome'
    $routeC_welcome1 = Receive-Message $Types $routeC_clients[1] 2000 'RawWelcome'
    Assert-Probe ($null -ne $routeC_welcome0 -and $null -ne $routeC_welcome1) 'welcome' 'both Welcome messages were not received.'
    if ($DropFirstControls)
    {
        # Discard the first Welcome, then prove duplicate Hello is idempotent.
        Send-Hello $Types $routeC_clients[0] (New-Nonce 16)
        $routeC_welcome0 = Receive-Message $Types $routeC_clients[0] 2000 'RawWelcome'
        Assert-Probe ($null -ne $routeC_welcome0) 'welcome-retry' 'dropped Welcome was not retried idempotently.'
        [Threading.Thread]::Sleep(50)
    }

    if ($DropFirstControls)
    {
        $null = New-ReadyDatagram $Types $routeC_welcome0.Message.SessionId
        $null = New-ReadyDatagram $Types $routeC_welcome1.Message.SessionId
        [Threading.Thread]::Sleep(50)
    }
    Send-Ready $Types $routeC_clients[0] $routeC_welcome0.Message.SessionId
    Send-Ready $Types $routeC_clients[1] $routeC_welcome1.Message.SessionId
    $routeC_start0 = Receive-Message $Types $routeC_clients[0] 2000 'RawStart'
    $routeC_start1 = Receive-Message $Types $routeC_clients[1] 2000 'RawStart'
    Assert-Probe ($null -ne $routeC_start0 -and $null -ne $routeC_start1) 'start' 'both Start messages were not received.'
    if ($DropFirstControls)
    {
        # The first Start was consumed above and intentionally not delivered.
        Send-Ready $Types $routeC_clients[0] $routeC_welcome0.Message.SessionId
        $routeC_start0 = Receive-Message $Types $routeC_clients[0] 2000 'RawStart'
        Assert-Probe ($null -ne $routeC_start0) 'start-retry' 'dropped Start was not retried.'
    }

    return [pscustomobject]@{
        Server = $routeC_server
        Clients = $routeC_clients
        Sessions = @(
            $routeC_welcome0.Message.SessionId,
            $routeC_welcome1.Message.SessionId)
        DroppedFirstControls = if ($DropFirstControls)
        {
            @('RawHello', 'RawWelcome', 'RawReady', 'RawStart')
        }
        else
        {
            @()
        }
    }
}

function Close-LiveFixture
{
    param($Fixture)
    if ($null -eq $Fixture) { return }
    foreach ($routeC_client in $Fixture.Clients)
    {
        if ($null -ne $routeC_client) { $routeC_client.Dispose() }
    }
    if ($null -ne $Fixture.Server)
    {
        $Fixture.Server.RequestStop()
        $Fixture.Server.WaitForStop(260) | Out-Null
        $Fixture.Server.Dispose()
    }
}

function Invoke-ProductionControlLoss
{
    param($Types)
    $routeC_server = [Activator]::CreateInstance($Types.Server, @(0, 6))
    $routeC_proxies = @()
    $routeC_transports = @()
    $routeC_clientEndpoints = @($null, $null)
    $routeC_dropTimes = @{}
    $routeC_retryDelays = @{}
    $routeC_earlyStartCopies = 0
    try
    {
        $routeC_server.Run()
        $routeC_bindWatch = [Diagnostics.Stopwatch]::StartNew()
        while ($routeC_server.Diagnostics.BoundPort -le 0 -and
            $routeC_bindWatch.ElapsedMilliseconds -lt 1000)
        {
            [Threading.Thread]::Yield() | Out-Null
        }
        Assert-Probe ($routeC_server.Diagnostics.BoundPort -gt 0) `
            'production-control-loss' `
            'server did not bind.'
        $routeC_serverEndpoint = [Net.IPEndPoint]::new(
            [Net.IPAddress]::Loopback,
            $routeC_server.Diagnostics.BoundPort)

        $routeC_controlWatch = [Diagnostics.Stopwatch]::StartNew()
        for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
        {
            $routeC_proxy = [Net.Sockets.Socket]::new(
                [Net.Sockets.AddressFamily]::InterNetwork,
                [Net.Sockets.SocketType]::Dgram,
                [Net.Sockets.ProtocolType]::Udp)
            $routeC_proxy.Bind([Net.IPEndPoint]::new(
                [Net.IPAddress]::Loopback,
                0))
            $routeC_proxies += $routeC_proxy

            $routeC_clock = [Activator]::CreateInstance($Types.Clock)
            $routeC_factoryType = [Func``1].MakeGenericType(@([byte[]]))
            $routeC_factoryMethod = if ($routeC_player -eq 0)
            {
                'CreatePlayerZero'
            }
            else
            {
                'CreatePlayerOne'
            }
            $routeC_nonceMethod =
                $Types.NonceFactory.GetMethod($routeC_factoryMethod)
            $routeC_nonceFactory =
                $routeC_nonceMethod.CreateDelegate($routeC_factoryType)
            $routeC_transport = [Activator]::CreateInstance(
                $Types.RawTransport,
                @($routeC_clock, $routeC_nonceFactory))
            $routeC_transports += $routeC_transport
            $routeC_transport.Start(
                '127.0.0.1',
                ([Net.IPEndPoint]$routeC_proxy.LocalEndPoint).Port)

            $routeC_firstBuffer = [byte[]]::new(1200)
            [Net.EndPoint]$routeC_firstRemote = [Net.IPEndPoint]::new(
                [Net.IPAddress]::Any,
                0)
            $routeC_firstWatch = [Diagnostics.Stopwatch]::StartNew()
            while (-not $routeC_proxy.Poll(
                    10000,
                    [Net.Sockets.SelectMode]::SelectRead) -and
                $routeC_firstWatch.ElapsedMilliseconds -lt 1000)
            {
            }
            Assert-Probe `
                ($routeC_proxy.Poll(0, [Net.Sockets.SelectMode]::SelectRead)) `
                'production-control-loss' `
                'production client did not emit its first Hello.'
            $routeC_firstCount = $routeC_proxy.ReceiveFrom(
                $routeC_firstBuffer,
                [ref]$routeC_firstRemote)
            $routeC_firstMessage = Decode-Envelope `
                $Types `
                $routeC_firstBuffer `
                $routeC_firstCount
            Assert-Probe `
                ($routeC_firstMessage.MessageType.ToString() -eq 'RawHello') `
                'production-control-loss' `
                'first production control was not RawHello.'
            $routeC_clientEndpoints[$routeC_player] = $routeC_firstRemote
            $routeC_dropTimes["client-$routeC_player-RawHello"] =
                $routeC_controlWatch.ElapsedMilliseconds
        }

        while (($routeC_transports[0].State.ToString() -ne 'Running' -or
                $routeC_transports[1].State.ToString() -ne 'Running') -and
            $routeC_controlWatch.ElapsedMilliseconds -lt 5000)
        {
            for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
            {
                $routeC_proxy = $routeC_proxies[$routeC_player]
                while ($routeC_proxy.Poll(
                    0,
                    [Net.Sockets.SelectMode]::SelectRead))
                {
                    $routeC_buffer = [byte[]]::new(1200)
                    [Net.EndPoint]$routeC_remote = [Net.IPEndPoint]::new(
                        [Net.IPAddress]::Any,
                        0)
                    $routeC_count = $routeC_proxy.ReceiveFrom(
                        $routeC_buffer,
                        [ref]$routeC_remote)
                    $routeC_message = Decode-Envelope `
                        $Types `
                        $routeC_buffer `
                        $routeC_count
                    $routeC_fromServer =
                        ([Net.IPEndPoint]$routeC_remote).Port -eq
                        $routeC_serverEndpoint.Port
                    $routeC_direction = if ($routeC_fromServer)
                    {
                        'server'
                    }
                    else
                    {
                        'client'
                    }
                    if (-not $routeC_fromServer)
                    {
                        $routeC_clientEndpoints[$routeC_player] = $routeC_remote
                    }
                    $routeC_typeName = $routeC_message.MessageType.ToString()
                    $routeC_isControlled =
                        ($routeC_direction -eq 'client' -and
                         ($routeC_typeName -eq 'RawHello' -or
                          $routeC_typeName -eq 'RawReady')) -or
                        ($routeC_direction -eq 'server' -and
                         ($routeC_typeName -eq 'RawWelcome' -or
                          $routeC_typeName -eq 'RawStart'))
                    $routeC_key =
                        "$routeC_direction-$routeC_player-$routeC_typeName"
                    if ($routeC_isControlled -and
                        -not $routeC_dropTimes.ContainsKey($routeC_key))
                    {
                        $routeC_dropTimes[$routeC_key] =
                            $routeC_controlWatch.ElapsedMilliseconds
                        continue
                    }
                    if ($routeC_isControlled -and
                        -not $routeC_retryDelays.ContainsKey($routeC_key))
                    {
                        $routeC_candidateDelay =
                            $routeC_controlWatch.ElapsedMilliseconds -
                            $routeC_dropTimes[$routeC_key]
                        if ($routeC_typeName -eq 'RawStart' -and
                            $routeC_candidateDelay -lt 40)
                        {
                            $routeC_earlyStartCopies++
                            continue
                        }
                        $routeC_retryDelays[$routeC_key] =
                            $routeC_candidateDelay
                    }

                    $routeC_copy = [byte[]]::new($routeC_count)
                    [Buffer]::BlockCopy(
                        $routeC_buffer,
                        0,
                        $routeC_copy,
                        0,
                        $routeC_count)
                    $routeC_target = if ($routeC_fromServer)
                    {
                        $routeC_clientEndpoints[$routeC_player]
                    }
                    else
                    {
                        $routeC_serverEndpoint
                    }
                    if ($null -ne $routeC_target)
                    {
                        $routeC_proxy.SendTo($routeC_copy, $routeC_target) |
                            Out-Null
                    }
                }
            }
            [Threading.Thread]::Yield() | Out-Null
        }

        Assert-Probe `
            ($routeC_transports[0].State.ToString() -eq 'Running' -and
             $routeC_transports[1].State.ToString() -eq 'Running') `
            'production-control-loss' `
            ("production Raw clients did not recover all first control losses; " +
             "states=$($routeC_transports[0].State),$($routeC_transports[1].State) " +
             "sent=$($routeC_transports[0].Diagnostics.DatagramsSent),$($routeC_transports[1].Diagnostics.DatagramsSent) " +
             "drops=$(@($routeC_dropTimes.Keys) -join ',') " +
             "retries=$(@($routeC_retryDelays.Keys) -join ',').")
        $routeC_expectedKeys = @()
        for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
        {
            $routeC_expectedKeys += "client-$routeC_player-RawHello"
            $routeC_expectedKeys += "server-$routeC_player-RawWelcome"
            $routeC_expectedKeys += "client-$routeC_player-RawReady"
            $routeC_expectedKeys += "server-$routeC_player-RawStart"
        }
        foreach ($routeC_key in $routeC_expectedKeys)
        {
            Assert-Probe $routeC_retryDelays.ContainsKey($routeC_key) `
                'production-control-loss' `
                "missing production retry for $routeC_key."
            Assert-Probe `
                ($routeC_retryDelays[$routeC_key] -ge 40 -and
                 $routeC_retryDelays[$routeC_key] -le 200) `
                'production-control-loss' `
                ("retry deadline drifted for ${routeC_key}: " +
                 "$($routeC_retryDelays[$routeC_key])ms.")
        }
        Assert-Probe ($routeC_earlyStartCopies -eq 0) `
            'production-control-loss' `
            'server emitted a Start retry before the 50ms deadline.'
        return [ordered]@{
            status = 'PASS'
            firstControlOpportunityDropped = $routeC_expectedKeys
            startCopiesBeforeRetryDeadlineSuppressed =
                $routeC_earlyStartCopies
            retryDelayMs = $routeC_retryDelays
        }
    }
    finally
    {
        foreach ($routeC_transport in $routeC_transports)
        {
            $routeC_transport.RequestStop()
            $routeC_transport.WaitForStop(260) | Out-Null
            $routeC_transport.Dispose()
        }
        foreach ($routeC_proxy in $routeC_proxies)
        {
            $routeC_proxy.Dispose()
        }
        $routeC_server.RequestStop()
        $routeC_server.WaitForStop(260) | Out-Null
        $routeC_server.Dispose()
    }
}

function Invoke-FirstControlLoss
{
    param($Types, [int]$Window, [int]$Frames)
    $routeC_controls = Invoke-ProductionControlLoss $Types
    $routeC_data = Invoke-DataRun `
        $Types `
        'first-control-loss-data' `
        $Window `
        $Frames `
        0
    return [ordered]@{
        scenario = 'first-control-loss'
        status = 'PASS'
        productionControls = $routeC_controls
        data = $routeC_data
    }
}

function Invoke-DataRun
{
    param(
        $Types,
        [string]$Name,
        [int]$Window,
        [int]$Frames,
        [int]$DropBurst = 0)
    $routeC_fixture = $null
    try
    {
        $routeC_fixture = New-LiveFixture $Types $Window ($Name -eq 'first-control-loss')
        $routeC_states = @(
            (New-ClientState 0 $routeC_fixture.Sessions[0] $Frames),
            (New-ClientState 1 $routeC_fixture.Sessions[1] $Frames))

        if ($Name -eq 'foreign-injection')
        {
            $routeC_foreign = [Net.Sockets.Socket]::new(
                [Net.Sockets.AddressFamily]::InterNetwork,
                [Net.Sockets.SocketType]::Dgram,
                [Net.Sockets.ProtocolType]::Udp)
            try
            {
                $routeC_foreign.Connect(
                    [Net.IPAddress]::Loopback,
                    $routeC_fixture.Server.Diagnostics.BoundPort)
                $routeC_foreign.Send((New-InputDatagram `
                    $Types $routeC_fixture.Sessions[0] 1 0 0 $Window)) | Out-Null
                [Threading.Thread]::Sleep(20)
                Assert-Probe (-not $routeC_fixture.Server.Diagnostics.IsTerminal) 'foreign-injection' 'foreign endpoint terminated legal match.'
            }
            finally
            {
                $routeC_foreign.Dispose()
            }
        }

        $routeC_started = [Diagnostics.Stopwatch]::StartNew()
        for ($routeC_frame = 0; $routeC_frame -lt $Frames; $routeC_frame++)
        {
            for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
            {
                $routeC_drop = $DropBurst -gt 0 -and
                    $routeC_player -eq 0 -and
                    $routeC_frame -ge 100 -and
                    $routeC_frame -lt 100 + $DropBurst
                if (-not $routeC_drop)
                {
                    $routeC_datagram = New-InputDatagram `
                        $Types `
                        $routeC_fixture.Sessions[$routeC_player] `
                        ([byte]$routeC_player) `
                        ([uint32]$routeC_frame) `
                        $routeC_frame `
                        ([byte]$Window)
                    $routeC_fixture.Clients[$routeC_player].Send($routeC_datagram) |
                        Out-Null
                }
            }
            if (($routeC_frame % 4) -eq 0)
            {
                Drain-Client $Types $routeC_fixture.Clients[0] $routeC_states[0] $Window $Name $Frames
                Drain-Client $Types $routeC_fixture.Clients[1] $routeC_states[1] $Window $Name $Frames
            }
        }

        for ($routeC_tail = 0; $routeC_tail -lt $Window - 1; $routeC_tail++)
        {
            for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
            {
                $routeC_sequence = [uint32]($Frames + $routeC_tail)
                $routeC_datagram = New-InputDatagram `
                    $Types `
                    $routeC_fixture.Sessions[$routeC_player] `
                    ([byte]$routeC_player) `
                    $routeC_sequence `
                    ($Frames - 1) `
                    ([byte]$Window)
                $routeC_fixture.Clients[$routeC_player].Send($routeC_datagram) |
                    Out-Null
            }
        }

        $routeC_wait = [Diagnostics.Stopwatch]::StartNew()
        while (($routeC_states[0].Accepted.Count -lt $Frames -or
                $routeC_states[1].Accepted.Count -lt $Frames) -and
            $routeC_wait.ElapsedMilliseconds -lt 15000)
        {
            Drain-Client $Types $routeC_fixture.Clients[0] $routeC_states[0] $Window $Name $Frames
            Drain-Client $Types $routeC_fixture.Clients[1] $routeC_states[1] $Window $Name $Frames
            [Threading.Thread]::Yield() | Out-Null
        }

        foreach ($routeC_state in $routeC_states)
        {
            if ($null -ne $routeC_state.Held)
            {
                Accept-Window $routeC_state $routeC_state.Held $Name $Frames
                $routeC_state.Held = $null
            }
        }

        Assert-Probe ($routeC_states[0].Accepted.Count -eq $Frames) 'exactly-once' 'client 0 did not accept every remote input.'
        Assert-Probe ($routeC_states[1].Accepted.Count -eq $Frames) 'exactly-once' 'client 1 did not accept every remote input.'
        for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
        {
            for ($routeC_frame = 0; $routeC_frame -lt $Frames; $routeC_frame++)
            {
                Assert-Probe ($routeC_states[$routeC_player].Publications[$routeC_frame] -eq 1) 'exactly-once' 'a remote Actual was published other than once.'
            }
            Assert-Probe $routeC_states[$routeC_player].DefaultSizeObserved 'datagram-size' 'steady-state RawInput size was not observed.'
        }
        Assert-Probe `
            ($routeC_fixture.Server.Diagnostics.MaximumPlayerWorkItemsPerRound -le 64) `
            'player-budget' `
            'production worker exceeded 64 accepted/generated work items in one player round.'

        $routeC_elapsed = [Math]::Max(1, $routeC_started.ElapsedMilliseconds)
        return [ordered]@{
            scenario = $Name
            status = 'PASS'
            framesPerClient = $Frames
            windowSize = $Window
            canonicalStartFrame = 0
            remoteFrameOffset = 0
            datagramBytes = if ($Window -eq 16) { 168 } else { 88 }
            direction0 = [ordered]@{
                datagrams = $routeC_states[1].DatagramCount
                bytes = $routeC_states[1].ByteCount
                pps = [Math]::Round($routeC_states[1].DatagramCount * 1000.0 / $routeC_elapsed, 3)
                sequenceHighWater = $routeC_states[1].SequenceHighWater
                acceptedActuals = $routeC_states[1].Accepted.Count
                duplicates = $routeC_states[1].DuplicateWindows
                reordered = $routeC_states[1].ReorderedWindows
                drops = $routeC_states[1].DroppedWindows
                delayed = $routeC_states[1].DelayedWindows
            }
            direction1 = [ordered]@{
                datagrams = $routeC_states[0].DatagramCount
                bytes = $routeC_states[0].ByteCount
                pps = [Math]::Round($routeC_states[0].DatagramCount * 1000.0 / $routeC_elapsed, 3)
                sequenceHighWater = $routeC_states[0].SequenceHighWater
                acceptedActuals = $routeC_states[0].Accepted.Count
                duplicates = $routeC_states[0].DuplicateWindows
                reordered = $routeC_states[0].ReorderedWindows
                drops = $routeC_states[0].DroppedWindows
                delayed = $routeC_states[0].DelayedWindows
            }
            queueHighWater = @(
                $routeC_fixture.Server.Diagnostics.Player0QueueHighWater,
                $routeC_fixture.Server.Diagnostics.Player1QueueHighWater)
            receiveBudgetExhaustions =
                $routeC_fixture.Server.Diagnostics.ReceiveBudgetExhaustionCount
            maximumPlayerWorkItemsPerRound =
                $routeC_fixture.Server.Diagnostics.MaximumPlayerWorkItemsPerRound
            lateRecoveryRelayCopies =
                $routeC_fixture.Server.Diagnostics.LateRecoveryRelayCopies
            tailCopies = 2 * ($Window - 1)
            droppedFirstControls = $routeC_fixture.DroppedFirstControls
        }
    }
    finally
    {
        Close-LiveFixture $routeC_fixture
    }
}

function Invoke-TerminalScenario
{
    param($Types, [string]$Name, [int]$Window)
    $routeC_fixture = $null
    try
    {
        $routeC_fixture = New-LiveFixture $Types $Window $false
        if ($Name -eq 'bound-conflict')
        {
            $routeC_fixture.Clients[0].Send((New-InputDatagram `
                $Types $routeC_fixture.Sessions[0] 0 0 0 $Window)) | Out-Null
            [Threading.Thread]::Sleep(20)
            $routeC_fixture.Clients[0].Send((New-InputDatagram `
                $Types $routeC_fixture.Sessions[0] 0 1 0 $Window 0)) | Out-Null
            $routeC_expectedReason = 'ConflictingInput'
            $routeC_expectedPlayer = 0
            $routeC_expectedFrame = 0
        }
        elseif ($Name -eq 'endpoint-change-silence')
        {
            $routeC_replacement = [Net.Sockets.Socket]::new(
                [Net.Sockets.AddressFamily]::InterNetwork,
                [Net.Sockets.SocketType]::Dgram,
                [Net.Sockets.ProtocolType]::Udp)
            try
            {
                $routeC_replacement.Connect(
                    [Net.IPAddress]::Loopback,
                    $routeC_fixture.Server.Diagnostics.BoundPort)
                $routeC_timeoutWatch = [Diagnostics.Stopwatch]::StartNew()
                $routeC_keepAliveSequence = 0
                while (-not $routeC_fixture.Server.Diagnostics.IsTerminal -and
                    $routeC_timeoutWatch.ElapsedMilliseconds -lt 3500)
                {
                    $routeC_replacement.Send((New-InputDatagram `
                        $Types `
                        $routeC_fixture.Sessions[0] `
                        0 `
                        ([uint32]$routeC_keepAliveSequence) `
                        $routeC_keepAliveSequence `
                        $Window)) | Out-Null
                    $routeC_fixture.Clients[1].Send((New-InputDatagram `
                        $Types `
                        $routeC_fixture.Sessions[1] `
                        1 `
                        ([uint32]$routeC_keepAliveSequence) `
                        $routeC_keepAliveSequence `
                        $Window)) | Out-Null
                    $routeC_keepAliveSequence++
                    [Threading.Thread]::Sleep(25)
                }
                Assert-Probe $routeC_fixture.Server.Diagnostics.IsTerminal `
                    'endpoint-change' `
                    'production server did not time out the silent bound endpoint.'
            }
            finally
            {
                $routeC_replacement.Dispose()
            }
            $routeC_expectedReason = 'ConnectionTimedOut'
            $routeC_expectedPlayer = 0
            $routeC_expectedFrame = -1
        }
        elseif ($Name -eq 'aligned-6-uplink')
        {
            for ($routeC_sequence = 6; $routeC_sequence -le 22; $routeC_sequence++)
            {
                $routeC_fixture.Clients[0].Send((New-InputDatagram `
                    $Types $routeC_fixture.Sessions[0] 0 ([uint32]$routeC_sequence) $routeC_sequence $Window)) | Out-Null
                [Threading.Thread]::Sleep(1)
            }
            $routeC_expectedReason = 'UnrecoverableInputGap'
            $routeC_expectedPlayer = 0
            $routeC_expectedFrame = 0
        }
        else
        {
            $routeC_downlinkAccepted = @{}
            $routeC_receiver = [Activator]::CreateInstance(
                $Types.InputReceiver,
                @($Window, $true))
            $routeC_acceptMethod = $Types.InputReceiver.GetMethod('Accept')
            $routeC_receiverFault = $null
            for ($routeC_sequence = 0; $routeC_sequence -le 22; $routeC_sequence++)
            {
                $routeC_fixture.Clients[0].Send((New-InputDatagram `
                    $Types $routeC_fixture.Sessions[0] 0 ([uint32]$routeC_sequence) $routeC_sequence $Window)) | Out-Null
                $routeC_downlink = Receive-Message `
                    $Types $routeC_fixture.Clients[1] 2000 'RawInput'
                Assert-Probe ($null -ne $routeC_downlink) 'aligned-6-downlink' 'expected rebuilt downlink was missing.'
                $routeC_downWindow = Decode-Input $Types $routeC_downlink.Message $Window
                Assert-Probe ($routeC_downWindow.PacketSequence -eq $routeC_sequence) 'aligned-6-downlink' 'downlink sequence was not fresh and contiguous.'
                if ($routeC_downWindow.PacketSequence -gt 5)
                {
                    $routeC_acceptArguments = @(
                        $routeC_downWindow,
                        $null,
                        [Activator]::CreateInstance($Types.Fault))
                    $routeC_disposition = $routeC_acceptMethod.Invoke(
                        $routeC_receiver,
                        $routeC_acceptArguments)
                    foreach ($routeC_entry in $routeC_acceptArguments[1])
                    {
                        $routeC_downlinkAccepted[[int]$routeC_entry.FrameID] =
                            [uint32]$routeC_entry.Raw
                    }
                    if ($routeC_disposition.ToString() -eq 'UnrecoverableGap')
                    {
                        $routeC_receiverFault = $routeC_acceptArguments[2]
                    }
                }
            }
            Assert-Probe (-not $routeC_fixture.Server.Diagnostics.IsTerminal) 'aligned-6-downlink' 'downlink-only loss incorrectly terminated server state.'
            Assert-Probe (-not $routeC_downlinkAccepted.ContainsKey(0)) 'aligned-6-downlink' 'scripted six-datagram loss unexpectedly recovered frame zero.'
            Assert-Probe ($null -ne $routeC_receiverFault) 'aligned-6-downlink' 'production receiver did not produce an unrecoverable-gap fault.'
            Send-Fault `
                $Types `
                $routeC_fixture.Clients[1] `
                $routeC_fixture.Sessions[1] `
                $routeC_receiverFault.Reason.ToString() `
                $routeC_receiverFault.PlayerIndex `
                $routeC_receiverFault.FrameID `
                $routeC_receiverFault.ObservedLatestFrameID
            $routeC_expectedReason = 'UnrecoverableInputGap'
            $routeC_expectedPlayer = 0
            $routeC_expectedFrame = 0
        }

        $routeC_faults = @()
        for ($routeC_player = 0; $routeC_player -lt 2; $routeC_player++)
        {
            $routeC_received = Receive-Message $Types $routeC_fixture.Clients[$routeC_player] 2000 'RawFault'
            Assert-Probe ($null -ne $routeC_received) 'terminal-broadcast' 'both clients did not receive RawFault.'
            $routeC_faults += Decode-Fault $Types $routeC_received.Message
        }
        foreach ($routeC_fault in $routeC_faults)
        {
            Assert-Probe ($routeC_fault.Reason.ToString() -eq $routeC_expectedReason) 'terminal-reason' 'terminal reason was downgraded.'
            Assert-Probe ([int]$routeC_fault.PlayerIndex -eq $routeC_expectedPlayer) 'terminal-player' 'terminal player attribution changed.'
            Assert-Probe ($routeC_fault.FrameID -eq $routeC_expectedFrame) 'terminal-frame' 'terminal frame changed.'
        }
        return [ordered]@{
            scenario = $Name
            status = 'PASS'
            reason = $routeC_expectedReason
            player = [int]$routeC_faults[0].PlayerIndex
            frame = [int]$routeC_faults[0].FrameID
        }
    }
    finally
    {
        Close-LiveFixture $routeC_fixture
    }
}

function Invoke-LateRecoveryLive
{
    param($Types)
    $routeC_fixture = $null
    try
    {
        $routeC_fixture = New-LiveFixture $Types 6 $false
        $routeC_fixture.Clients[0].Send((New-InputDatagram `
            $Types $routeC_fixture.Sessions[0] 0 6 6 6)) | Out-Null
        $routeC_front = Receive-Message $Types $routeC_fixture.Clients[1] 2000 'RawInput'
        Assert-Probe ($null -ne $routeC_front) 'late-recovery' '[F+1..F+6] frontier window was not relayed.'
        $routeC_frontWindow = Decode-Input $Types $routeC_front.Message 6
        Assert-Probe ($routeC_frontWindow.Entries[0].FrameID -eq 1) 'late-recovery' 'frontier window did not begin at F+1.'

        $routeC_fixture.Clients[0].Send((New-InputDatagram `
            $Types $routeC_fixture.Sessions[0] 0 5 5 6)) | Out-Null
        $routeC_sequences = @()
        for ($routeC_copy = 0; $routeC_copy -lt 6; $routeC_copy++)
        {
            $routeC_recovered = Receive-Message $Types $routeC_fixture.Clients[1] 2000 'RawInput'
            Assert-Probe ($null -ne $routeC_recovered) 'late-recovery' 'late F did not receive exactly N generated opportunities.'
            $routeC_recoveredWindow = Decode-Input $Types $routeC_recovered.Message 6
            Assert-Probe ($routeC_recoveredWindow.Entries[0].FrameID -eq 0) 'late-recovery' 'rebuilt late window omitted F.'
            $routeC_sequences += [uint32]$routeC_recoveredWindow.PacketSequence
        }
        Assert-Probe (($routeC_sequences -join ',') -eq '1,2,3,4,5,6') 'late-recovery' 'rebuilt copies did not use fresh downlink sequences.'
        Assert-Probe ($routeC_fixture.Server.Diagnostics.LateRecoveryRelayCopies -eq 5) 'late-recovery' 'server did not record exactly N-1 extra copies.'
        return [ordered]@{
            status = 'PASS'
            firstWindow = '[F+1..F+6]'
            secondWindow = '[F..F+5]'
            olderUpstreamSequenceArrivedSecond = $true
            downlinkOpportunitiesForF = 6
            freshDownlinkSequences = $routeC_sequences
        }
    }
    finally
    {
        Close-LiveFixture $routeC_fixture
    }
}

function Invoke-BoundedStop
{
    param($Types)
    $routeC_measurements = @()

    $routeC_unstarted = [Activator]::CreateInstance($Types.Server, @(0, 6))
    try
    {
        $routeC_measurements += Measure-BoundedStop $routeC_unstarted 'unstarted'
    }
    finally { $routeC_unstarted.Dispose() }

    $routeC_handshaking = [Activator]::CreateInstance($Types.Server, @(0, 6))
    $routeC_handshakeClient = $null
    try
    {
        $routeC_handshaking.Run()
        while ($routeC_handshaking.Diagnostics.BoundPort -le 0)
        {
            [Threading.Thread]::Yield() | Out-Null
        }
        $routeC_handshakeClient = [Net.Sockets.Socket]::new(
            [Net.Sockets.AddressFamily]::InterNetwork,
            [Net.Sockets.SocketType]::Dgram,
            [Net.Sockets.ProtocolType]::Udp)
        $routeC_handshakeClient.Connect(
            [Net.IPAddress]::Loopback,
            $routeC_handshaking.Diagnostics.BoundPort)
        Send-Hello $Types $routeC_handshakeClient (New-Nonce 208)
        $null = Receive-Message $Types $routeC_handshakeClient 2000 'RawWelcome'
        $routeC_measurements += Measure-BoundedStop $routeC_handshaking 'handshaking'
    }
    finally
    {
        if ($routeC_handshakeClient) { $routeC_handshakeClient.Dispose() }
        $routeC_handshaking.Dispose()
    }

    $routeC_running = $null
    try
    {
        $routeC_running = New-LiveFixture $Types 6 $false
        $routeC_measurements += Measure-BoundedStop $routeC_running.Server 'running'
    }
    finally { Close-LiveFixture $routeC_running }

    $routeC_terminal = $null
    try
    {
        $routeC_terminal = New-LiveFixture $Types 6 $false
        $routeC_terminal.Clients[0].Send((New-InputDatagram `
            $Types $routeC_terminal.Sessions[0] 0 0 0 6)) | Out-Null
        $routeC_terminal.Clients[0].Send((New-InputDatagram `
            $Types $routeC_terminal.Sessions[0] 0 1 0 6 0)) | Out-Null
        $routeC_terminalWatch = [Diagnostics.Stopwatch]::StartNew()
        while (-not $routeC_terminal.Server.Diagnostics.IsTerminal -and
            $routeC_terminalWatch.ElapsedMilliseconds -lt 1000)
        {
            [Threading.Thread]::Yield() | Out-Null
        }
        Assert-Probe $routeC_terminal.Server.Diagnostics.IsTerminal `
            'bounded-stop' `
            'terminal fixture did not enter the repeat phase.'
        $routeC_measurements += Measure-BoundedStop $routeC_terminal.Server 'terminal'
    }
    finally { Close-LiveFixture $routeC_terminal }

    return [ordered]@{
        scenario = 'bounded-stop'
        status = 'PASS'
        states = $routeC_measurements
        boundMs = 260
    }
}

function Measure-BoundedStop
{
    param($Server, [string]$StateName)
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    $Server.RequestStop()
    Assert-Probe ($Server.WaitForStop(260)) 'bounded-stop' `
        "$StateName server did not stop within 260ms."
    $routeC_elapsed = $routeC_watch.ElapsedMilliseconds
    $Server.RequestStop()
    Assert-Probe ($Server.WaitForStop(260)) 'bounded-stop' `
        "$StateName repeated stop failed."
    return [ordered]@{
        state = $StateName
        durationMs = $routeC_elapsed
    }
}

function Invoke-OneScenario
{
    param($Types, [string]$Name, [int]$RequestedWindow, [int]$Frames)
    switch ($Name)
    {
        'clean' { return Invoke-DataRun $Types $Name $RequestedWindow $Frames 0 }
        'first-control-loss' { return Invoke-FirstControlLoss $Types $RequestedWindow $Frames }
        'mixed-faults' { return Invoke-DataRun $Types $Name $RequestedWindow $Frames 0 }
        'foreign-injection' { return Invoke-DataRun $Types $Name $RequestedWindow $Frames 0 }
        'window-16' { return Invoke-DataRun $Types $Name 16 $Frames 0 }
        'drop-1-through-5'
        {
            $routeC_runs = @()
            for ($routeC_count = 1; $routeC_count -le 5; $routeC_count++)
            {
                $routeC_runs += Invoke-DataRun $Types $Name $RequestedWindow $Frames $routeC_count
            }
            return [ordered]@{
                scenario = $Name
                status = 'PASS'
                testedBurstLengths = @(1, 2, 3, 4, 5)
                runs = $routeC_runs
                lateRecovery = Invoke-LateRecoveryLive $Types
            }
        }
        'aligned-6-uplink' { return Invoke-TerminalScenario $Types $Name $RequestedWindow }
        'aligned-6-downlink' { return Invoke-TerminalScenario $Types $Name $RequestedWindow }
        'bound-conflict' { return Invoke-TerminalScenario $Types $Name $RequestedWindow }
        'endpoint-change-silence' { return Invoke-TerminalScenario $Types $Name $RequestedWindow }
        'bounded-stop' { return Invoke-BoundedStop $Types }
    }
}

Assert-Probe (Test-Path -LiteralPath $ServerPath -PathType Leaf) 'assembly' 'servercheck is missing.'
$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_types = [pscustomobject]@{
    Server = $routeC_assembly.GetType('FrameSyncServer.RawUdpRelayServer', $true)
    EnvelopeCodec = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolCodec', $true)
    RawCodec = $routeC_assembly.GetType('FrameSyncDemo.RawUdpProtocolCodec', $true)
    MessageType = $routeC_assembly.GetType('FrameSyncDemo.RouteCMessageType', $true)
    SessionId = $routeC_assembly.GetType('FrameSyncDemo.RouteCSessionId', $true)
    DropReason = $routeC_assembly.GetType('FrameSyncDemo.RouteCProtocolDropReason', $true)
    InputEntry = $routeC_assembly.GetType('FrameSyncDemo.RawUdpInputEntry', $true)
    Fault = $routeC_assembly.GetType('FrameSyncDemo.RawUdpFault', $true)
    FaultReason = $routeC_assembly.GetType('FrameSyncDemo.RawUdpFaultReason', $true)
    InputReceiver = $routeC_assembly.GetType('FrameSyncDemo.RawUdpInputReceiver', $true)
    RawTransport = $routeC_assembly.GetType('FrameSyncDemo.RawUdpInputTransport', $true)
    Clock = $routeC_assembly.GetType('FrameSyncDemo.StopwatchMonotonicClock', $true)
    NonceFactory = $routeC_assembly.GetType('FrameSyncServer.RawUdpLiveProbeNonceFactory', $true)
}

$routeC_names = if ($Scenario -eq 'all')
{
    @(
        'clean',
        'first-control-loss',
        'drop-1-through-5',
        'aligned-6-uplink',
        'aligned-6-downlink',
        'mixed-faults',
        'foreign-injection',
        'bound-conflict',
        'endpoint-change-silence',
        'window-16',
        'bounded-stop')
}
else
{
    @($Scenario)
}

$routeC_evidence = [ordered]@{
    schema = 'route-c-raw-udp-live-probe-v1'
    seed = $Seed
    serverArtifact = [IO.Path]::GetFileName($ServerPath)
    scenarios = @()
}

try
{
    foreach ($routeC_name in $routeC_names)
    {
        $routeC_window = if ($routeC_name -eq 'window-16') { 16 } else { $WindowSize }
        $routeC_evidence.scenarios += Invoke-OneScenario `
            $routeC_types `
            $routeC_name `
            $routeC_window `
            $FramesPerClient
    }
}
catch
{
    $routeC_evidence.scenarios += [ordered]@{
        scenario = $routeC_name
        status = 'FAIL'
        error = $_.Exception.Message
    }
    $routeC_directory = Split-Path -Parent $EvidencePath
    if (-not [string]::IsNullOrEmpty($routeC_directory))
    {
        [IO.Directory]::CreateDirectory($routeC_directory) | Out-Null
    }
    $routeC_evidence | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $EvidencePath -Encoding UTF8
    throw
}

$routeC_evidence.scenarios = @($routeC_evidence.scenarios)
$routeC_outputDirectory = Split-Path -Parent $EvidencePath
if (-not [string]::IsNullOrEmpty($routeC_outputDirectory))
{
    [IO.Directory]::CreateDirectory($routeC_outputDirectory) | Out-Null
}
$routeC_evidence | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $EvidencePath -Encoding UTF8
Write-Output "PASS: Raw UDP live probe scenario '$Scenario' completed with isolated Raw-only evidence."
