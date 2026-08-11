# Remote Presentation Buffer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add one fixed logic-frame of presentation buffering to remote players and non-local basketball states while preserving the current local-player display path and rollback smoother.

**Architecture:** Deepen `PresentationFrameInterpolator` into one fixed-allocation three-endpoint module. At logic head `N` it evaluates the local player from `N-1 → N`, the remote player and detached basketball from `N-2 → N-1`, emits a presentation-only attachment sample for Held basketball, and atomically replaces corrected history after rollback.

**Tech Stack:** Unity 2022.3.62f2, C# 9, UnityEngine `Vector3`, NUnit EditMode tests, Unity Roslyn response files.

**Approved specification:** `docs/superpowers/specs/2026-08-07-remote-presentation-buffer-design.md`

**Repository constraints:** Work only in `E:/帧同步_RouteC` and preserve all existing P0–P1-D changes. Do not modify `E:/帧同步`. Do not commit, merge, push, reset, clean, or overwrite with checkout. Commit steps are omitted because the maintainer publishes Git changes manually. Do not control the Unity UI.

---

## File map

- Create `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationBallSample.cs` — immutable basketball base position and attachment key.
- Modify `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs` — three endpoints, mixed timelines, basketball policy, rollback replacement.
- Modify `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationTargetResolver.cs` — resolve buffered ball targets and pure attachment decisions.
- Modify `Project/Frame Synchronization/Assets/Scripts/GameController.cs` — consume the mixed output and replace corrected history after replay.
- Modify `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs` — timing, ball state, rollback, allocation, and isolation.
- Modify `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationTargetResolverTests.cs` — target resolution and attachment changes.

Do not modify simulation, prediction, replay, snapshot, WorldHash, network, or server files.

## Reusable compile commands

Run from:

~~~powershell
Set-Location -LiteralPath 'E:/帧同步_RouteC/Project/Frame Synchronization'
~~~

Runtime:

~~~powershell
& 'C:/Program Files/dotnet/dotnet.exe' 'C:/Unity/unity2022/Editor/Data/DotNetSdkRoslyn/csc.dll' '@Library/Bee/artifacts/1900b0aE.dag/FrameSyncDemo.Runtime.rsp' 'Assets/Scripts/Presentation/PresentationBallSample.cs'
~~~

EditMode:

~~~powershell
& 'C:/Program Files/dotnet/dotnet.exe' 'C:/Unity/unity2022/Editor/Data/DotNetSdkRoslyn/csc.dll' '@Library/Bee/artifacts/1900b0aE.dag/FrameSyncDemo.Tests.EditMode.rsp'
~~~

If Unity regenerates the runtime response file and already lists `PresentationBallSample.cs`, omit the extra source argument. GREEN means exit code 0 and zero C# errors.

### Task 1: Establish a focused RED/GREEN harness

**Files:**
- Create temporarily: `C:/tmp/RouteC-P1E-Runner/RouteCFocusedTestRunner.cs`
- Generate temporarily: `C:/tmp/RouteC-P1E-Runner/RouteCFocusedTestRunner.exe`

- [ ] **Step 1: Reconfirm repository guards**

Run:

~~~powershell
$env:GIT_OPTIONAL_LOCKS = '0'
git -C 'E:/帧同步_RouteC' branch --show-current
git -C 'E:/帧同步_RouteC' rev-parse HEAD
git -C 'E:/帧同步_RouteC' -c core.quotepath=false status --short
git -C 'E:/帧同步' branch --show-current
git -C 'E:/帧同步' rev-parse HEAD
~~~

Expected Route C is `delivery/route-c` at `37260b437260c7712c658d5d0e05cbdd183accfb`; the original is `main` at `346b9ed235523ee3cd5fa9bb55d7f405a12cb1a7`. Record every Route C status line as the preservation baseline.

- [ ] **Step 2: Create the temporary runner**

Create `RouteCFocusedTestRunner.cs`:

~~~csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

internal static class RouteCFocusedTestRunner
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
            return 2;

        Assembly assembly = Assembly.LoadFrom(args[0]);
        var requested = new HashSet<string>(
            args.Skip(1),
            StringComparer.Ordinal);
        int passed = 0;
        int failed = 0;
        int skipped = 0;

        foreach (Type fixtureType in assembly.GetTypes()
            .Where(type => requested.Contains(type.FullName)))
        {
            MethodInfo[] setups = fixtureType.GetMethods()
                .Where(method => method
                    .GetCustomAttributes(typeof(SetUpAttribute), true)
                    .Length > 0)
                .ToArray();
            MethodInfo[] teardowns = fixtureType.GetMethods()
                .Where(method => method
                    .GetCustomAttributes(typeof(TearDownAttribute), true)
                    .Length > 0)
                .ToArray();

            foreach (MethodInfo method in fixtureType.GetMethods())
            {
                if (method.GetCustomAttributes(
                    typeof(IgnoreAttribute),
                    true).Length > 0)
                {
                    continue;
                }

                TestCaseAttribute[] cases = method.GetCustomAttributes(
                        typeof(TestCaseAttribute),
                        true)
                    .Cast<TestCaseAttribute>()
                    .ToArray();
                bool isTest = method.GetCustomAttributes(
                    typeof(TestAttribute),
                    true).Length > 0;
                if (!isTest && cases.Length == 0)
                    continue;

                if (cases.Length == 0)
                {
                    RunCase(
                        fixtureType,
                        method,
                        Array.Empty<object>(),
                        setups,
                        teardowns,
                        ref passed,
                        ref failed,
                        ref skipped);
                }
                else
                {
                    foreach (TestCaseAttribute testCase in cases)
                    {
                        RunCase(
                            fixtureType,
                            method,
                            testCase.Arguments,
                            setups,
                            teardowns,
                            ref passed,
                            ref failed,
                            ref skipped);
                    }
                }
            }
        }

        Console.WriteLine(
            "Focused tests: " + passed + " passed, " +
            failed + " failed, " + skipped + " skipped");
        return failed == 0 ? 0 : 1;
    }

    private static void RunCase(
        Type fixtureType,
        MethodInfo method,
        object[] arguments,
        MethodInfo[] setups,
        MethodInfo[] teardowns,
        ref int passed,
        ref int failed,
        ref int skipped)
    {
        object fixture = Activator.CreateInstance(fixtureType);
        string name = fixtureType.FullName + "." + method.Name;
        try
        {
            foreach (MethodInfo setup in setups)
                setup.Invoke(fixture, Array.Empty<object>());

            method.Invoke(fixture, arguments);
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (TargetInvocationException exception)
        {
            Exception cause = exception.InnerException ?? exception;
            string details = cause.ToString();
            if (details.Contains("get_unityLogger") ||
                details.Contains("can only be called from the main thread") ||
                details.Contains("internal call"))
            {
                skipped++;
                Console.WriteLine("SKIP " + name + ": Unity native runtime unavailable");
            }
            else
            {
                failed++;
                Console.Error.WriteLine(
                    "FAIL " + name + ": " + cause.Message);
            }
        }
        finally
        {
            foreach (MethodInfo teardown in teardowns)
                teardown.Invoke(fixture, Array.Empty<object>());
        }
    }
}
~~~

