using System;

namespace FrameSyncDemo
{
    /// <summary>
    /// 完整世界回滚编排：恢复安全快照、重建每帧输入并通过共享模拟入口追平。
    /// </summary>
    public static class FrameReplaySystem
    {
        public static FrameReplayResult Replay(
            int errorFrame,
            int lastExecutedFrame,
            uint correctedRemoteRaw,
            int remoteFrameOffset,
            int localPlayerIndex,
            int remotePlayerIndex,
            FrameBuffer frameBuffer,
            Func<int, uint?> tryGetRemoteInput,
            PredictionSystem predictionSystem,
            PlayerEntity[] players,
            PlayerStateMachine[] stateMachines,
            BallEntity ball,
            FixedInt moveDistance,
            FixedInt deltaTime,
            Action resetWorld)
        {
            ValidateArguments(
                errorFrame,
                remoteFrameOffset,
                localPlayerIndex,
                remotePlayerIndex,
                frameBuffer,
                tryGetRemoteInput,
                predictionSystem,
                players,
                stateMachines,
                ball,
                resetWorld);

            int restoredFrame = -1;
            for (int frame = errorFrame - 1; frame >= 0; frame--)
            {
                if (predictionSystem.TryGetWorldSnapshot(frame, out _))
                {
                    restoredFrame = frame;
                    break;
                }
            }

            int replayStartFrame;
            if (restoredFrame >= 0)
            {
                replayStartFrame = restoredFrame + 1;
            }
            else
            {
                replayStartFrame = 0;
            }

            int missingInputFrame = FindMissingInputFrame(
                replayStartFrame,
                lastExecutedFrame,
                localPlayerIndex,
                remotePlayerIndex,
                frameBuffer);
            if (missingInputFrame >= 0)
            {
                return new FrameReplayResult(
                    false,
                    restoredFrame,
                    0,
                    missingInputFrame);
            }

            if (restoredFrame >= 0)
            {
                if (!predictionSystem.RestoreWorldSnapshot(
                    restoredFrame,
                    players,
                    ball))
                {
                    throw new InvalidOperationException(
                        "预检通过后安全快照不可用。");
                }
            }
            else
            {
                resetWorld();
            }

            int replayedFrameCount = 0;
            uint lastKnownRemoteRaw = correctedRemoteRaw;
            for (int frame = replayStartFrame; frame <= lastExecutedFrame; frame++)
            {
                if (!frameBuffer.PeekFrame(frame, out FrameBuffer.Frame frameData))
                {
                    throw new InvalidOperationException(
                        $"预检通过后帧 {frame} 输入不可用。");
                }

                var inputs = new FrameInput[2];
                inputs[localPlayerIndex] = frameData.inputs[localPlayerIndex];

                int remoteFrame = frame - remoteFrameOffset;
                uint? actualRemoteRaw = tryGetRemoteInput(remoteFrame);
                uint replayRemoteRaw;
                bool isPredicted = false;
                if (actualRemoteRaw.HasValue)
                {
                    replayRemoteRaw = actualRemoteRaw.Value;
                    lastKnownRemoteRaw = replayRemoteRaw;
                }
                else if (frame < errorFrame)
                {
                    replayRemoteRaw = frameData.inputs[remotePlayerIndex]._raw;
                }
                else if (frame == errorFrame)
                {
                    replayRemoteRaw = correctedRemoteRaw;
                    lastKnownRemoteRaw = replayRemoteRaw;
                }
                else
                {
                    replayRemoteRaw = FrameInput
                        .FromRaw(lastKnownRemoteRaw)
                        .ToPredictionInput()
                        ._raw;
                    isPredicted = true;
                }

                inputs[remotePlayerIndex] = FrameInput.FromRaw(replayRemoteRaw);

                FrameSimulationSystem.Step(
                    players,
                    stateMachines,
                    ball,
                    inputs,
                    moveDistance,
                    deltaTime);
                predictionSystem.TakeWorldSnapshot(frame, players, ball);
                if (frame >= errorFrame)
                {
                    predictionSystem.RecordReplayRemote(
                        frame,
                        replayRemoteRaw,
                        isPredicted);
                }
                replayedFrameCount++;
            }

            return new FrameReplayResult(
                true,
                restoredFrame,
                replayedFrameCount,
                -1);
        }

        private static int FindMissingInputFrame(
            int replayStartFrame,
            int lastExecutedFrame,
            int localPlayerIndex,
            int remotePlayerIndex,
            FrameBuffer frameBuffer)
        {
            for (int frame = replayStartFrame; frame <= lastExecutedFrame; frame++)
            {
                if (!frameBuffer.PeekFrame(frame, out FrameBuffer.Frame frameData) ||
                    frameData.inputs == null ||
                    frameData.inputs.Length <= localPlayerIndex ||
                    frameData.inputs.Length <= remotePlayerIndex)
                {
                    return frame;
                }
            }

            return -1;
        }

        private static void ValidateArguments(
            int errorFrame,
            int remoteFrameOffset,
            int localPlayerIndex,
            int remotePlayerIndex,
            FrameBuffer frameBuffer,
            Func<int, uint?> tryGetRemoteInput,
            PredictionSystem predictionSystem,
            PlayerEntity[] players,
            PlayerStateMachine[] stateMachines,
            BallEntity ball,
            Action resetWorld)
        {
            if (errorFrame < 0)
                throw new ArgumentOutOfRangeException(nameof(errorFrame));
            if (remoteFrameOffset == int.MinValue)
                throw new ArgumentException("远端帧偏移尚未锚定。", nameof(remoteFrameOffset));
            if (localPlayerIndex < 0 || localPlayerIndex > 1)
                throw new ArgumentOutOfRangeException(nameof(localPlayerIndex));
            if (remotePlayerIndex < 0 || remotePlayerIndex > 1 ||
                remotePlayerIndex == localPlayerIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(remotePlayerIndex));
            }
            if (frameBuffer == null)
                throw new ArgumentNullException(nameof(frameBuffer));
            if (tryGetRemoteInput == null)
                throw new ArgumentNullException(nameof(tryGetRemoteInput));
            if (predictionSystem == null)
                throw new ArgumentNullException(nameof(predictionSystem));
            if (players == null || players.Length != 2 ||
                players[0] == null || players[1] == null)
            {
                throw new ArgumentException("重演世界必须恰好包含两名非空球员。", nameof(players));
            }
            if (stateMachines == null || stateMachines.Length != 2 ||
                stateMachines[0] == null || stateMachines[1] == null)
            {
                throw new ArgumentException(
                    "重演世界必须恰好包含两个非空状态机。",
                    nameof(stateMachines));
            }
            if (ball == null)
                throw new ArgumentNullException(nameof(ball));
            if (resetWorld == null)
                throw new ArgumentNullException(nameof(resetWorld));
        }
    }
}
