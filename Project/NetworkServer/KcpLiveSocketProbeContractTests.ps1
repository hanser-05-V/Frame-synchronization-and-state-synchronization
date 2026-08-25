param(
    [string]$ProbePath = (Join-Path $PSScriptRoot 'KcpLiveSocketProbe.ps1'),
    [Parameter(Mandatory = $true)]
    [string]$ServerPath,
    [string]$MatrixPath = (Join-Path $PSScriptRoot 'KcpIntervalMatrix.ps1'),
    [switch]$IncludeResumeScenarios
)

$ErrorActionPreference = 'Stop'

function Assert-RouteCProbeContract
{
    param([bool]$Condition, [string]$Message)
    if (-not $Condition)
    {
        throw $Message
    }
}

Assert-RouteCProbeContract `
    (Test-Path -LiteralPath $ProbePath -PathType Leaf) `
    "RED: live KCP probe does not exist: $ProbePath"

$routeC_contractJson = & $ProbePath -ContractOnly
$routeC_contract = $routeC_contractJson | ConvertFrom-Json

Assert-RouteCProbeContract `
    ($routeC_contract.schema -eq 'route-c-kcp-live-socket-v1') `
    'Live KCP probe schema changed.'
Assert-RouteCProbeContract `
    (($routeC_contract.allowedIntervalsMs -join ',') -eq '1,5,10,20') `
    'Live KCP probe interval contract changed.'