- [ ] **Step 3: Compile and run the baseline**

Run:

~~~powershell
$runnerRoot = 'C:/tmp/RouteC-P1E-Runner'
$projectRoot = 'E:/帧同步_RouteC/Project/Frame Synchronization'
$nunit = Join-Path $projectRoot 'Library/PackageCache/com.unity.ext.nunit@1.0.6/net35/unity-custom/nunit.framework.dll'
New-Item -ItemType Directory -Path $runnerRoot -Force | Out-Null
& 'C:/Program Files/dotnet/dotnet.exe' 'C:/Unity/unity2022/Editor/Data/DotNetSdkRoslyn/csc.dll' -nologo -target:exe -out:"$runnerRoot/RouteCFocusedTestRunner.exe" -r:"$nunit" "$runnerRoot/RouteCFocusedTestRunner.cs"
$artifactRoot = Join-Path $projectRoot 'Library/Bee/artifacts/1900b0aE.dag'
$env:MONO_PATH = [string]::Join(';', @($artifactRoot, (Join-Path $projectRoot 'Library/PackageCache/com.unity.ext.nunit@1.0.6/net35/unity-custom'), 'C:/Unity/unity2022/Editor/Data/Managed', 'C:/Unity/unity2022/Editor/Data/Managed/UnityEngine'))
& 'C:/Unity/unity2022/Editor/Data/MonoBleedingEdge/bin/mono.exe' "$runnerRoot/RouteCFocusedTestRunner.exe" "$artifactRoot/FrameSyncDemo.Tests.EditMode.dll" 'FrameSyncDemo.Tests.PresentationFrameInterpolatorTests' 'FrameSyncDemo.Tests.PresentationTargetResolverTests' 'FrameSyncDemo.Tests.PresentationCorrectionSmootherTests'
~~~

Expected: runner compilation succeeds and every existing presentation case passes.

### Task 2: Add PresentationBallSample and its resolver seam

**Files:**
- Create: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationBallSample.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationTargetResolver.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationTargetResolverTests.cs`

- [ ] **Step 1: Write failing tests**

Add:

~~~csharp
[Test]
public void ResolveBufferedBallTarget_Unattached_ReturnsBase()
{
    var sample = new PresentationBallSample(
        new Vector3(1f, 2f, 3f),
        -1);

    Vector3 result = PresentationTargetResolver.ResolveBufferedBallTarget(
        sample,
        new[] { Vector3.zero, Vector3.one },
        new[] { Vector3.one * 2f, Vector3.one * 3f });

    Assert.AreEqual(sample.BasePosition, result);
}

[Test]
public void ResolveBufferedBallTarget_Attached_InheritsHolderCorrection()
{
    var sample = new PresentationBallSample(
        new Vector3(4f, 1.3f, 0.8f),
        1);
    Vector3[] bases =
    {
        new Vector3(-3f, 0.5f, 0f),
        new Vector3(3f, 0.5f, 0f)
    };
    Vector3[] displays =
    {
        new Vector3(-3f, 0.5f, 0f),
        new Vector3(2.25f, 0.5f, 0f)
    };

    Vector3 result = PresentationTargetResolver.ResolveBufferedBallTarget(
        sample,
        bases,
        displays);

    Assert.AreEqual(new Vector3(3.25f, 1.3f, 0.8f), result);
}

[Test]
public void ResolveBufferedBallTarget_InvalidAttachment_ReturnsBase()
{
    var sample = new PresentationBallSample(
        new Vector3(1f, 2f, 3f),
        2);

    Assert.AreEqual(
        sample.BasePosition,
        PresentationTargetResolver.ResolveBufferedBallTarget(
            sample,
            new Vector3[2],
            new Vector3[2]));
    Assert.AreEqual(
        sample.BasePosition,
        PresentationTargetResolver.ResolveBufferedBallTarget(
            sample,
            null,
            null));
}
~~~

- [ ] **Step 2: Compile and verify RED**

Expected: only `PresentationBallSample` and `ResolveBufferedBallTarget` are missing.

- [ ] **Step 3: Add the immutable sample**

Create:

~~~csharp
using UnityEngine;

namespace FrameSyncDemo
{
    /// <summary>
    /// 一次表现求值产生的篮球基准及表现附着键。
    /// </summary>
    public readonly struct PresentationBallSample
    {
        public PresentationBallSample(
            Vector3 basePosition,
            int attachedPlayerIndex)
        {
            BasePosition = basePosition;
            AttachedPlayerIndex = attachedPlayerIndex;
        }

        public Vector3 BasePosition { get; }
        public int AttachedPlayerIndex { get; }
        public bool IsAttached => AttachedPlayerIndex >= 0;
    }
}
~~~

- [ ] **Step 4: Add the resolver**

Add without removing legacy methods:

~~~csharp
public static Vector3 ResolveBufferedBallTarget(
    PresentationBallSample sample,
    Vector3[] playerBasePositions,
    Vector3[] displayedPlayerPositions)
{
    if (!sample.IsAttached)
        return sample.BasePosition;

    int holderIndex = sample.AttachedPlayerIndex;
    if (playerBasePositions == null ||
        displayedPlayerPositions == null ||
        holderIndex >= playerBasePositions.Length ||
        holderIndex >= displayedPlayerPositions.Length)
    {
        return sample.BasePosition;
    }

    return sample.BasePosition +
        displayedPlayerPositions[holderIndex] -
        playerBasePositions[holderIndex];
}
~~~

- [ ] **Step 5: Compile and verify GREEN**

Run runtime/test compilation and the resolver fixture. All old and new cases must pass.

### Task 3: Expand player interpolation to three endpoints

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`

