param(
    [string]$ServerPath = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ServerPath))
{
    $routeC_scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ServerPath = Join-Path $routeC_scriptDirectory 'NetworkServer.exe'
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

function New-RouteCProfile
{
    param(
        [Type]$ProfileType,
        [int]$BaseDelayMs = 0,
        [int]$JitterMs = 0,
        [int]$ApplicationReorderPercent = 0,
        [int]$ReorderExtraDelayMs = 0,
        [int]$DuplicatePercent = 0,
        [int]$RecoveredLossPercent = 0,
        [int]$LossRecoveryDelayMs = 0,
        [uint32]$Seed = 1
    )

    return [Activator]::CreateInstance(
        $ProfileType,
        @(
            $BaseDelayMs,
            $JitterMs,
            $ApplicationReorderPercent,
            $ReorderExtraDelayMs,
            $DuplicatePercent,
            $RecoveredLossPercent,
            $LossRecoveryDelayMs,
            $Seed))
}

function Add-RouteCScheduledPacket
{
    param(
        [Reflection.MethodInfo]$ScheduleMethod,
        $Scheduler,
        [long]$EnqueuedAtMs,
        [int]$SenderIndex,
        [long]$SenderSequence,
        [uint32]$Raw,
        [int]$FrameID
    )

    return $ScheduleMethod.Invoke(
        $Scheduler,
        @($EnqueuedAtMs, $SenderIndex, $SenderSequence, $Raw, $FrameID))
}

function Remove-RouteCDuePacket
{
    param(
        [Reflection.MethodInfo]$DequeueMethod,
        $Scheduler,
        [long]$NowMs
    )

    $routeC_arguments = @($NowMs, $null)
    $routeC_succeeded = [bool]$DequeueMethod.Invoke(
        $Scheduler,
        $routeC_arguments)
    return [pscustomobject]@{
        Succeeded = $routeC_succeeded
        Delivery = $routeC_arguments[1]
    }
}

if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf))
{
    throw "Server assembly does not exist: $ServerPath"
}

$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_profileType = $routeC_assembly.GetType(
    'FrameSyncDemo.NetworkLabProfile',
    $true)
$routeC_schedulerType = $routeC_assembly.GetType(
    'FrameSyncServer.DeterministicPacketScheduler',
    $true)
$routeC_optionsType = $routeC_assembly.GetType(
    'FrameSyncServer.NetworkLabOptions',
    $true)
$routeC_traceWriterType = $routeC_assembly.GetType(
    'FrameSyncServer.NetworkTraceWriter',
    $true)

$routeC_scheduleMethod = $routeC_schedulerType.GetMethod('Schedule')
$routeC_dequeueMethod = $routeC_schedulerType.GetMethod('TryDequeueDue')
$routeC_nextDueProperty = $routeC_schedulerType.GetProperty('NextDueTimestampMs')
$routeC_countProperty = $routeC_schedulerType.GetProperty('Count')
Assert-RouteCTrue ($null -ne $routeC_scheduleMethod) 'Schedule method is missing.'
Assert-RouteCTrue ($null -ne $routeC_dequeueMethod) 'TryDequeueDue method is missing.'

$routeC_fixed100 = New-RouteCProfile -ProfileType $routeC_profileType -BaseDelayMs 100
$routeC_scheduler = [Activator]::CreateInstance(
    $routeC_schedulerType,
    @($routeC_fixed100))
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_scheduler 0 0 0 0x1000 0 | Out-Null
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_scheduler 33 0 1 0x1001 1 | Out-Null
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_scheduler 66 0 2 0x1002 2 | Out-Null

Assert-RouteCEqual 3 $routeC_countProperty.GetValue($routeC_scheduler) 'Fixed-delay queue count mismatch.'
Assert-RouteCEqual 100 $routeC_nextDueProperty.GetValue($routeC_scheduler) 'First due timestamp mismatch.'
$routeC_beforeFirst = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_scheduler 99
Assert-RouteCTrue (-not $routeC_beforeFirst.Succeeded) 'Packet was released before its due timestamp.'
$routeC_first = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_scheduler 100
Assert-RouteCTrue $routeC_first.Succeeded 'First packet was not released at its due timestamp.'
Assert-RouteCEqual 0 $routeC_first.Delivery.FrameID 'First frame mismatch.'
Assert-RouteCEqual 100 $routeC_first.Delivery.DueAtMs 'First packet due timestamp mismatch.'
$routeC_beforeSecond = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_scheduler 132
Assert-RouteCTrue (-not $routeC_beforeSecond.Succeeded) 'Second packet was released early.'
$routeC_second = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_scheduler 133
Assert-RouteCTrue $routeC_second.Succeeded 'Second packet was not released at 133ms.'
Assert-RouteCEqual 1 $routeC_second.Delivery.FrameID 'Second frame mismatch.'

