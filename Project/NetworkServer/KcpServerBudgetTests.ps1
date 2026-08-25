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

$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_budgetType = $routeC_assembly.GetType(
    'FrameSyncServer.KcpServerRoundBudget',
    $true)
$routeC_diagnosticsType = $routeC_assembly.GetType(
    'FrameSyncServer.KcpServerDiagnostics',
    $true)
$routeC_diagnostics = [Activator]::CreateInstance($routeC_diagnosticsType)
$routeC_budget = [Activator]::CreateInstance(
    $routeC_budgetType,
    @($routeC_diagnostics, [int64]1000))

for ($routeC_index = 0; $routeC_index -lt 64; $routeC_index++)
{
    Assert-KcpTrue $routeC_budget.TryTakeDatagram() "ASSERT datagram-slot-${routeC_index}: legal slot rejected."
}
Assert-KcpTrue (-not $routeC_budget.TryTakeDatagram()) 'ASSERT datagram-limit: 65th datagram was accepted.'
Assert-KcpTrue (-not $routeC_budget.TryTakeDatagram()) 'ASSERT datagram-repeat: exhausted budget reopened.'
Assert-KcpEqual 1 $routeC_diagnostics.ReceiveBudgetExhaustionCount 'ASSERT datagram-counter: exhaustion was not counted once.'

foreach ($routeC_player in @(0, 1))
{
    for ($routeC_index = 0; $routeC_index -lt 64; $routeC_index++)
    {
        Assert-KcpTrue $routeC_budget.TryTakeMessage([byte]$routeC_player) "ASSERT player${routeC_player}-slot-${routeC_index}: legal slot rejected."
    }
    Assert-KcpTrue (-not $routeC_budget.TryTakeMessage([byte]$routeC_player)) "ASSERT player${routeC_player}-limit: 65th message was accepted."
}
Assert-KcpEqual 1 $routeC_diagnostics.GetMessageBudgetExhaustionCount([byte]0) 'ASSERT player0-counter: exhaustion mismatch.'
Assert-KcpEqual 1 $routeC_diagnostics.GetMessageBudgetExhaustionCount([byte]1) 'ASSERT player1-counter: exhaustion mismatch.'

$routeC_beforeDeadline = [int64](1000 + ([Diagnostics.Stopwatch]::Frequency * 2 / 1000) - 1)
$routeC_atDeadline = [int64](1000 + ([Diagnostics.Stopwatch]::Frequency * 2 / 1000))
Assert-KcpTrue $routeC_budget.HasLiveSliceBudget($routeC_beforeDeadline) 'ASSERT slice-before: budget exhausted before 2ms.'
Assert-KcpTrue (-not $routeC_budget.HasLiveSliceBudget($routeC_atDeadline)) 'ASSERT slice-at: 2ms exhaustion was missed.'
Assert-KcpTrue (-not $routeC_budget.HasLiveSliceBudget($routeC_atDeadline + 1)) 'ASSERT slice-repeat: exhausted slice reopened.'
Assert-KcpEqual 1 $routeC_diagnostics.LiveSliceBudgetExhaustionCount 'ASSERT slice-counter: exhaustion was not counted once.'

Write-Output 'PASS: KCP server enforces and independently counts 64/64/2ms round budgets.'