- [ ] **Step 1: Write failing mixed-timeline tests**

Add:

~~~csharp
[Test]
public void Evaluate_LocalZero_UsesCurrentPairAndRemoteUsesDelayedPair()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    players[0].position.x = FixedInt.FromInt(-2);
    players[1].position.x = FixedInt.FromInt(4);
    interpolator.PushLogicFrame(11, players, ball);
    players[0].position.x = FixedInt.FromInt(-1);
    players[1].position.x = FixedInt.FromInt(5);
    interpolator.PushLogicFrame(12, players, ball);

    interpolator.Evaluate(0, 0.5f, output, out _);

    Assert.AreEqual(new Vector3(-1.5f, 0.5f, 0f), output[0]);
    Assert.AreEqual(new Vector3(3.5f, 0.5f, 0f), output[1]);
    Assert.AreEqual(10, interpolator.OldestFrameID);
    Assert.AreEqual(11, interpolator.PreviousFrameID);
    Assert.AreEqual(12, interpolator.CurrentFrameID);
}

[Test]
public void Evaluate_LocalOne_ReversesTimelineOwnership()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    players[0].position.x = FixedInt.FromInt(-2);
    players[1].position.x = FixedInt.FromInt(4);
    interpolator.PushLogicFrame(11, players, ball);
    players[0].position.x = FixedInt.FromInt(-1);
    players[1].position.x = FixedInt.FromInt(5);
    interpolator.PushLogicFrame(12, players, ball);

    interpolator.Evaluate(1, 0.5f, output, out _);

    Assert.AreEqual(new Vector3(-2.5f, 0.5f, 0f), output[0]);
    Assert.AreEqual(new Vector3(4.5f, 0.5f, 0f), output[1]);
}

[Test]
public void Evaluate_InvalidLocalPlayerIndex_Throws()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    interpolator.Reset(10, players, ball);

    Assert.Throws<ArgumentOutOfRangeException>(
        () => interpolator.Evaluate(-1, 0f, new Vector3[2], out _));
    Assert.Throws<ArgumentOutOfRangeException>(
        () => interpolator.Evaluate(2, 0f, new Vector3[2], out _));
}

[Test]
public void Reset_AfterThreeFrames_CollapsesEveryTimelineToResetWorld()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    players[0].position.x = FixedInt.FromInt(-2);
    interpolator.PushLogicFrame(11, players, ball);
    players[0].position.x = FixedInt.FromInt(-1);
    interpolator.PushLogicFrame(12, players, ball);
    players[0].position.x = FixedInt.FromInt(7);

    interpolator.Reset(20, players, ball);
    interpolator.Evaluate(0, 0.5f, output, out _);

    Assert.AreEqual(new Vector3(7f, 0.5f, 0f), output[0]);
    Assert.AreEqual(20, interpolator.OldestFrameID);
    Assert.AreEqual(20, interpolator.PreviousFrameID);
    Assert.AreEqual(20, interpolator.CurrentFrameID);
}
~~~

- [ ] **Step 2: Compile and verify RED**

Expected: the mixed Evaluate overload and `OldestFrameID` are missing.

- [ ] **Step 3: Add and shift the oldest endpoint**

Add:

~~~csharp
private readonly Vector3[] _oldestPlayerPositions;
private Vector3 _oldestBallPosition;

public int OldestFrameID { get; private set; }
~~~

Allocate `_oldestPlayerPositions` in the constructor. Replace Reset:

~~~csharp
public void Reset(
    int frameID,
    PlayerEntity[] players,
    BallEntity ball)
{
    ValidateWorld(players, ball);
    CaptureCurrent(players, ball);
    CopyCurrentToPrevious();
    CopyPreviousToOldest();
    OldestFrameID = frameID;
    PreviousFrameID = frameID;
    CurrentFrameID = frameID;
    IsReady = true;
}
~~~

After normal push validation, shift in this order:

~~~csharp
CopyPreviousToOldest();
OldestFrameID = PreviousFrameID;
CopyCurrentToPrevious();
PreviousFrameID = CurrentFrameID;
CaptureCurrent(players, ball);
CurrentFrameID = frameID;
~~~

Add:

~~~csharp
private void CopyPreviousToOldest()
{
    for (int i = 0; i < _playerCount; i++)
        _oldestPlayerPositions[i] = _previousPlayerPositions[i];

    _oldestBallPosition = _previousBallPosition;
}
~~~

- [ ] **Step 4: Add mixed Evaluate**

Add:

~~~csharp
public void Evaluate(
    int localPlayerIndex,
    float alpha,
    Vector3[] playerPositions,
    out PresentationBallSample ballSample)
{
    ValidateMixedEvaluation(localPlayerIndex, playerPositions);
    float safeAlpha = NormalizeAlpha(alpha);
    for (int i = 0; i < _playerCount; i++)
    {
        Vector3 from = i == localPlayerIndex
            ? _previousPlayerPositions[i]
            : _oldestPlayerPositions[i];
        Vector3 to = i == localPlayerIndex
            ? _currentPlayerPositions[i]
            : _previousPlayerPositions[i];
        playerPositions[i] = Vector3.LerpUnclamped(
            from,
            to,
            safeAlpha);
    }

    ballSample = new PresentationBallSample(
        Vector3.LerpUnclamped(
            _oldestBallPosition,
            _previousBallPosition,
            safeAlpha),
        -1);
}