$routeC_stableScheduler = [Activator]::CreateInstance(
    $routeC_schedulerType,
    @($routeC_fixed100))
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_stableScheduler 0 1 0 0x2000 2 | Out-Null
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_stableScheduler 0 0 5 0x1000 1 | Out-Null
$routeC_stableFirst = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_stableScheduler 100
$routeC_stableSecond = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_stableScheduler 100
Assert-RouteCEqual 0 $routeC_stableFirst.Delivery.SenderIndex 'Same-due sender ordering is unstable.'
Assert-RouteCEqual 1 $routeC_stableSecond.Delivery.SenderIndex 'Same-due second sender mismatch.'

$routeC_duplicateProfile = New-RouteCProfile `
    -ProfileType $routeC_profileType `
    -DuplicatePercent 100
$routeC_duplicateScheduler = [Activator]::CreateInstance(
    $routeC_schedulerType,
    @($routeC_duplicateProfile))
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_duplicateScheduler 10 0 0 0x3000 3 | Out-Null
Assert-RouteCEqual 2 $routeC_countProperty.GetValue($routeC_duplicateScheduler) 'Duplicate profile did not enqueue exactly two deliveries.'
$routeC_primary = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_duplicateScheduler 10
$routeC_copy = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_duplicateScheduler 10
Assert-RouteCEqual 0 $routeC_primary.Delivery.CopyIndex 'Primary copy index mismatch.'
Assert-RouteCEqual 1 $routeC_copy.Delivery.CopyIndex 'Duplicate copy index mismatch.'

$routeC_lossProfile = New-RouteCProfile `
    -ProfileType $routeC_profileType `
    -RecoveredLossPercent 50 `
    -LossRecoveryDelayMs 100 `
    -Seed 1
$routeC_lossScheduler = [Activator]::CreateInstance(
    $routeC_schedulerType,
    @($routeC_lossProfile))
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_lossScheduler 0 0 0 0x4000 4 | Out-Null
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_lossScheduler 33 0 1 0x4001 5 | Out-Null
$routeC_beforeRecovery = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_lossScheduler 99
Assert-RouteCTrue (-not $routeC_beforeRecovery.Succeeded) 'Recovered loss did not hold the sender stream.'
$routeC_recoveredFirst = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_lossScheduler 100
$routeC_recoveredSecond = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_lossScheduler 100
Assert-RouteCEqual 4 $routeC_recoveredFirst.Delivery.FrameID 'Recovered-loss first frame mismatch.'
Assert-RouteCEqual 5 $routeC_recoveredSecond.Delivery.FrameID 'Recovered-loss barrier did not preserve sender order.'

$routeC_jitterProfile = New-RouteCProfile `
    -ProfileType $routeC_profileType `
    -BaseDelayMs 100 `
    -JitterMs 100 `
    -Seed 4
$routeC_jitterScheduler = [Activator]::CreateInstance(
    $routeC_schedulerType,
    @($routeC_jitterProfile))
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_jitterScheduler 0 0 0 0x7000 9 | Out-Null
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_jitterScheduler 33 0 1 0x7001 10 | Out-Null
$routeC_beforeJitterBarrier = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_jitterScheduler 150
Assert-RouteCTrue (-not $routeC_beforeJitterBarrier.Succeeded) 'Jitter bypassed reliable sender ordering without an application-reorder label.'
$routeC_jitterFirst = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_jitterScheduler 151
$routeC_jitterSecond = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_jitterScheduler 151
Assert-RouteCEqual 9 $routeC_jitterFirst.Delivery.FrameID 'Reliable jitter order lost the earlier sender sequence.'
Assert-RouteCEqual 10 $routeC_jitterSecond.Delivery.FrameID 'Reliable jitter barrier did not release the held successor.'

