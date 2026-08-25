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

function Wait-KcpCondition
{
    param([scriptblock]$Condition, [int]$TimeoutMs, [string]$Message)
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    while ($routeC_watch.ElapsedMilliseconds -lt $TimeoutMs)
    {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 5
    }
    throw $Message
}

if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf))
{
    throw "Server assembly does not exist: $ServerPath"
}

$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_serverType = $routeC_assembly.GetType(
    'FrameSyncServer.KcpUdpRelayServer',
    $true)
$routeC_routerType = $routeC_assembly.GetType(
    'FrameSyncServer.ServerSessionRouter',
    $true)
$routeC_fields = @($routeC_serverType.GetFields(
    [Reflection.BindingFlags]'Instance, Public, NonPublic'))
Assert-KcpEqual 1 @($routeC_fields | Where-Object FieldType -eq ([Net.Sockets.Socket])).Count 'ASSERT ownership-socket: server must own exactly one UDP Socket field.'
Assert-KcpEqual 1 @($routeC_fields | Where-Object FieldType -eq ([Threading.Thread])).Count 'ASSERT ownership-thread: server must own exactly one worker Thread field.'
Assert-KcpEqual 1 @($routeC_fields | Where-Object FieldType -eq $routeC_routerType).Count 'ASSERT ownership-router: server must own exactly one mutable protocol router.'
Assert-KcpTrue ([IDisposable].IsAssignableFrom($routeC_serverType)) 'ASSERT lifecycle-dispose: server must be IDisposable.'

$routeC_serverRoot = Split-Path -Parent (Split-Path -Parent $ServerPath)
$routeC_workerSource = Get-Content -LiteralPath (
    Join-Path $routeC_serverRoot 'KcpUdpRelayServer.cs') -Raw
$routeC_routerSource = Get-Content -LiteralPath (
    Join-Path $routeC_serverRoot 'ServerSessionRouter.cs') -Raw
Assert-KcpTrue $routeC_workerSource.Contains('new KcpServerRoundBudget') 'ASSERT budget-wiring: worker does not create one shared round budget.'
Assert-KcpTrue $routeC_workerSource.Contains('budget.TryTakeDatagram()') 'ASSERT receive-budget-wiring: UDP receive path bypasses the 64-datagram budget.'
Assert-KcpTrue $routeC_workerSource.Contains('_router.TickWithBudget(') 'ASSERT tick-budget-wiring: worker budget is not shared with router Tick.'
Assert-KcpTrue $routeC_workerSource.Contains('_router.NextActionAt(') 'ASSERT select-deadline: worker does not wait for the earliest Session deadline.'
Assert-KcpTrue $routeC_routerSource.Contains('budget.TryTakeMessage(sender.PlayerIndex)') 'ASSERT message-budget-wiring: KCP drain bypasses the per-session 64-message budget.'
Assert-KcpTrue $routeC_routerSource.Contains('budget.HasLiveSliceBudget(Stopwatch.GetTimestamp())') 'ASSERT slice-budget-wiring: decoded-message drain bypasses the 2ms live-work budget.'

$routeC_unstarted = [Activator]::CreateInstance($routeC_serverType, @(10, 0))
try
{
    $routeC_unstarted.RequestStop()
    Assert-KcpTrue $routeC_unstarted.WaitForStop(300) 'ASSERT unstarted-stop: stop did not complete.'
    $routeC_unstarted.RequestStop()
    Assert-KcpTrue $routeC_unstarted.WaitForStop(300) 'ASSERT unstarted-repeat: repeated stop was not idempotent.'
}
finally
{
    $routeC_unstarted.Dispose()
}

$routeC_running = [Activator]::CreateInstance($routeC_serverType, @(10, 0))
try
{
    $routeC_running.Run()
    Wait-KcpCondition { $routeC_running.BoundPort -gt 0 } 1000 'ASSERT bind: worker did not bind an ephemeral UDP port.'
    Assert-KcpTrue (-not $routeC_running.Diagnostics.WorkerFault) 'ASSERT running-fault: clean worker faulted.'
    $routeC_watch = [Diagnostics.Stopwatch]::StartNew()
    $routeC_running.RequestStop()
    Assert-KcpTrue $routeC_running.WaitForStop(300) 'ASSERT running-stop: worker did not stop within bound.'
    Assert-KcpTrue ($routeC_watch.ElapsedMilliseconds -lt 300) 'ASSERT running-stop-duration: stop exceeded bound.'
    Assert-KcpTrue (-not $routeC_running.Diagnostics.WorkerFault) 'ASSERT running-stop-fault: clean stop reported fault.'
    $routeC_running.RequestStop()
    Assert-KcpTrue $routeC_running.WaitForStop(300) 'ASSERT running-repeat: repeated stop was not idempotent.'
}
finally
{
    $routeC_running.Dispose()
}

$routeC_blocker = [Net.Sockets.Socket]::new(
    [Net.Sockets.AddressFamily]::InterNetwork,
    [Net.Sockets.SocketType]::Dgram,
    [Net.Sockets.ProtocolType]::Udp)
$routeC_blocker.ExclusiveAddressUse = $true
$routeC_blocker.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
$routeC_occupiedPort = ([Net.IPEndPoint]$routeC_blocker.LocalEndPoint).Port
$routeC_faulted = [Activator]::CreateInstance(
    $routeC_serverType,
    @(10, $routeC_occupiedPort))
try
{
    $routeC_faulted.Run()
    Assert-KcpTrue $routeC_faulted.WaitForStop(1000) 'ASSERT bind-fault-stop: faulted worker did not exit.'
    Assert-KcpTrue $routeC_faulted.Diagnostics.WorkerFault 'ASSERT bind-fault: occupied port was not reported.'
}
finally
{
    $routeC_faulted.Dispose()
    $routeC_blocker.Dispose()
}

Write-Output 'PASS: KCP relay owns one Socket/worker/router and stops cleanly in all lifecycle states.'