private void ValidateMixedEvaluation(
    int localPlayerIndex,
    Vector3[] playerPositions)
{
    if (!IsReady)
        throw new InvalidOperationException("表现帧插值器尚未初始化。");
    if (localPlayerIndex < 0 || localPlayerIndex >= _playerCount)
        throw new ArgumentOutOfRangeException(nameof(localPlayerIndex));
    if (playerPositions == null)
        throw new ArgumentNullException(nameof(playerPositions));
    if (playerPositions.Length != _playerCount)
    {
        throw new ArgumentException(
            "输出球员数组长度必须与构造时的球员数量一致。",
            nameof(playerPositions));
    }
}
~~~

Keep the legacy two-endpoint Evaluate overload unchanged.

- [ ] **Step 5: Compile and verify GREEN**

Run the interpolator fixture. All old and mixed-player cases pass.

### Task 4: Make basketball routing endpoint-consistent

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`

- [ ] **Step 1: Write failing ball-policy tests**

Add:

~~~csharp
[Test]
public void Evaluate_LocalHeld_UsesLocalHolderTimeline()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    players[0].position.x = FixedInt.FromInt(-2);
    SetHeldBall(players, ball, 0, new Vector3(0.4f, 0.8f, 0.6f));
    interpolator.PushLogicFrame(11, players, ball);
    players[0].position.x = FixedInt.FromInt(-1);
    SetHeldBall(players, ball, 0, new Vector3(0.4f, 0.8f, 0.6f));
    interpolator.PushLogicFrame(12, players, ball);

    interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

    Assert.AreEqual(0, sample.AttachedPlayerIndex);
    Assert.AreEqual(
        output[0] + new Vector3(0.4f, 0.8f, 0.6f),
        sample.BasePosition);
}

[TestCase(BallEntity.EState.Free)]
[TestCase(BallEntity.EState.Airborne)]
[TestCase(BallEntity.EState.Scored)]
public void Evaluate_NonHeldBall_UsesDelayedWorld(
    BallEntity.EState state)
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    ball.state = state;
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    ball.position.x = FixedInt.FromInt(2);
    interpolator.PushLogicFrame(11, players, ball);
    ball.position.x = FixedInt.FromInt(100);
    interpolator.PushLogicFrame(12, players, ball);

    interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

    Assert.AreEqual(-1, sample.AttachedPlayerIndex);
    Assert.AreEqual(new Vector3(1f, 0f, 0f), sample.BasePosition);
}

[Test]
public void Evaluate_RemoteHeld_WaitsForDelayedOwnership()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    players[1].position.x = FixedInt.FromInt(4);
    ball.position.x = FixedInt.FromInt(1);
    interpolator.PushLogicFrame(11, players, ball);
    players[1].position.x = FixedInt.FromInt(5);
    SetHeldBall(players, ball, 1, new Vector3(-0.4f, 0.8f, 0.6f));
    interpolator.PushLogicFrame(12, players, ball);
    interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample early);
    Assert.AreEqual(-1, early.AttachedPlayerIndex);

    players[1].position.x = FixedInt.FromInt(6);
    SetHeldBall(players, ball, 1, new Vector3(-0.4f, 0.8f, 0.6f));
    interpolator.PushLogicFrame(13, players, ball);
    interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample delayed);

    Assert.AreEqual(1, delayed.AttachedPlayerIndex);
    Assert.AreEqual(
        output[1] + new Vector3(-0.4f, 0.8f, 0.6f),
        delayed.BasePosition);
}

[Test]
public void Evaluate_LocalRelease_DoesNotUseStaleLocalAttachment()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    SetHeldBall(players, ball, 0, new Vector3(0.4f, 0.8f, 0.6f));
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    players[0].position.x = FixedInt.FromInt(-2);
    SetHeldBall(players, ball, 0, new Vector3(0.4f, 0.8f, 0.6f));
    interpolator.PushLogicFrame(11, players, ball);
    ball.state = BallEntity.EState.Airborne;
    ball.holderPlayerIndex = -1;
    ball.position.x = FixedInt.FromInt(8);
    interpolator.PushLogicFrame(12, players, ball);

    interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

    Assert.AreEqual(-1, sample.AttachedPlayerIndex);
}

[Test]
public void Evaluate_RemoteRelease_WaitsForDelayedEndpoint()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    SetHeldBall(players, ball, 1, new Vector3(-0.4f, 0.8f, 0.6f));
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    players[1].position.x = FixedInt.FromInt(4);
    SetHeldBall(players, ball, 1, new Vector3(-0.4f, 0.8f, 0.6f));
    interpolator.PushLogicFrame(11, players, ball);
    ball.state = BallEntity.EState.Airborne;
    ball.holderPlayerIndex = -1;
    ball.position.x = FixedInt.FromInt(8);
    interpolator.PushLogicFrame(12, players, ball);

    interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample delayedHeld);
    Assert.AreEqual(1, delayedHeld.AttachedPlayerIndex);

    ball.position.x = FixedInt.FromInt(9);
    interpolator.PushLogicFrame(13, players, ball);
    interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample released);

    Assert.AreEqual(-1, released.AttachedPlayerIndex);
}
~~~

Add:

~~~csharp
private static void SetHeldBall(
    PlayerEntity[] players,
    BallEntity ball,
    int holderIndex,
    Vector3 heldOffset)
{
    Vector3 holderBase =
        PresentationTargetResolver.ResolvePlayerTarget(
            players[holderIndex]);
    Vector3 position = holderBase + heldOffset;
    ball.position = new FixedVector3(
        FixedInt.FromFloat(position.x),
        FixedInt.FromFloat(position.y),
        FixedInt.FromFloat(position.z));
    ball.velocity = FixedVector3.Zero;
    ball.state = BallEntity.EState.Held;
    ball.holderPlayerIndex = holderIndex;
}
~~~

- [ ] **Step 2: Run and verify RED**

Held cases fail because Task 3 emits an unattached delayed sample.

- [ ] **Step 3: Capture full ball metadata**

Add:

~~~csharp
private struct BallEndpoint
{
    public Vector3 Position;
    public BallEntity.EState State;
    public int HolderPlayerIndex;
    public Vector3 HeldOffset;
}

private BallEndpoint _oldestBall;
private BallEndpoint _previousBall;
private BallEndpoint _currentBall;
~~~

Replace ball capture inside `CaptureCurrent`:

~~~csharp
Vector3 ballPosition = ball.position.ToVector3();
int holderIndex = ball.holderPlayerIndex;
Vector3 heldOffset = Vector3.zero;
if (ball.state == BallEntity.EState.Held &&
    holderIndex >= 0 &&
    holderIndex < _playerCount)
{
    heldOffset =
        ballPosition - _currentPlayerPositions[holderIndex];
}

_currentBall = new BallEndpoint
{
    Position = ballPosition,
    State = ball.state,
    HolderPlayerIndex = holderIndex,
    HeldOffset = heldOffset
};
~~~

Update copy helpers to assign `_previousBall = _currentBall` and `_oldestBall = _previousBall`. Update legacy Evaluate to interpolate `BallEndpoint.Position`.

Use these complete helpers:

~~~csharp
private void CopyCurrentToPrevious()
{
    for (int i = 0; i < _playerCount; i++)
        _previousPlayerPositions[i] = _currentPlayerPositions[i];

    _previousBall = _currentBall;
}

private void CopyPreviousToOldest()
{
    for (int i = 0; i < _playerCount; i++)
        _oldestPlayerPositions[i] = _previousPlayerPositions[i];

    _oldestBall = _previousBall;
}
~~~

The legacy ball interpolation becomes:

~~~csharp
ballPosition = Vector3.LerpUnclamped(
    _previousBall.Position,
    _currentBall.Position,
    safeAlpha);
~~~

- [ ] **Step 4: Implement the approved policy**

The mixed Evaluate assigns:

~~~csharp
ballSample = EvaluateBallSample(
    localPlayerIndex,
    safeAlpha,
    playerPositions);
~~~

Add:

~~~csharp
private PresentationBallSample EvaluateBallSample(
    int localPlayerIndex,
    float alpha,
    Vector3[] playerPositions)
{
    if (IsHeldBy(_currentBall, localPlayerIndex))
    {
        Vector3 offset = InterpolateHeldOffset(
            _previousBall,
            _currentBall,
            localPlayerIndex,
            alpha);
        return new PresentationBallSample(
            playerPositions[localPlayerIndex] + offset,
            localPlayerIndex);
    }

    int remoteHolder = _previousBall.HolderPlayerIndex;
    if (remoteHolder != localPlayerIndex &&
        remoteHolder >= 0 &&
        remoteHolder < _playerCount &&
        IsHeldBy(_previousBall, remoteHolder))
    {
        Vector3 offset = InterpolateHeldOffset(
            _oldestBall,
            _previousBall,
            remoteHolder,
            alpha);
        return new PresentationBallSample(
            playerPositions[remoteHolder] + offset,
            remoteHolder);
    }

    return new PresentationBallSample(
        Vector3.LerpUnclamped(
            _oldestBall.Position,
            _previousBall.Position,
            alpha),
        -1);
}

private static bool IsHeldBy(
    BallEndpoint endpoint,
    int playerIndex)
{
    return playerIndex >= 0 &&
        endpoint.State == BallEntity.EState.Held &&
        endpoint.HolderPlayerIndex == playerIndex;
}

private static Vector3 InterpolateHeldOffset(
    BallEndpoint from,
    BallEndpoint to,
    int holderIndex,
    float alpha)
{
    return IsHeldBy(from, holderIndex)
        ? Vector3.LerpUnclamped(
            from.HeldOffset,
            to.HeldOffset,
            alpha)
        : to.HeldOffset;
}
~~~

- [ ] **Step 5: Compile and verify GREEN**

Run interpolator and resolver fixtures. All new policy and legacy cases pass.

### Task 5: Atomically replace corrected rollback history

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationFrameInterpolator.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`

