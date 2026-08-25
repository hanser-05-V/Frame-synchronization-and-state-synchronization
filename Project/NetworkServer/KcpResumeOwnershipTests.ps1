param(
    [Parameter(Mandatory = $true)]
    [string]$ServerPath
)

$ErrorActionPreference = 'Stop'

function Assert-OwnershipTrue
{
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-OwnershipEqual
{
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual)
        { throw "$Message Expected=$Expected Actual=$Actual" }
}

function Read-OwnershipSource
{
    param([string]$RelativePath)
    $routeC_root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    return Get-Content -Raw -LiteralPath (Join-Path $routeC_root $RelativePath)
}

Assert-OwnershipTrue (Test-Path -LiteralPath $ServerPath -PathType Leaf) `
    'ASSERT assembly: Task 9 servercheck is missing.'
$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_transportType = $routeC_assembly.GetType(
    'FrameSyncDemo.KcpUdpClientTransport',
    $true)
$routeC_coordinatorType = $routeC_assembly.GetType(
    'FrameSyncDemo.KcpClientResumeCoordinator',
    $true)
$routeC_socketType = [Net.Sockets.Socket]
$routeC_threadType = [Threading.Thread]
$routeC_historyType = $routeC_assembly.GetType(
    'FrameSyncDemo.OutboundActualHistory',
    $true)
$routeC_fields = @($routeC_transportType.GetFields(
    [Reflection.BindingFlags]'Instance,NonPublic'))
Assert-OwnershipEqual 1 @($routeC_fields | Where-Object FieldType -eq $routeC_socketType).Count `
    'ASSERT client-socket-owner: KCP client must own exactly one Socket.'
Assert-OwnershipEqual 1 @($routeC_fields | Where-Object FieldType -eq $routeC_threadType).Count `
    'ASSERT client-worker-owner: KCP client must own exactly one worker Thread.'
$routeC_coordinatorFields = @($routeC_coordinatorType.GetFields(
    [Reflection.BindingFlags]'Instance,NonPublic'))
Assert-OwnershipEqual 1 @($routeC_coordinatorFields | Where-Object FieldType -eq $routeC_historyType).Count `
    'ASSERT history-owner: resume coordinator must reference exactly one existing outbound history.'

$routeC_gameController = Read-OwnershipSource `
    'Project\Frame Synchronization\Assets\Scripts\GameController.cs'
$routeC_networkClient = Read-OwnershipSource `
    'Project\Frame Synchronization\Assets\Scripts\Network\NetworkClient.cs'
$routeC_kcpTransport = Read-OwnershipSource `
    'Project\Frame Synchronization\Assets\Scripts\Network\KcpUdpClientTransport.cs'
$routeC_kcpState = Read-OwnershipSource `
    'Project\Frame Synchronization\Assets\Scripts\Network\KcpUdpClientStateMachine.cs'
$routeC_kcpCoordinator = Read-OwnershipSource `
    'Project\Frame Synchronization\Assets\Scripts\Network\KcpClientResumeCoordinator.cs'
$routeC_tcpClient = Read-OwnershipSource `
    'Project\Frame Synchronization\Assets\Scripts\Network\TcpClientTransport.cs'
$routeC_rawClient = Read-OwnershipSource `
    'Project\Frame Synchronization\Assets\Scripts\Network\RawUdpInputTransport.cs'
$routeC_tcpServer = Read-OwnershipSource `
    'Project\NetworkServer\TcpRelayServer.cs'
$routeC_rawServer = Read-OwnershipSource `
    'Project\NetworkServer\RawUdpRelayServer.cs'
$routeC_kcpServer = Read-OwnershipSource `
    'Project\NetworkServer\ServerSessionRouter.cs'

