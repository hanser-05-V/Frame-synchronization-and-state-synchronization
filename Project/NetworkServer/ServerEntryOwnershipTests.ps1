param(
    [string]$ServerPath = '',
    [string]$SourceRoot = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceRoot))
{
    $SourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($ServerPath))
{
    $ServerPath = Join-Path $SourceRoot 'NetworkServer.exe'
}

function Assert-RouteCEqual
{
    param(
        $Expected,
        $Actual,
        [string]$Message
    )

    if ($Expected -ne $Actual)
    {
        throw "$Message Expected=$Expected Actual=$Actual"
    }
}

function Assert-RouteCTrue
{
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition)
    {
        throw $Message
    }
}

if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf))
{
    throw "Server assembly does not exist: $ServerPath"
}

$routeC_programPath = Join-Path $SourceRoot 'NetworkServer.cs'
$routeC_tcpPath = Join-Path $SourceRoot 'TcpRelayServer.cs'
$routeC_rawPath = Join-Path $SourceRoot 'RawUdpRelayServer.cs'
$routeC_kcpPath = Join-Path $SourceRoot 'KcpUdpRelayServer.cs'
$routeC_barrierPath = Join-Path $SourceRoot 'NetworkServerBarrierTests.ps1'
$routeC_timingPath = Join-Path $SourceRoot 'NetworkServerTimingProbe.ps1'
$routeC_programSource = Get-Content -Raw -LiteralPath $routeC_programPath
Assert-RouteCTrue `
    (Test-Path -LiteralPath $routeC_tcpPath -PathType Leaf) `
    'TcpRelayServer.cs is missing.'
Assert-RouteCTrue `
    (Test-Path -LiteralPath $routeC_rawPath -PathType Leaf) `
    'RawUdpRelayServer.cs is missing.'
Assert-RouteCTrue `
    (Test-Path -LiteralPath $routeC_kcpPath -PathType Leaf) `
    'KcpUdpRelayServer.cs is missing.'
$routeC_tcpSource = Get-Content -Raw -LiteralPath $routeC_tcpPath
$routeC_barrierSource = Get-Content -Raw -LiteralPath $routeC_barrierPath
$routeC_timingSource = Get-Content -Raw -LiteralPath $routeC_timingPath

foreach ($routeC_forbiddenToken in @(
    'TcpListener',
    'TcpClient',
    'NetworkStream',
    'List<',
    'DeterministicPacketScheduler',
    'NetworkTraceWriter',
    'AutoResetEvent',
    'ReceiveLoop',
    'SchedulerLoop',
    'Broadcast'))
{
    Assert-RouteCTrue `
        (-not $routeC_programSource.Contains($routeC_forbiddenToken)) `
        "Program still owns TCP relay state or behavior: $routeC_forbiddenToken"
}

Assert-RouteCEqual `
    1 `
    ([regex]::Matches($routeC_programSource, 'new\s+TcpRelayServer\s*\(').Count) `
    'Program must construct exactly one supported TCP server.'
Assert-RouteCEqual `
    3 `
    ([regex]::Matches($routeC_programSource, '\.Run\s*\(').Count) `
    'Program must expose exactly one Run call in each TCP/Raw/KCP branch.'
Assert-RouteCTrue `
    ($routeC_programSource.Contains('using (var server = new TcpRelayServer(')) `
    'Program must deterministically dispose the selected TCP server.'
Assert-RouteCEqual `
    1 `
    ([regex]::Matches($routeC_programSource, 'new\s+RawUdpRelayServer\s*\(').Count) `
    'Program must construct exactly one Raw UDP server in its selected branch.'
Assert-RouteCTrue `
    ($routeC_programSource.Contains('using (var server = new RawUdpRelayServer(')) `
    'Program must deterministically dispose the selected Raw UDP server.'
Assert-RouteCEqual `
    1 `
    ([regex]::Matches($routeC_programSource, 'new\s+KcpUdpRelayServer\s*\(').Count) `
    'Program must construct exactly one KCP UDP server in its selected branch.'
Assert-RouteCTrue `
    ($routeC_programSource.Contains('using (var server = new KcpUdpRelayServer(')) `
    'Program must deterministically dispose the selected KCP UDP server.'
Assert-RouteCEqual `
    2 `
    ([regex]::Matches($routeC_programSource, '\.WaitForStop\s*\(\s*-1\s*\)').Count) `
    'Only the asynchronous Raw and KCP branches should wait indefinitely for workers.'
Assert-RouteCEqual `
    1 `
    ([regex]::Matches($routeC_programSource, '\.WaitForStop\s*\(\s*10\s*\)').Count) `
    'KCP stop-file polling must use exactly one bounded 10ms worker check.'
Assert-RouteCEqual `
    1 `
    ([regex]::Matches($routeC_programSource, '\.WaitForStop\s*\(\s*2000\s*\)').Count) `
    'KCP stop-file shutdown must use exactly one bounded 2000ms final wait.'

$routeC_serverSources = @(
    Get-ChildItem -LiteralPath $SourceRoot -Filter '*.cs' -File |
        ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join "`n"
Assert-RouteCEqual `
    1 `
    ([regex]::Matches($routeC_serverSources, 'new\s+TcpListener\s*\(').Count) `
    'There must be exactly one TCP listener implementation.'
Assert-RouteCEqual `
    1 `
    ([regex]::Matches($routeC_serverSources, 'new\s+DeterministicPacketScheduler\s*\(').Count) `
    'There must be exactly one deterministic scheduler implementation.'