- [ ] **Step 1: Write failing replacement tests**

Add:

~~~csharp
[Test]
public void ReplaceHistoryAfterRollback_ReplacesEveryPredictedEndpoint()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(100, players, ball);
    players[0].position.x = FixedInt.FromInt(101);
    players[1].position.x = FixedInt.FromInt(101);
    interpolator.PushLogicFrame(101, players, ball);
    players[0].position.x = FixedInt.FromInt(102);
    players[1].position.x = FixedInt.FromInt(102);
    interpolator.PushLogicFrame(102, players, ball);

    FrameSnapshot oldest = CreateSnapshot(10, -3, 3, 0);
    FrameSnapshot previous = CreateSnapshot(11, -2, 4, 2);
    FrameSnapshot newest = CreateSnapshot(12, -1, 5, 4);
    interpolator.ReplaceHistoryAfterRollback(
        newest,
        previous,
        oldest);
    interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

    Assert.AreEqual(new Vector3(-1.5f, 0.5f, 0f), output[0]);
    Assert.AreEqual(new Vector3(3.5f, 0.5f, 0f), output[1]);
    Assert.AreEqual(new Vector3(1f, 0f, 0f), sample.BasePosition);
    Assert.AreEqual(10, interpolator.OldestFrameID);
    Assert.AreEqual(11, interpolator.PreviousFrameID);
    Assert.AreEqual(12, interpolator.CurrentFrameID);
}

[Test]
public void ReplaceHistoryAfterRollback_MissingHistory_DuplicatesNewest()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    FrameSnapshot newest = CreateSnapshot(12, -1, 5, 4);

    interpolator.ReplaceHistoryAfterRollback(
        newest,
        null,
        null);
    interpolator.Evaluate(0, 0.5f, output, out PresentationBallSample sample);

    Assert.AreEqual(new Vector3(-1f, 0.5f, 0f), output[0]);
    Assert.AreEqual(new Vector3(5f, 0.5f, 0f), output[1]);
    Assert.AreEqual(new Vector3(4f, 0f, 0f), sample.BasePosition);
}

[Test]
public void ReplaceHistoryAfterRollback_NonConsecutive_Throws()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    interpolator.Reset(10, players, ball);

    Assert.Throws<ArgumentException>(
        () => interpolator.ReplaceHistoryAfterRollback(
            CreateSnapshot(12, -1, 5, 4),
            CreateSnapshot(10, -2, 4, 2),
            null));
}
~~~

