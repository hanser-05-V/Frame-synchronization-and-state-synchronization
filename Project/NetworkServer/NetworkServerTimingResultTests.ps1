param(
    [Parameter(Mandatory = $true)]
    [string]$ResultPath
)

$ErrorActionPreference = 'Stop'

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

if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf))
{
    throw "Timing result does not exist: $ResultPath"
}

$routeC_result = Get-Content -Raw -LiteralPath $ResultPath | ConvertFrom-Json
Assert-RouteCEqual 'tcp' $routeC_result.transport 'Timing result transport label changed.'
Assert-RouteCEqual `
    'tcp-recovered-loss-hol' `
    $routeC_result.faultSemantics.recoveredLoss `
    'Recovered-loss/HOL semantics were not labeled as TCP.'
Assert-RouteCEqual `
    'tcp-application-reorder' `
    $routeC_result.faultSemantics.applicationReorder `
    'Application reorder semantics were not labeled as TCP.'
Assert-RouteCEqual `
    'tcp-application-duplicate' `
    $routeC_result.faultSemantics.duplicate `
    'Application duplicate semantics were not labeled as TCP.'

$routeC_uniqueFrames = @(
    $routeC_result.samples |
        ForEach-Object { [int]$_.frame } |
        Sort-Object -Unique)
Assert-RouteCEqual `
    ([int]$routeC_result.packetCount) `
    $routeC_uniqueFrames.Count `
    'Timing completion must include every original frame, not duplicate copies.'
for ($routeC_frame = 0; $routeC_frame -lt $routeC_result.packetCount; $routeC_frame++)
{
    Assert-RouteCEqual `
        $routeC_frame `
        $routeC_uniqueFrames[$routeC_frame] `
        "Timing result is missing original frame $routeC_frame."
}

Write-Output 'PASS: timing evidence retains explicit TCP recovered-loss/HOL and application-fault labels.'