$routeC_reorderProfile = New-RouteCProfile `
    -ProfileType $routeC_profileType `
    -ApplicationReorderPercent 50 `
    -RecoveredLossPercent 50 `
    -LossRecoveryDelayMs 100 `
    -Seed 3
$routeC_reorderScheduler = [Activator]::CreateInstance(
    $routeC_schedulerType,
    @($routeC_reorderProfile))
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_reorderScheduler 0 0 0 0x5000 6 | Out-Null
Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_reorderScheduler 33 0 1 0x5001 7 | Out-Null
$routeC_reordered = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_reorderScheduler 33
Assert-RouteCEqual 7 $routeC_reordered.Delivery.FrameID 'Explicit application reorder did not pass the held message.'
Assert-RouteCTrue $routeC_reordered.Delivery.Decision.ApplicationReordered 'Application reorder label is missing.'
$routeC_held = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_reorderScheduler 100
Assert-RouteCEqual 6 $routeC_held.Delivery.FrameID 'Recovered-loss packet was not released at its own due time.'

$routeC_fixedDecision = Add-RouteCScheduledPacket $routeC_scheduleMethod $routeC_stableScheduler 200 0 6 0x6000 8
$routeC_formatDecision = $routeC_traceWriterType.GetMethod('FormatDecision')
$routeC_formatTiming = $routeC_traceWriterType.GetMethod('FormatTiming')
Assert-RouteCTrue ($null -ne $routeC_formatDecision) 'FormatDecision method is missing.'
Assert-RouteCTrue ($null -ne $routeC_formatTiming) 'FormatTiming method is missing.'
$routeC_decisionLine = $routeC_formatDecision.Invoke(
    $null,
    @(0, [long]6, 8, [uint32]0x6000, $routeC_fixedDecision))
Assert-RouteCTrue ($routeC_decisionLine.Contains('"sender":0')) 'Decision trace is not structured JSONL.'
Assert-RouteCTrue (-not $routeC_decisionLine.Contains('enqueuedAtMs')) 'Decision trace contains an observational enqueue timestamp.'
Assert-RouteCTrue (-not $routeC_decisionLine.Contains('actualSendAtMs')) 'Decision trace contains an observational send timestamp.'
$routeC_timingDelivery = Remove-RouteCDuePacket $routeC_dequeueMethod $routeC_stableScheduler 300
$routeC_timingLine = $routeC_formatTiming.Invoke(
    $null,
    @($routeC_timingDelivery.Delivery, [long]305, [long]12))
Assert-RouteCTrue ($routeC_timingLine.Contains('"enqueuedAtMs"')) 'Timing trace omits enqueue timestamp.'
Assert-RouteCTrue ($routeC_timingLine.Contains('"actualSendAtMs"')) 'Timing trace omits actual send timestamp.'
Assert-RouteCTrue ($routeC_timingLine.Contains('"latenessMs"')) 'Timing trace omits scheduler lateness.'

$routeC_mode100 = $routeC_optionsType.GetMethod('FromInteractiveChoice').Invoke(
    $null,
    @('1'))
Assert-RouteCEqual 100 $routeC_mode100.Profile.BaseDelayMs 'Interactive mode 1 is not fixed 100ms.'
$routeC_mode200 = $routeC_optionsType.GetMethod('FromInteractiveChoice').Invoke(
    $null,
    @('2'))
Assert-RouteCEqual 200 $routeC_mode200.Profile.BaseDelayMs 'Interactive mode 2 is not fixed 200ms.'

$routeC_parseMethod = $routeC_optionsType.GetMethod('Parse')
$routeC_parsed = $routeC_parseMethod.Invoke(
    $null,
    @(,[string[]]@(
        '--delay-ms', '90',
        '--jitter-ms', '7',
        '--reorder-percent', '11',
        '--reorder-delay-ms', '13',
        '--duplicate-percent', '17',
        '--loss-percent', '19',
        '--loss-recovery-ms', '23',
        '--seed', '29',
        '--decision-trace', 'decision.jsonl',
        '--timing-trace', 'timing.jsonl')))
Assert-RouteCEqual 90 $routeC_parsed.Profile.BaseDelayMs 'Parsed base delay mismatch.'
Assert-RouteCEqual 7 $routeC_parsed.Profile.JitterMs 'Parsed jitter mismatch.'
Assert-RouteCEqual 11 $routeC_parsed.Profile.ApplicationReorderPercent 'Parsed reorder percentage mismatch.'
Assert-RouteCEqual 13 $routeC_parsed.Profile.ReorderExtraDelayMs 'Parsed reorder delay mismatch.'
Assert-RouteCEqual 17 $routeC_parsed.Profile.DuplicatePercent 'Parsed duplicate percentage mismatch.'
Assert-RouteCEqual 19 $routeC_parsed.Profile.RecoveredLossPercent 'Parsed recovered-loss percentage mismatch.'
Assert-RouteCEqual 23 $routeC_parsed.Profile.LossRecoveryDelayMs 'Parsed recovery delay mismatch.'
Assert-RouteCEqual 29 $routeC_parsed.Profile.Seed 'Parsed seed mismatch.'
Assert-RouteCEqual 'decision.jsonl' $routeC_parsed.DecisionTracePath 'Decision trace path mismatch.'
Assert-RouteCEqual 'timing.jsonl' $routeC_parsed.TimingTracePath 'Timing trace path mismatch.'

Write-Output 'PASS: scheduler, recovery barrier, explicit reorder, trace separation, and presets are correct.'
