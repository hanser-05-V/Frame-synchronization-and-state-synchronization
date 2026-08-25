param(
    [string]$ServerPath = '',

    [ValidateSet(
        'All',
        'StopBeforeRun',
        'WaitForSecondClient',
        'DefinitiveJoin',
        'BackpressureDispose',
        'BroadcastAfterDispose',
        'StopAfterDispose')]
    [string]$Case = 'All'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ServerPath))
{
    $routeC_scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ServerPath = Join-Path $routeC_scriptDirectory 'NetworkServer.exe'
}
if (-not (Test-Path -LiteralPath $ServerPath -PathType Leaf))
{
    throw "Server assembly does not exist: $ServerPath"
}

Add-Type -TypeDefinition @'
using System;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

public static class RouteCTcpRelayLifecycleProbe
{
    public static Thread StartSleeper(int milliseconds)
    {
        var thread = new Thread(() => Thread.Sleep(milliseconds));
        thread.IsBackground = true;
        thread.Start();
        return thread;
    }

    public static string StopBeforeRun(string assemblyPath)
    {
        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        Type labOptionsType = assembly.GetType(
            "FrameSyncServer.NetworkLabOptions",
            true);
        Type serverType = assembly.GetType(
            "FrameSyncServer.TcpRelayServer",
            true);
        object labOptions = labOptionsType.GetMethod("Parse").Invoke(
            null,
            new object[] { new string[0] });
        object server = serverType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new Type[] { labOptionsType },
            null).Invoke(new object[] { labOptions });
        MethodInfo run = serverType.GetMethod("Run");
        MethodInfo requestStop = serverType.GetMethod(
            "RequestStop",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo dispose = serverType.GetMethod("Dispose");
        if (requestStop == null)
        {
            dispose.Invoke(server, null);
            return "RequestStop seam is missing.";
        }

        Exception runException = null;
        requestStop.Invoke(server, null);
        var runThread = new Thread(() =>
        {
            try
            {
                run.Invoke(server, null);
            }
            catch (TargetInvocationException exception)
            {
                runException = exception.InnerException ?? exception;
            }
        });
        runThread.IsBackground = true;
        runThread.Start();
        try
        {
            if (!runThread.Join(500))
            {
                return "Run discarded a stop request recorded before startup.";
            }
            if (runException != null)
            {
                return "Stopped startup surfaced an exception: " +
                    runException.GetType().FullName + ": " +
                    runException.Message;
            }
            return "PASS";
        }
        finally
        {
            requestStop.Invoke(server, null);
            runThread.Join(2000);
            dispose.Invoke(server, null);
        }
    }

    public static string StopAfterDispose(string assemblyPath)
    {
        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        Type labOptionsType = assembly.GetType(
            "FrameSyncServer.NetworkLabOptions",
            true);
        Type serverType = assembly.GetType(
            "FrameSyncServer.TcpRelayServer",
            true);
        object labOptions = labOptionsType.GetMethod("Parse").Invoke(
            null,
            new object[] { new string[0] });
        object server = serverType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new Type[] { labOptionsType },
            null).Invoke(new object[] { labOptions });
        MethodInfo requestStop = serverType.GetMethod(
            "RequestStop",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo dispose = serverType.GetMethod("Dispose");
        dispose.Invoke(server, null);
        try
        {
            requestStop.Invoke(server, null);
            return "PASS";
        }
        catch (TargetInvocationException exception)
        {
            Exception inner = exception.InnerException ?? exception;
            return "A late stop touched disposed resources: " +
                inner.GetType().FullName + ": " + inner.Message;
        }
    }

    public static string BroadcastAfterDispose(string assemblyPath)
    {
        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        Type labOptionsType = assembly.GetType(
            "FrameSyncServer.NetworkLabOptions",
            true);
        Type serverType = assembly.GetType(
            "FrameSyncServer.TcpRelayServer",
            true);
        object labOptions = labOptionsType.GetMethod("Parse").Invoke(
            null,
            new object[] { new string[0] });
        object server = serverType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new Type[] { labOptionsType },
            null).Invoke(new object[] { labOptions });
        MethodInfo dispose = serverType.GetMethod("Dispose");
        MethodInfo broadcast = serverType.GetMethod(
            "Broadcast",
            BindingFlags.Instance | BindingFlags.NonPublic);