foreach ($routeC_source in @($routeC_gameController, $routeC_networkClient))
{
    Assert-OwnershipTrue (-not [Regex]::IsMatch(
        $routeC_source,
        '(Concurrent|Sorted)?Dictionary\s*<')) `
        'ASSERT second-ledger: orchestration layer added a mutable FrameID dictionary.'
}
Assert-OwnershipEqual 1 ([Regex]::Matches(
    $routeC_kcpTransport,
    'new\s+OutboundActualHistory\s*\(').Count) `
    'ASSERT outbound-truth: KCP worker must construct exactly one outbound Actual history.'
Assert-OwnershipTrue (-not $routeC_networkClient.Contains('OutboundActualHistory')) `
    'ASSERT network-client-history: NetworkClient added a second outbound history.'
Assert-OwnershipTrue $routeC_kcpTransport.Contains('PumpResumeUpload(') `
    'ASSERT client-upload-pump: worker does not progressively pump resume uploads.'
Assert-OwnershipTrue ([Regex]::IsMatch(
    $routeC_kcpTransport,
    'sentCount\s*<\s*\r?\n?\s*RouteCProtocolConstants\.MaximumMessagesPerSessionPerRound')) `
    'ASSERT client-upload-message-budget: resume upload lost the 64-message round cap.'
Assert-OwnershipTrue $routeC_kcpTransport.Contains(
    'System.Diagnostics.Stopwatch.GetTimestamp() - startedAt') `
    'ASSERT client-upload-time-budget: resume upload lost the live-slice time cap.'
Assert-OwnershipTrue ([Regex]::IsMatch(
    $routeC_kcpTransport,
    'stateMachine\.State\s*==\s*NetworkSessionState\.Resuming\s*\|\|\s*\r?\n?\s*resumeCoordinator\.IsActive')) `
    'ASSERT pre-accepted-progress: transport only observes replay after Accepted.'
Assert-OwnershipTrue (-not [Regex]::IsMatch(
    $routeC_kcpCoordinator,
    '(Concurrent|Sorted)?Dictionary\s*<')) `
    'ASSERT coordinator-second-truth: progress retention added a mutable dictionary.'

$routeC_resumeNetworkSource =
    $routeC_kcpTransport + $routeC_kcpState + $routeC_kcpCoordinator + $routeC_kcpServer
foreach ($routeC_forbidden in @(
    'RestoreWorld',
    'RestoreSnapshot',
    'TryRestore',
    'FrameSnapshot',
    'BallEntity',
    'PlayerEntity'))
{
    Assert-OwnershipTrue (-not $routeC_resumeNetworkSource.Contains($routeC_forbidden)) `
        "ASSERT world-ownership: resume network code contains $routeC_forbidden."
}
foreach ($routeC_rawTerm in @(
    'RawFault',
    'PacketSequence',
    'RawInputWindow',
    'LateRecovery',
    'GapGrace'))
{
    Assert-OwnershipTrue (-not $routeC_resumeNetworkSource.Contains($routeC_rawTerm)) `
        "ASSERT raw-kcp-isolation: KCP resume imported $routeC_rawTerm."
}
foreach ($routeC_legacySource in @(
    $routeC_tcpClient,
    $routeC_rawClient,
    $routeC_tcpServer,
    $routeC_rawServer))
{
    foreach ($routeC_resumeTerm in @(
        'ResumeProbe',
        'ResumeState',
        'ResumeAccepted',
        'ResumeComplete',
        'NetworkSessionState.Resuming'))
    {
        Assert-OwnershipTrue (-not $routeC_legacySource.Contains($routeC_resumeTerm)) `
            "ASSERT legacy-isolation: TCP/Raw imported $routeC_resumeTerm."
    }
}

Assert-OwnershipEqual 1 ([Regex]::Matches(
    $routeC_gameController,
    '_frameSyncCoordinator\.RecordActual\(').Count) `
    'ASSERT ledger-entry: GameController Actual arrival entry count changed.'

Write-Output 'PASS: Task 9 ownership keeps one worker/socket/history, one Ledger entry, no world restore, and strict TCP/Raw/KCP isolation.'