Assert-RouteCProbeContract `
    (($routeC_contract.allowedScenarios -join ',') -eq `
        'clean,reconnect,grace-expired,history-expired') `
    'Live KCP probe scenario contract changed.'
Assert-RouteCProbeContract `
    ($routeC_contract.minimumFramesPerClient -eq 900) `
    'Live KCP probe permits fewer than 900 frames per client.'
Assert-RouteCProbeContract `
    ($routeC_contract.businessInputBytes -eq 8) `
    'Live KCP probe changed the business input size.'
Assert-RouteCProbeContract `
    ($routeC_contract.logicalCadenceMs -eq 33) `
    'Live KCP probe changed the project logical cadence.'

$routeC_timeoutNames = @(
    'startupMs',
    'readyMs',
    'runMs',
    'stopMs',
    'cleanupMs')
foreach ($routeC_timeoutName in $routeC_timeoutNames)
{
    $routeC_timeout = $routeC_contract.timeouts.$routeC_timeoutName
    Assert-RouteCProbeContract `
        ($null -ne $routeC_timeout -and $routeC_timeout -gt 0) `
        "Live KCP probe timeout '$routeC_timeoutName' is not bounded."
}

Assert-RouteCProbeContract `
    (($routeC_contract.forbiddenFields -join ',') -eq `
        'token,nonce,resumeAttemptID,sessionID') `
    'Live KCP probe secret-field exclusion changed.'
Assert-RouteCProbeContract `
    ($routeC_contract.processCleanup -eq 'owned-exact-pid-only') `
    'Live KCP probe process cleanup is not exact-PID scoped.'
Assert-RouteCProbeContract `
    ($routeC_contract.portStrategy -eq 'dynamic-loopback') `
    'Live KCP probe does not require dynamic loopback ports.'
Assert-RouteCProbeContract `
    ($routeC_contract.cleanServerLaunchMode -eq
        'networkserver-exe-cli' -and
     $routeC_contract.cleanServerPort -eq 8888) `
    'Clean live runs do not exercise the real server CLI on port 8888.'

$routeC_smokeJson = & $ProbePath `
    -ServerPath $ServerPath `
    -Scenario clean `
    -LifecycleSmoke
$routeC_smoke = $routeC_smokeJson | ConvertFrom-Json
Assert-RouteCProbeContract `
    ($routeC_smoke.status -eq 'PASS') `
    'Live KCP lifecycle smoke did not pass.'
Assert-RouteCProbeContract `
    ($routeC_smoke.boundPort -gt 0) `
    'Live KCP lifecycle smoke did not bind a dynamic port.'
Assert-RouteCProbeContract `
    (($routeC_smoke.players -join ',') -eq '0,1') `
    'Live KCP lifecycle smoke did not start two distinct clients.'
Assert-RouteCProbeContract `
    $routeC_smoke.serverStopped `
    'Live KCP lifecycle smoke did not stop the server worker.'
Assert-RouteCProbeContract `
    $routeC_smoke.portReleased `
    'Live KCP lifecycle smoke did not release its dynamic port.'

if ($IncludeResumeScenarios)
{
    $routeC_reconnect = (& $ProbePath `
        -ServerPath $ServerPath `
        -Scenario reconnect `
        -IntervalMs 10) | ConvertFrom-Json
    Assert-RouteCProbeContract `
        ($routeC_reconnect.status -eq 'PASS') `
        'Recoverable live reconnect did not pass.'
    Assert-RouteCProbeContract `
        $routeC_reconnect.endpointChanged `
        'Recoverable reconnect did not use a new UDP Endpoint.'
    Assert-RouteCProbeContract `
        ($routeC_reconnect.generationBefore -eq 1 -and
         $routeC_reconnect.generationAfter -eq 2) `
        'Recoverable reconnect did not switch generation exactly once.'
    Assert-RouteCProbeContract `
        ($routeC_reconnect.recoverableGapFrames -eq 8 -and
         $routeC_reconnect.replayedFrames -eq 8) `
        'Recoverable reconnect did not deliver its real KCP replay gap.'
    Assert-RouteCProbeContract `
        (($routeC_reconnect.finalStates -join ',') -eq 'Running,Running') `
        'Recoverable reconnect did not return both peers to Running.'

    $routeC_grace = (& $ProbePath `
        -ServerPath $ServerPath `
        -Scenario grace-expired `
        -IntervalMs 10) | ConvertFrom-Json
    Assert-RouteCProbeContract `
        ($routeC_grace.status -eq 'PASS') `
        'Grace-expired live scenario did not pass.'
    Assert-RouteCProbeContract `
        ($routeC_grace.rejectedPeers -eq 2) `
        'Grace-expired rejection was not observed by both peers.'
    Assert-RouteCProbeContract `
        ($routeC_grace.rejectionReason -eq 'ResumeGraceExpired') `
        'Grace-expired scenario reported the wrong wire reason.'
    Assert-RouteCProbeContract `
        ($routeC_grace.diagnosticFailureKind -eq 'GraceExpired') `
        'Grace-expired scenario lacks its internal diagnostic reason.'

    $routeC_history = (& $ProbePath `
        -ServerPath $ServerPath `
        -Scenario history-expired `
        -IntervalMs 10) | ConvertFrom-Json
    Assert-RouteCProbeContract `
        ($routeC_history.status -eq 'PASS') `
        'History-expired live scenario did not pass.'
    Assert-RouteCProbeContract `
        $routeC_history.endpointChanged `
        'History-expired reconnect did not use a new UDP Endpoint.'
    Assert-RouteCProbeContract `
        ($routeC_history.rejectedPeers -eq 2) `
        'History-expired rejection was not observed by both peers.'
    Assert-RouteCProbeContract `
        ($routeC_history.rejectionReason -eq 'UnsafeResume') `
        'History-expired scenario reported the wrong wire reason.'
    Assert-RouteCProbeContract `
        ($routeC_history.diagnosticFailureKind -eq `
            'RangeCapacityExceeded') `
        'History-expired scenario lacks the capacity diagnostic reason.'
}

Assert-RouteCProbeContract `
    (Test-Path -LiteralPath $MatrixPath -PathType Leaf) `
    "RED: KCP interval matrix does not exist: $MatrixPath"
$routeC_matrixContract = (& $MatrixPath -ContractOnly) | ConvertFrom-Json
Assert-RouteCProbeContract `
    ($routeC_matrixContract.schema -eq
        'route-c-kcp-interval-matrix-v1') `
    'KCP interval matrix schema changed.'
Assert-RouteCProbeContract `
    (($routeC_matrixContract.intervalsMs -join ',') -eq '1,5,10,20') `
    'KCP interval matrix candidates changed.'
Assert-RouteCProbeContract `
    ($routeC_matrixContract.runsPerInterval -eq 3) `
    'KCP interval matrix no longer runs each candidate three times.'
Assert-RouteCProbeContract `
    ($routeC_matrixContract.runSummaryPattern -eq
        'p2f-kcp-interval-{interval}ms-run{run}-summary.json') `
    'KCP interval matrix no longer retains per-run summary evidence.'
Assert-RouteCProbeContract `
    ($routeC_matrixContract.framesPerClient -eq 900 -and
     $routeC_matrixContract.logicalCadenceMs -eq 33) `
    'KCP interval matrix business script changed.'
Assert-RouteCProbeContract `
    ($routeC_matrixContract.processIsolation -eq
        'hidden-owned-exact-pid') `
    'KCP interval matrix child cleanup is not exact-PID scoped.'
Assert-RouteCProbeContract `
    ($routeC_matrixContract.serverLaunchMode -eq
        'networkserver-exe-cli') `
    'KCP interval matrix does not exercise the real server CLI.'
Assert-RouteCProbeContract `
    ($routeC_matrixContract.serverPidHandoff -eq
        'per-run-exact-pid-file') `
    'KCP interval matrix cannot clean the exact server PID on timeout.'
Assert-RouteCProbeContract `
    ($routeC_matrixContract.tcpControlSummaryName -eq
        'p2f-tcp-control-summary.json') `
    'TCP control summary evidence name changed.'
Assert-RouteCProbeContract `
    ($routeC_matrixContract.tcpControlFaultSemantics -eq
        'tcp-recovered-loss-hol/application') `
    'TCP control fault semantics are not isolated from UDP drop.'

Write-Output 'PASS: live KCP probe command, schema, timeout, secrecy, and cleanup contracts are locked.'