Assert-RouteCEqual `
    1 `
    ([regex]::Matches($routeC_serverSources, 'new\s+NetworkTraceWriter\s*\(').Count) `
    'There must be exactly one trace-writer owner.'

$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_programType = $routeC_assembly.GetType('FrameSyncServer.Program', $true)
$routeC_tcpType = $routeC_assembly.GetType(
    'FrameSyncServer.TcpRelayServer',
    $true)
$routeC_rawType = $routeC_assembly.GetType(
    'FrameSyncServer.RawUdpRelayServer',
    $true)
$routeC_kcpType = $routeC_assembly.GetType(
    'FrameSyncServer.KcpUdpRelayServer',
    $true)
$routeC_routerType = $routeC_assembly.GetType(
    'FrameSyncServer.ServerSessionRouter',
    $true)
$routeC_instanceFields = @($routeC_tcpType.GetFields(
    [Reflection.BindingFlags]'Instance, Public, NonPublic'))
$routeC_staticProgramFields = @($routeC_programType.GetFields(
    [Reflection.BindingFlags]'Static, Public, NonPublic') |
    Where-Object { -not $_.IsLiteral })
Assert-RouteCEqual `
    0 `
    $routeC_staticProgramFields.Count `
    'Program must not retain mutable static server state.'

$routeC_schedulerFields = @(
    $routeC_instanceFields |
        Where-Object { $_.FieldType.FullName -eq 'FrameSyncServer.DeterministicPacketScheduler' })
$routeC_traceFields = @(
    $routeC_instanceFields |
        Where-Object { $_.FieldType.FullName -eq 'FrameSyncServer.NetworkTraceWriter' })
$routeC_listenerFields = @(
    $routeC_instanceFields |
        Where-Object { $_.FieldType -eq [System.Net.Sockets.TcpListener] })
$routeC_clientListFields = @(
    $routeC_instanceFields |
        Where-Object {
            $_.FieldType.IsGenericType -and
            $_.FieldType.GetGenericTypeDefinition() -eq [System.Collections.Generic.List``1] -and
            $_.FieldType.GetGenericArguments()[0] -eq [System.Net.Sockets.TcpClient]
        })
Assert-RouteCEqual 1 $routeC_schedulerFields.Count 'TcpRelayServer must own exactly one scheduler.'
Assert-RouteCEqual 1 $routeC_traceFields.Count 'TcpRelayServer must own exactly one trace writer.'
Assert-RouteCEqual 1 $routeC_listenerFields.Count 'TcpRelayServer must own exactly one listener.'
Assert-RouteCEqual 1 $routeC_clientListFields.Count 'TcpRelayServer must own exactly one client list.'

$routeC_rawFields = @($routeC_rawType.GetFields(
    [Reflection.BindingFlags]'Instance, Public, NonPublic'))
Assert-RouteCEqual `
    1 `
    @($routeC_rawFields | Where-Object FieldType -eq ([System.Net.Sockets.Socket])).Count `
    'RawUdpRelayServer must own exactly one Socket.'
Assert-RouteCEqual `
    1 `
    @($routeC_rawFields | Where-Object FieldType -eq ([System.Threading.Thread])).Count `
    'RawUdpRelayServer must own exactly one worker Thread.'

$routeC_kcpFields = @($routeC_kcpType.GetFields(
    [Reflection.BindingFlags]'Instance, Public, NonPublic'))
Assert-RouteCEqual `
    1 `
    @($routeC_kcpFields | Where-Object FieldType -eq ([System.Net.Sockets.Socket])).Count `
    'KcpUdpRelayServer must own exactly one Socket.'
Assert-RouteCEqual `
    1 `
    @($routeC_kcpFields | Where-Object FieldType -eq ([System.Threading.Thread])).Count `
    'KcpUdpRelayServer must own exactly one worker Thread.'
Assert-RouteCEqual `
    1 `
    @($routeC_kcpFields | Where-Object FieldType -eq $routeC_routerType).Count `
    'KcpUdpRelayServer must own exactly one mutable session router.'

$routeC_configureMethod = $routeC_tcpType.GetMethod(
    'ConfigureLowLatency',
    [Reflection.BindingFlags]'Static, NonPublic')
$routeC_readMethod = $routeC_tcpType.GetMethod(
    'TryReadExactly',
    [Reflection.BindingFlags]'Static, NonPublic')
Assert-RouteCTrue ($null -ne $routeC_configureMethod) 'TCP NoDelay seam was not preserved.'
Assert-RouteCTrue ($null -ne $routeC_readMethod) 'Exact 8-byte stream read seam was not preserved.'
Assert-RouteCTrue `
    ([IDisposable].IsAssignableFrom($routeC_tcpType)) `
    'TcpRelayServer must provide deterministic disposal.'
Assert-RouteCTrue `
    ($routeC_barrierSource.Contains("Arguments = '--transport tcp'")) `
    'TCP barrier must select TCP explicitly.'
Assert-RouteCTrue `
    ($routeC_timingSource.Contains("'--transport', 'tcp'")) `
    'Explicit timing profiles must select TCP explicitly.'

$routeC_occupied = [Net.Sockets.Socket]::new(
    [Net.Sockets.AddressFamily]::InterNetwork,
    [Net.Sockets.SocketType]::Dgram,
    [Net.Sockets.ProtocolType]::Udp)
try
{
    $routeC_occupied.ExclusiveAddressUse = $true
    $routeC_occupied.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, 8888))
    & $ServerPath --transport raw-udp --raw-window 6 2>$null
    Assert-RouteCEqual `
        3 `
        $LASTEXITCODE `
        'Raw worker failure must produce a nonzero process exit code.'
}
finally
{
    $routeC_occupied.Dispose()
}

Write-Output 'PASS: Program is thin and the TCP/Raw/KCP servers exclusively own their selected transports.'