Add:

~~~csharp
private static FrameSnapshot CreateSnapshot(
    int frameID,
    int player0X,
    int player1X,
    int ballX)
{
    return new FrameSnapshot
    {
        frameID = frameID,
        player1X = FixedInt.FromInt(player0X),
        player1Y = FixedInt.Zero,
        player1Z = FixedInt.Zero,
        player2X = FixedInt.FromInt(player1X),
        player2Y = FixedInt.Zero,
        player2Z = FixedInt.Zero,
        player1State = (int)PlayerEntity.EState.Idle,
        player2State = (int)PlayerEntity.EState.Idle,
        ballPosX = FixedInt.FromInt(ballX),
        ballPosY = FixedInt.Zero,
        ballPosZ = FixedInt.Zero,
        ballState = (int)BallEntity.EState.Free,
        ballHolder = -1
    };
}
~~~

- [ ] **Step 2: Compile and verify RED**

Expected: `ReplaceHistoryAfterRollback` is missing.

- [ ] **Step 3: Implement replacement**

Add:

~~~csharp
public void ReplaceHistoryAfterRollback(
    FrameSnapshot newest,
    FrameSnapshot? previous,
    FrameSnapshot? oldest)
{
    if (_playerCount != 2)
        throw new InvalidOperationException(
            "当前完整世界快照只支持两名球员。");
    if (!newest.IsValid)
        throw new ArgumentException("最新回滚快照无效。", nameof(newest));
    if (previous.HasValue &&
        previous.Value.frameID != newest.frameID - 1)
    {
        throw new ArgumentException(
            "前一回滚快照必须紧邻最新快照。",
            nameof(previous));
    }
    if (oldest.HasValue &&
        (!previous.HasValue ||
         oldest.Value.frameID != previous.Value.frameID - 1))
    {
        throw new ArgumentException(
            "最旧回滚快照必须紧邻前一快照。",
            nameof(oldest));
    }

    CaptureSnapshot(newest, _currentPlayerPositions, out _currentBall);
    CurrentFrameID = newest.frameID;
    if (previous.HasValue)
    {
        CaptureSnapshot(
            previous.Value,
            _previousPlayerPositions,
            out _previousBall);
        PreviousFrameID = previous.Value.frameID;
    }
    else
    {
        CopyCurrentToPrevious();
        PreviousFrameID = CurrentFrameID;
    }

    if (oldest.HasValue)
    {
        CaptureSnapshot(
            oldest.Value,
            _oldestPlayerPositions,
            out _oldestBall);
        OldestFrameID = oldest.Value.frameID;
    }
    else
    {
        CopyPreviousToOldest();
        OldestFrameID = PreviousFrameID;
    }
    IsReady = true;
}
~~~

Add:

~~~csharp
private static void CaptureSnapshot(
    FrameSnapshot snapshot,
    Vector3[] players,
    out BallEndpoint ballEndpoint)
{
    players[0] = new Vector3(
        snapshot.player1X.ToFloat(),
        snapshot.player1Y.ToFloat() + 0.5f,
        snapshot.player1Z.ToFloat());
    players[1] = new Vector3(
        snapshot.player2X.ToFloat(),
        snapshot.player2Y.ToFloat() + 0.5f,
        snapshot.player2Z.ToFloat());
    Vector3 position = new Vector3(
        snapshot.ballPosX.ToFloat(),
        snapshot.ballPosY.ToFloat(),
        snapshot.ballPosZ.ToFloat());
    var state = (BallEntity.EState)snapshot.ballState;
    int holder = snapshot.ballHolder;
    Vector3 offset = Vector3.zero;
    if (state == BallEntity.EState.Held &&
        holder >= 0 &&
        holder < players.Length)
    {
        offset = position - players[holder];
    }

    ballEndpoint = new BallEndpoint
    {
        Position = position,
        State = state,
        HolderPlayerIndex = holder,
        HeldOffset = offset
    };
}
~~~

Update legacy `ReplaceAfterRollback` to copy the corrected live endpoint into all three slots and frame IDs. Keep it as the safe fallback if the final replay snapshot is unexpectedly absent.

Use:

~~~csharp
public void ReplaceAfterRollback(
    int frameID,
    PlayerEntity[] players,
    BallEntity ball)
{
    ValidateWorld(players, ball);
    CaptureCurrent(players, ball);
    CopyCurrentToPrevious();
    CopyPreviousToOldest();
    OldestFrameID = frameID;
    PreviousFrameID = frameID;
    CurrentFrameID = frameID;
    IsReady = true;
}
~~~

- [ ] **Step 4: Compile and verify GREEN**

Run the interpolator fixture. All replacement, policy, timing, and legacy cases pass.

### Task 6: Integrate attachment decisions and GameController

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Scripts/Presentation/PresentationTargetResolver.cs`
- Modify: `Project/Frame Synchronization/Assets/Scripts/GameController.cs`
- Test: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationTargetResolverTests.cs`

- [ ] **Step 1: Write failing decision tests**

Add:

~~~csharp
[TestCase(-1, -1, false)]
[TestCase(-1, 0, true)]
[TestCase(0, -1, true)]
[TestCase(0, 1, true)]
[TestCase(1, 1, false)]
public void ShouldTransferBallVisualCorrection_AttachmentChangesOnly(
    int previous,
    int current,
    bool expected)
{
    Assert.AreEqual(
        expected,
        PresentationTargetResolver.ShouldTransferBallVisualCorrection(
            previous,
            current));
}

[Test]
public void ShouldUseIndependentBallSmoother_OnlyWhenDetached()
{
    Assert.IsTrue(
        PresentationTargetResolver.ShouldUseIndependentBallSmoother(
            new PresentationBallSample(Vector3.zero, -1)));
    Assert.IsFalse(
        PresentationTargetResolver.ShouldUseIndependentBallSmoother(
            new PresentationBallSample(Vector3.zero, 0)));
}
~~~

- [ ] **Step 2: Compile and verify RED**

Expected: missing attachment-key overload and smoother policy.

- [ ] **Step 3: Add the pure decisions**