        dispose.Invoke(server, null);
        try
        {
            broadcast.Invoke(server, new object[] { (uint)1, 0, 1 });
        }
        catch (TargetInvocationException exception)
        {
            Exception cause = exception.InnerException ?? exception;
            return "A delivery that crossed shutdown crashed Broadcast: " +
                cause.GetType().FullName + ": " + cause.Message;
        }

        return "PASS";
    }

    public static string DisposeUnderBackpressure(string assemblyPath)
    {
        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        Type labOptionsType = assembly.GetType(
            "FrameSyncServer.NetworkLabOptions",
            true);
        Type serverType = assembly.GetType(
            "FrameSyncServer.TcpRelayServer",
            true);
        object labOptions = labOptionsType.GetMethod("Parse").Invoke(
            null,
            new object[] { new string[0] });
        object server = serverType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new Type[] { labOptionsType },
            null).Invoke(new object[] { labOptions });
        MethodInfo run = serverType.GetMethod("Run");
        MethodInfo requestStop = serverType.GetMethod(
            "RequestStop",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo dispose = serverType.GetMethod("Dispose");
        Exception runException = null;
        Exception disposeException = null;
        var runThread = new Thread(() =>
        {
            try
            {
                run.Invoke(server, null);
            }
            catch (TargetInvocationException exception)
            {
                runException = exception.InnerException ?? exception;
            }
        });
        runThread.IsBackground = true;
        runThread.Start();

        TcpClient client0 = null;
        TcpClient client1 = null;
        Thread sender = null;
        int stopSender = 0;
        Thread disposeThread = null;
        try
        {
            client0 = ConnectWithRetry();
            client1 = ConnectWithRetry();
            client1.ReceiveBufferSize = 1024;
            NetworkStream stream0 = client0.GetStream();
            NetworkStream stream1 = client1.GetStream();
            stream0.ReadTimeout = 2000;
            stream1.ReadTimeout = 2000;
            if (stream0.ReadByte() != 0 || stream1.ReadByte() != 1)
            {
                return "TCP player barrier did not complete.";
            }

            var clients = (System.Collections.IList)serverType.GetField(
                "_clients",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
            var serverPeer = (TcpClient)clients[1];
            serverPeer.SendBufferSize = 1024;
            byte[] packet = new byte[8];
            sender = new Thread(() =>
            {
                try
                {
                    while (Volatile.Read(ref stopSender) == 0)
                    {
                        stream0.Write(packet, 0, packet.Length);
                    }
                }
                catch (Exception)
                {
                }
            });
            sender.IsBackground = true;
            sender.Start();

            Thread.Sleep(1500);

            requestStop.Invoke(server, null);
            if (!runThread.Join(2000))
            {
                return "Run did not return after stop under backpressure.";
            }
            disposeThread = new Thread(() =>
            {
                try
                {
                    dispose.Invoke(server, null);
                }
                catch (TargetInvocationException exception)
                {
                    disposeException = exception.InnerException ?? exception;
                }
            });
            disposeThread.IsBackground = true;
            disposeThread.Start();
            if (!disposeThread.Join(2000))
            {
                return "Dispose deadlocked behind a backpressured broadcast.";
            }
            if (disposeException != null)
            {
                return "Dispose failed under backpressure: " +
                    disposeException.GetType().FullName + ": " +
                    disposeException.Message;
            }
            if (runException != null)
            {
                return "Run failed under backpressure: " +
                    runException.GetType().FullName + ": " +
                    runException.Message;
            }
            return "PASS";
        }
        finally
        {
            Interlocked.Exchange(ref stopSender, 1);
            if (client1 != null)
            {
                client1.Close();
            }
            if (client0 != null)
            {
                client0.Close();
            }
            requestStop.Invoke(server, null);
            if (sender != null)
            {
                sender.Join(2000);
            }
            runThread.Join(2000);
            if (disposeThread != null)
            {
                disposeThread.Join(2000);
            }
            dispose.Invoke(server, null);
        }
    }

    private static TcpClient ConnectWithRetry()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var candidate = new TcpClient();
            try
            {
                candidate.Connect("127.0.0.1", 8888);
                return candidate;
            }
            catch (SocketException)
            {
                candidate.Close();
                Thread.Sleep(25);
            }
        }
        throw new InvalidOperationException("TCP relay did not start listening.");
    }

    public static string StopWhileWaitingForSecondClient(string assemblyPath)
    {
        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        Type labOptionsType = assembly.GetType(
            "FrameSyncServer.NetworkLabOptions",
            true);
        Type serverType = assembly.GetType(
            "FrameSyncServer.TcpRelayServer",
            true);
        MethodInfo parse = labOptionsType.GetMethod("Parse");
        object labOptions = parse.Invoke(
            null,
            new object[] { new string[0] });
        ConstructorInfo constructor = serverType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new Type[] { labOptionsType },
            null);
        object server = constructor.Invoke(new object[] { labOptions });
        MethodInfo run = serverType.GetMethod("Run");
        MethodInfo requestStop = serverType.GetMethod(
            "RequestStop",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo dispose = serverType.GetMethod("Dispose");
        if (requestStop == null)
        {
            dispose.Invoke(server, null);
            return "RequestStop seam is missing.";
        }

        Exception runException = null;
        var runThread = new Thread(() =>
        {
            try
            {
                run.Invoke(server, null);
            }
            catch (TargetInvocationException exception)
            {
                runException = exception.InnerException ?? exception;
            }
            catch (Exception exception)
            {
                runException = exception;
            }
        });
        runThread.IsBackground = true;
        runThread.Start();

        TcpClient firstClient = null;
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                var candidate = new TcpClient();
                try
                {
                    candidate.Connect("127.0.0.1", 8888);
                    firstClient = candidate;
                    break;
                }
                catch (SocketException)
                {
                    candidate.Close();
                    Thread.Sleep(25);
                }
            }

            if (firstClient == null)
            {
                return "TCP relay did not start listening.";
            }

            requestStop.Invoke(server, null);
            if (!runThread.Join(2000))
            {
                return "RequestStop did not unblock AcceptTcpClient.";
            }
            if (runException != null)
            {
                return "Run surfaced an exception during expected stop: " +
                    runException.GetType().FullName + ": " +
                    runException.Message;
            }

            return "PASS";
        }
        finally
        {
            if (firstClient != null)
            {
                firstClient.Close();
            }
            requestStop.Invoke(server, null);
            runThread.Join(2000);
            dispose.Invoke(server, null);
        }
    }
}
'@

