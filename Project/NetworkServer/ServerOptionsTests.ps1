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

function Invoke-RouteCParse
{
    param(
        [Reflection.MethodInfo]$ParseMethod,
        [string[]]$Arguments
    )

    return $ParseMethod.Invoke($null, @(,[string[]]$Arguments))
}

function Assert-RouteCParseRejected
{
    param(
        [Reflection.MethodInfo]$ParseMethod,
        [string[]]$Arguments,
        [string]$ExpectedMessagePart
    )

    try
    {
        Invoke-RouteCParse $ParseMethod $Arguments | Out-Null
        throw "Expected command line to be rejected: $($Arguments -join ' ')"
    }
    catch [Management.Automation.MethodInvocationException]
    {
        $routeC_message = $_.Exception.InnerException.Message
        Assert-RouteCTrue `
            ($routeC_message.Contains($ExpectedMessagePart)) `
            "Unexpected rejection message: $routeC_message"
    }
}

if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf))
{
    throw "Server assembly does not exist: $ServerPath"
}

$routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
$routeC_optionsType = $routeC_assembly.GetType(
    'FrameSyncServer.ServerOptions',
    $true)
$routeC_parseMethod = $routeC_optionsType.GetMethod('Parse')
$routeC_interactiveMethod = $routeC_optionsType.GetMethod(
    'FromInteractiveChoice')
Assert-RouteCTrue ($null -ne $routeC_parseMethod) 'ServerOptions.Parse is missing.'
Assert-RouteCTrue `
    ($null -ne $routeC_interactiveMethod) `
    'ServerOptions.FromInteractiveChoice is missing.'

$routeC_legacy = Invoke-RouteCParse $routeC_parseMethod @()
Assert-RouteCTrue $routeC_legacy.UseLegacyInteractive 'No arguments must select the legacy interactive entry.'
Assert-RouteCEqual 'Tcp' $routeC_legacy.Transport.ToString() 'Legacy entry must select TCP.'

$routeC_expectedDelays = @(0, 100, 200)
for ($routeC_index = 0; $routeC_index -lt $routeC_expectedDelays.Count; $routeC_index++)
{
    $routeC_choice = $routeC_index.ToString()
    $routeC_interactive = $routeC_interactiveMethod.Invoke(
        $null,
        @($routeC_choice))
    Assert-RouteCTrue `
        (-not $routeC_interactive.UseLegacyInteractive) `
        "Interactive choice $routeC_choice was not resolved."
    Assert-RouteCEqual `
        'Tcp' `
        $routeC_interactive.Transport.ToString() `
        "Interactive choice $routeC_choice did not select TCP."
    Assert-RouteCEqual `
        $routeC_expectedDelays[$routeC_index] `
        $routeC_interactive.LabOptions.Profile.BaseDelayMs `
        "Interactive choice $routeC_choice changed its P2-E delay profile."
}

$routeC_defaultExplicit = Invoke-RouteCParse `
    $routeC_parseMethod `
    @('--delay-ms', '0')
Assert-RouteCEqual `
    'KcpUdp' `
    $routeC_defaultExplicit.Transport.ToString() `
    'Explicit command-line runs without --transport must default to KCP.'
Assert-RouteCEqual `
    10 `
    $routeC_defaultExplicit.KcpIntervalMs `
    'The Task 7 KCP smoke interval default must remain 10ms.'