~~~csharp
public static bool ShouldTransferBallVisualCorrection(
    int previousAttachment,
    int currentAttachment)
{
    return previousAttachment != currentAttachment;
}

public static bool ShouldUseIndependentBallSmoother(
    PresentationBallSample sample)
{
    return !sample.IsAttached;
}
~~~

Keep the legacy state/holder overload.

- [ ] **Step 4: Replace live state memory**

In `GameController` replace the three live ball presentation fields with:

~~~csharp
private int _lastPresentedBallAttachmentIndex = -1;
private bool _hasPresentedBallAttachment;
~~~

Replace the remember method:

~~~csharp
private void RememberPresentedBallAttachment(
    PresentationBallSample sample)
{
    _lastPresentedBallAttachmentIndex =
        sample.AttachedPlayerIndex;
    _hasPresentedBallAttachment = true;
}
~~~

- [ ] **Step 5: Use mixed evaluation in SyncPresentationFromLogic**

Replace the interpolator call:

~~~csharp
_presentationInterpolator.Evaluate(
    GetPresentationLocalPlayerIndex(),
    _frameEngine.RenderInterpolationAlpha,
    _interpolatedPlayerPositions,
    out PresentationBallSample ballSample);
~~~

Keep the player smoother loop. Replace basketball resolution:

~~~csharp
Vector3 ballTarget =
    PresentationTargetResolver.ResolveBufferedBallTarget(
        ballSample,
        _interpolatedPlayerPositions,
        _playerPresentationPositions);
bool useIndependentSmoother =
    PresentationTargetResolver.ShouldUseIndependentBallSmoother(
        ballSample);
if (_hasPresentedBallAttachment &&
    PresentationTargetResolver.ShouldTransferBallVisualCorrection(
        _lastPresentedBallAttachmentIndex,
        ballSample.AttachedPlayerIndex) &&
    useIndependentSmoother)
{
    _ballSmoother.BeginCorrection(
        _ballObject.transform.position,
        ballTarget);
}

Vector3 ballPosition;
if (useIndependentSmoother)
{
    ballPosition = _ballSmoother.Evaluate(ballTarget, deltaTime);
}
else
{
    _ballSmoother.Snap();
    ballPosition = ballTarget;
}

_ballObject.transform.position = ballPosition;
RememberPresentedBallAttachment(ballSample);
~~~

- [ ] **Step 6: Reset attachment history on lifecycle snaps**

In `SnapPresentationToLogic` after snapping smoothers:

~~~csharp
_hasPresentedBallAttachment = false;
_lastPresentedBallAttachmentIndex = -1;
_presentationInterpolator?.Reset(
    _frameEngine.CurrentFrame - 1,
    _playerEntities,
    _ballEntity);
SyncPresentationFromLogic(0f);
~~~

Remove the old live state remember call.

- [ ] **Step 7: Replace corrected history after replay**

Add:

~~~csharp
private void ReplacePresentationHistoryAfterRollback(
    int lastExecutedFrame)
{
    if (_predictionSystem.TryGetWorldSnapshot(
        lastExecutedFrame,
        out FrameSnapshot newest))
    {
        FrameSnapshot? previous = null;
        FrameSnapshot? oldest = null;
        if (_predictionSystem.TryGetWorldSnapshot(
            lastExecutedFrame - 1,
            out FrameSnapshot previousValue))
        {
            previous = previousValue;
            if (_predictionSystem.TryGetWorldSnapshot(
                lastExecutedFrame - 2,
                out FrameSnapshot oldestValue))
            {
                oldest = oldestValue;
            }
        }

        _presentationInterpolator.ReplaceHistoryAfterRollback(
            newest,
            previous,
            oldest);
        return;
    }

    Debug.LogError(
        $"[RouteC][Presentation] frame={lastExecutedFrame} " +
        "回滚最终快照缺失，表现缓冲退化为纠正后的实时世界");
    _presentationInterpolator.ReplaceAfterRollback(
        lastExecutedFrame,
        _playerEntities,
        _ballEntity);
}
~~~

Use:

~~~csharp
ReplacePresentationHistoryAfterRollback(lastExecutedFrame);
BeginRollbackPresentationCorrection(ballDisplayPosition);
~~~

- [ ] **Step 8: Make rollback correction attachment-aware**

Evaluate with the local index and `PresentationBallSample`:

~~~csharp
_presentationInterpolator.Evaluate(
    GetPresentationLocalPlayerIndex(),
    _frameEngine.RenderInterpolationAlpha,
    _interpolatedPlayerPositions,
    out PresentationBallSample ballSample);
~~~

After beginning player corrections:

~~~csharp
Vector3 ballTarget =
    PresentationTargetResolver.ResolveBufferedBallTarget(
        ballSample,
        _interpolatedPlayerPositions,
        _playerPresentationPositions);
if (PresentationTargetResolver.ShouldUseIndependentBallSmoother(
    ballSample))
{
    _ballSmoother.BeginCorrection(
        ballDisplayPosition,
        ballTarget);
}
else
{
    _ballSmoother.Snap();
}
RememberPresentedBallAttachment(ballSample);
~~~

- [ ] **Step 9: Compile and verify GREEN**

Run all three presentation fixtures. Zero failures and zero new P1-E warnings.

### Task 7: Prove allocation and deterministic isolation

**Files:**
- Modify: `Project/Frame Synchronization/Assets/Tests/EditMode/PresentationFrameInterpolatorTests.cs`

- [ ] **Step 1: Add the allocation test**

~~~csharp
[Test]
public void Evaluate_AfterWarmup_DoesNotAllocateManagedMemory()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    players[0].position.x = FixedInt.FromInt(-2);
    interpolator.PushLogicFrame(11, players, ball);
    players[0].position.x = FixedInt.FromInt(-1);
    interpolator.PushLogicFrame(12, players, ball);
    for (int i = 0; i < 32; i++)
        interpolator.Evaluate(0, 0.5f, output, out _);

    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 1000; i++)
        interpolator.Evaluate(0, 0.5f, output, out _);
    long after = GC.GetAllocatedBytesForCurrentThread();

    Assert.AreEqual(before, after);
}
~~~