if ($Case -eq 'All' -or $Case -eq 'StopBeforeRun')
{
    $routeC_result = [RouteCTcpRelayLifecycleProbe]::StopBeforeRun($ServerPath)
    if ($routeC_result -ne 'PASS')
    {
        throw $routeC_result
    }
}

if ($Case -eq 'All' -or $Case -eq 'WaitForSecondClient')
{
    $routeC_result = [RouteCTcpRelayLifecycleProbe]::StopWhileWaitingForSecondClient(
        $ServerPath)
    if ($routeC_result -ne 'PASS')
    {
        throw $routeC_result
    }
}

if ($Case -eq 'All' -or $Case -eq 'DefinitiveJoin')
{
    $routeC_assembly = [Reflection.Assembly]::LoadFrom($ServerPath)
    $routeC_serverType = $routeC_assembly.GetType(
        'FrameSyncServer.TcpRelayServer',
        $true)
    $routeC_joinMethod = $routeC_serverType.GetMethod(
        'JoinThread',
        [Reflection.BindingFlags]'Static, NonPublic')
    $routeC_sleeper = [RouteCTcpRelayLifecycleProbe]::StartSleeper(1250)
    $routeC_joinMethod.Invoke($null, @($routeC_sleeper)) | Out-Null
    if ($routeC_sleeper.IsAlive)
    {
        $routeC_sleeper.Join(2000) | Out-Null
        throw 'JoinThread returned while the owned worker was still alive.'
    }
}

if ($Case -eq 'All' -or $Case -eq 'BackpressureDispose')
{
    $routeC_result = [RouteCTcpRelayLifecycleProbe]::DisposeUnderBackpressure(
        $ServerPath)
    if ($routeC_result -ne 'PASS')
    {
        throw $routeC_result
    }
}

if ($Case -eq 'All' -or $Case -eq 'StopAfterDispose')
{
    $routeC_result = [RouteCTcpRelayLifecycleProbe]::StopAfterDispose($ServerPath)
    if ($routeC_result -ne 'PASS')
    {
        throw $routeC_result
    }
}

if ($Case -eq 'All' -or $Case -eq 'BroadcastAfterDispose')
{
    $routeC_result = [RouteCTcpRelayLifecycleProbe]::BroadcastAfterDispose(
        $ServerPath)
    if ($routeC_result -ne 'PASS')
    {
        throw $routeC_result
    }
}

Write-Output 'PASS: TCP relay startup cancellation and worker joins release all owned resources.'