Assert-RouteCEqual `
    6 `
    $routeC_defaultExplicit.RawWindowSize `
    'The Raw UDP window default must remain 6 without selecting Raw UDP.'

$routeC_tcp = Invoke-RouteCParse `
    $routeC_parseMethod `
    @(
        '--transport', 'tcp',
        '--kcp-interval-ms', '20',
        '--delay-ms', '90',
        '--jitter-ms', '7',
        '--reorder-percent', '11',
        '--reorder-delay-ms', '13',
        '--duplicate-percent', '17',
        '--loss-percent', '19',
        '--loss-recovery-ms', '23',
        '--seed', '29')
Assert-RouteCEqual 'Tcp' $routeC_tcp.Transport.ToString() '--transport tcp was not selected.'
Assert-RouteCEqual 20 $routeC_tcp.KcpIntervalMs 'KCP interval parsing mismatch.'
Assert-RouteCEqual 90 $routeC_tcp.LabOptions.Profile.BaseDelayMs 'TCP base delay was rewritten.'
Assert-RouteCEqual 7 $routeC_tcp.LabOptions.Profile.JitterMs 'TCP jitter was rewritten.'
Assert-RouteCEqual 11 $routeC_tcp.LabOptions.Profile.ApplicationReorderPercent 'TCP reorder was rewritten.'
Assert-RouteCEqual 17 $routeC_tcp.LabOptions.Profile.DuplicatePercent 'TCP duplicate was rewritten.'
Assert-RouteCEqual 19 $routeC_tcp.LabOptions.Profile.RecoveredLossPercent 'TCP recovered-loss was rewritten.'
Assert-RouteCEqual 23 $routeC_tcp.LabOptions.Profile.LossRecoveryDelayMs 'TCP recovery delay was rewritten.'

$routeC_kcp = Invoke-RouteCParse `
    $routeC_parseMethod `
    @('--transport', 'kcp')
Assert-RouteCEqual 'KcpUdp' $routeC_kcp.Transport.ToString() '--transport kcp was not selected.'
Assert-RouteCEqual $null $routeC_kcp.KcpStopFilePath `
    'Default KCP options unexpectedly enabled a stop file.'
Assert-RouteCEqual $null $routeC_kcp.KcpDiagnosticsOutputPath `
    'Default KCP options unexpectedly enabled diagnostics output.'

$routeC_kcpHarness = Invoke-RouteCParse `
    $routeC_parseMethod `
    @(
        '--transport', 'kcp',
        '--kcp-interval-ms', '5',
        '--kcp-stop-file', 'C:\temp\route-c.stop',
        '--kcp-diagnostics-output', 'C:\temp\route-c.json')
Assert-RouteCEqual 'C:\temp\route-c.stop' $routeC_kcpHarness.KcpStopFilePath `
    'KCP stop-file path was rewritten.'
Assert-RouteCEqual `
    'C:\temp\route-c.json' `
    $routeC_kcpHarness.KcpDiagnosticsOutputPath `
    'KCP diagnostics-output path was rewritten.'

Assert-RouteCParseRejected `
    $routeC_parseMethod `
    @('--transport', 'kcp', '--kcp-stop-file', 'C:\temp\route-c.stop') `
    '--kcp-stop-file and --kcp-diagnostics-output must be specified together'
Assert-RouteCParseRejected `
    $routeC_parseMethod `
    @(
        '--transport', 'tcp',
        '--kcp-stop-file', 'C:\temp\route-c.stop',
        '--kcp-diagnostics-output', 'C:\temp\route-c.json') `
    'KCP probe control options require --transport kcp'

$routeC_rawDefault = Invoke-RouteCParse `
    $routeC_parseMethod `
    @('--transport', 'raw-udp')
Assert-RouteCEqual `
    'RawUdp' `
    $routeC_rawDefault.Transport.ToString() `
    '--transport raw-udp was not selected.'
