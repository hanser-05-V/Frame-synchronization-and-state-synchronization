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

$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_type = $routeC_assembly.GetType(
    'FrameSyncDemo.RouteCKcpSession',
    $true)
$routeC_property = $routeC_type.GetProperty('HasPendingBusinessInput')
Assert-KcpTrue `
    ($null -ne $routeC_property) `
    'RED: RouteCKcpSession.HasPendingBusinessInput is missing.'
Assert-KcpTrue `
    ($routeC_property.PropertyType -eq [bool]) `
    'ASSERT pending-type: property must be Boolean.'
Assert-KcpTrue `
    ($null -eq $routeC_property.GetSetMethod()) `
    'ASSERT pending-readonly: property must not expose a setter.'

Write-Output 'PASS: RouteCKcpSession exposes a read-only non-consuming pending-message seam.'