If the selected Unity API profile lacks `GetAllocatedBytesForCurrentThread`, remove only this test and use ProfilerRecorder during manual acceptance; the no-allocation requirement remains.

- [ ] **Step 2: Add logic isolation**

~~~csharp
[Test]
public void Evaluate_DoesNotMutateLogicWorld()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.FromInt(2));
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    FixedVector3 player0Before = players[0].position;
    FixedVector3 player1Before = players[1].position;
    FixedVector3 ballBefore = ball.position;
    BallEntity.EState stateBefore = ball.state;
    int holderBefore = ball.holderPlayerIndex;

    for (int i = 0; i < 100; i++)
        interpolator.Evaluate(0, 0.5f, output, out _);

    Assert.AreEqual(player0Before, players[0].position);
    Assert.AreEqual(player1Before, players[1].position);
    Assert.AreEqual(ballBefore, ball.position);
    Assert.AreEqual(stateBefore, ball.state);
    Assert.AreEqual(holderBefore, ball.holderPlayerIndex);
}
~~~

- [ ] **Step 3: Add WorldHash isolation**

~~~csharp
[Test]
public void ReplaceHistoryAndEvaluate_DoNotChangeWorldHash()
{
    PlayerEntity[] players = CreatePlayers();
    BallEntity ball = CreateBall(FixedInt.Zero);
    var interpolator = new PresentationFrameInterpolator(2);
    var output = new Vector3[2];
    interpolator.Reset(10, players, ball);
    FrameSnapshot oldest = CreateSnapshot(10, -3, 3, 0);
    FrameSnapshot previous = CreateSnapshot(11, -2, 4, 2);
    FrameSnapshot newest = CreateSnapshot(12, -1, 5, 4);
    ulong before = WorldHash.Compute(newest, 12);

    interpolator.ReplaceHistoryAfterRollback(
        newest,
        previous,
        oldest);
    interpolator.Evaluate(0, 0.5f, output, out _);
    ulong after = WorldHash.Compute(newest, 12);

    Assert.AreEqual(before, after);
    Assert.AreEqual(12, newest.frameID);
    Assert.AreEqual(FixedInt.FromInt(4), newest.ballPosX);
    Assert.AreEqual((int)BallEntity.EState.Free, newest.ballState);
    Assert.AreEqual(-1, newest.ballHolder);
}
~~~

- [ ] **Step 4: Run GREEN and inspect isolation**

Run the interpolator fixture. Confirm no P1-E field enters `PlayerEntity`, `BallEntity`, `FrameInput`, `FrameSnapshot`, or `WorldHash`, and simulation/prediction/replay do not read presentation state.

### Task 8: Verification, independent review, and Unity handoff

**Files:**
- Verify all files in the file map.
- Do not modify `E:/帧同步`.

- [ ] **Step 1: Compile complete assemblies**

Run both reusable compile commands. Expected: exit code 0 and zero errors.

- [ ] **Step 2: Run non-native regressions**

Use the temporary runner for:

~~~text
BallPhysicsSystemTests
BallPossessionSystemTests
BallShotSystemTests
CanonicalFrameTests
FrameReplaySystemTests
FrameSimulationSystemTests
NetworkFrameTimelineTests
NetworkStreamReaderTests
PredictionSystemResolveRemoteTests
PredictionSystemSnapshotTests
PresentationFrameInterpolatorTests
PresentationTargetResolverTests
PresentationCorrectionSmootherTests
RollbackRequestBufferTests
WorldHashTests
~~~

Expected: zero failures; preserve the previous 145 non-native passes, skip only the 8 Unity-native cases, and add the new P1-E cases.

Also rerun the existing failed-replay preflight case in
`FrameReplaySystemTests`; it must still prove that a failed replay returns
before any snapshot, entity, or presentation replacement can occur.

- [ ] **Step 3: Recheck repository safety**

Verify:

~~~text
Route C branch = delivery/route-c
Route C HEAD = 37260b437260c7712c658d5d0e05cbdd183accfb
git diff --check = clean
Original branch = main
Original HEAD = 346b9ed235523ee3cd5fa9bb55d7f405a12cb1a7
Original status count = 3
Original fingerprint = 2992895D404E0E3DA707992A6096957379219F8DA0C6050D503F814F34B484D6
~~~

Use the same LF-joined, no-trailing-newline, UTF-8 SHA256 calculation recorded in the handoff.

- [ ] **Step 4: Request independent read-only review**

Reviewer checks three-slot ordering, catch-up, local-index symmetry, local and remote time ranges, local Held priority, delayed remote possession, Held offset consistency, Airborne/Free/Scored transitions, atomic rollback history replacement, missing-history fallback, allocation, lifecycle behavior, and deterministic isolation. Resolve every Critical or Important finding, add a regression test for behavioral defects, and rerun affected verification.

- [ ] **Step 5: Remove only the temporary runner**

Resolve `C:/tmp/RouteC-P1E-Runner` with `GetFullPath`, compare it to the same explicit expected path, then remove that directory recursively. Do not remove any other temporary or repository path.

- [ ] **Step 6: Hand off Unity checks without operating Unity**

Provide:

~~~text
1. 停止 Play Mode，等待 Unity 编译完成；Console 无红色错误。
2. 运行全部 EditMode；原有 153 项加 P1-E 新增用例全部通过，最终数量以实际发现数为准。
3. Editor 和打包客户端双端运行，分别确认本地玩家没有新增一帧延迟。
4. 双端直线、斜向、启动、停止和快速变向无新增抖动。
5. 连续快速改变远端输入，可见回收到变向点的距离明显小于 P1-D。
6. 本地 Held 与远端 Held 均稳定跟手，无持续人球分离。
7. Held→Airborne、Airborne→Free、Airborne→Scored、Scored 落地无闪跳或二次纠正。
8. Reset、暂停恢复、录制回放进入/退出无异常跳变。
9. 回滚无首帧冻结、明显目标穿越或二次回抽。
10. 两端最终逻辑位置及同一 canonical frame 的 WorldHash 一致。
~~~

Do not claim P1-E complete and do not generate the stage summary or next-window handoff until Unity and dual-client acceptance both pass.