Assert-RouteCEqual `
    6 `
    $routeC_rawDefault.RawWindowSize `
    'Raw UDP default window must be 6.'

foreach ($routeC_window in @(1, 6, 16))
{
    $routeC_rawWindow = Invoke-RouteCParse `
        $routeC_parseMethod `
        @(
            '--raw-window', $routeC_window.ToString(),
            '--transport', 'raw-udp')
    Assert-RouteCEqual `
        'RawUdp' `
        $routeC_rawWindow.Transport.ToString() `
        "Raw UDP transport changed for window $routeC_window."
    Assert-RouteCEqual `
        $routeC_window `
        $routeC_rawWindow.RawWindowSize `
        "Raw UDP window $routeC_window was not preserved."
}

foreach ($routeC_invalidWindow in @('0', '17', 'not-an-integer'))
{
    Assert-RouteCParseRejected `
        $routeC_parseMethod `
        @('--transport', 'raw-udp', '--raw-window', $routeC_invalidWindow) `
        '--raw-window must be an integer from 1 through 16'
}

Assert-RouteCParseRejected `
    $routeC_parseMethod `
    @('--transport', 'tcp', '--raw-window', '6') `
    '--raw-window requires --transport raw-udp'
Assert-RouteCParseRejected `
    $routeC_parseMethod `
    @('--raw-window', '6', '--transport', 'kcp') `
    '--raw-window requires --transport raw-udp'

foreach ($routeC_interval in @(1, 5, 10, 20))
{
    $routeC_intervalOptions = Invoke-RouteCParse `
        $routeC_parseMethod `
        @('--transport', 'kcp', '--kcp-interval-ms', $routeC_interval.ToString())
    Assert-RouteCEqual `
        $routeC_interval `
        $routeC_intervalOptions.KcpIntervalMs `
        "Valid KCP interval $routeC_interval was not preserved."
}

Assert-RouteCParseRejected `
    $routeC_parseMethod `
    @('--transport', 'udp') `
    'Unknown transport'
foreach ($routeC_invalidInterval in @(0, 2, 4, 6, 19, 21))
{
    Assert-RouteCParseRejected `
        $routeC_parseMethod `
        @('--kcp-interval-ms', $routeC_invalidInterval.ToString()) `
        '--kcp-interval-ms must be one of 1, 5, 10, or 20'
}

$routeC_portBlocker = [System.Net.Sockets.Socket]::new(
    [System.Net.Sockets.AddressFamily]::InterNetwork,
    [System.Net.Sockets.SocketType]::Dgram,
    [System.Net.Sockets.ProtocolType]::Udp)
$routeC_process = $null
try
{
    $routeC_portBlocker.ExclusiveAddressUse = $true
    $routeC_portBlocker.Bind([System.Net.IPEndPoint]::new(
        [System.Net.IPAddress]::Any,
        8888))
    $routeC_startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $routeC_startInfo.FileName = $ServerPath
    $routeC_startInfo.Arguments = '--transport kcp --kcp-interval-ms 10'
    $routeC_startInfo.UseShellExecute = $false
    $routeC_startInfo.RedirectStandardOutput = $true
    $routeC_startInfo.RedirectStandardError = $true
    $routeC_startInfo.CreateNoWindow = $true
    $routeC_process = [System.Diagnostics.Process]::Start($routeC_startInfo)
    Assert-RouteCTrue `
        ($routeC_process.WaitForExit(2000)) `
        'KCP worker did not propagate its occupied-port failure.'
    $routeC_standardOutput = $routeC_process.StandardOutput.ReadToEnd()
    $routeC_standardError = $routeC_process.StandardError.ReadToEnd()
    $routeC_combined = $routeC_standardOutput + $routeC_standardError
    Assert-RouteCEqual 4 $routeC_process.ExitCode 'KCP worker failure must use its dedicated exit code.'
    Assert-RouteCTrue `
        ($routeC_combined.Contains('KCP UDP worker terminated unexpectedly')) `
        "KCP worker failure was not reported: $routeC_combined"
}
finally
{
    if ($routeC_process -and -not $routeC_process.HasExited)
    {
        $routeC_process.Kill()
        $routeC_process.WaitForExit(2000) | Out-Null
    }
    $routeC_portBlocker.Dispose()
}

Write-Output 'PASS: server transport options, legacy TCP profiles, and KCP worker entry are correct.'
