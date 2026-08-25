using System;

namespace FrameSyncDemo
{
    public static class ViewWorldBuilder
    {
        private readonly struct TrackEndpoints
        {
            public TrackEndpoints(
                in SimulationWorldState from,
                in SimulationWorldState to,
                int fromFrame,
                int toFrame,
                ViewSampleSource source)
            {
                From = from;
                To = to;
                FromFrame = fromFrame;
                ToFrame = toFrame;
                Source = source;
            }

            public SimulationWorldState From { get; }
            public SimulationWorldState To { get; }
            public int FromFrame { get; }
            public int ToFrame { get; }
            public ViewSampleSource Source { get; }
        }

        public static bool TryBuild(
            FrameSyncCoordinator coordinator,
            int localPlayerIndex,
            out ViewWorldState view)
        {
            ValidateArguments(coordinator, localPlayerIndex);
            return TryBuild(
                coordinator,
                localPlayerIndex,
                coordinator.ConfirmedFrame,
                false,
                out view);
        }

        public static bool TryBuild(
            FrameSyncCoordinator coordinator,
            int localPlayerIndex,
            int presentationFrame,
            out ViewWorldState view)
        {
            ValidateArguments(coordinator, localPlayerIndex);
            if (presentationFrame < -1 ||
                presentationFrame > coordinator.ConfirmedFrame)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(presentationFrame));
            }

            return TryBuild(
                coordinator,
                localPlayerIndex,
                presentationFrame,
                true,
                out view);
        }

        private static bool TryBuild(
            FrameSyncCoordinator coordinator,
            int localPlayerIndex,
            int presentationFrame,
            bool requireConsecutiveConfirmedInterval,
            out ViewWorldState view)
        {
            if (!TryGetPredictedEndpoints(
                    coordinator,
                    out TrackEndpoints predicted) ||
                !TryGetConfirmedEndpoints(
                    coordinator,
                    presentationFrame,
                    requireConsecutiveConfirmedInterval,
                    out TrackEndpoints confirmed))
            {
                view = default;
                return false;
            }

            int remotePlayerIndex = localPlayerIndex == 0 ? 1 : 0;
            ViewPlayerState local = CreatePlayer(
                localPlayerIndex,
                predicted);
            ViewPlayerState remote = CreatePlayer(
                remotePlayerIndex,
                confirmed);
            ViewPlayerState player0 = localPlayerIndex == 0
                ? local
                : remote;
            ViewPlayerState player1 = localPlayerIndex == 1
                ? local
                : remote;
            TrackEndpoints ballEndpoints = SelectBallEndpoints(
                localPlayerIndex,
                remotePlayerIndex,
                predicted,
                confirmed);
            var ball = new ViewBallState(
                ballEndpoints.From.ball,
                ballEndpoints.To.ball,
                ballEndpoints.FromFrame,
                ballEndpoints.ToFrame,
                ballEndpoints.Source);

            view = new ViewWorldState(
                coordinator.PredictedFrame,
                coordinator.ConfirmedFrame,
                confirmed.ToFrame,
                player0,
                player1,
                ball);
            return true;
        }

        private static void ValidateArguments(
            FrameSyncCoordinator coordinator,
            int localPlayerIndex)
        {
            if (coordinator == null)
                throw new ArgumentNullException(nameof(coordinator));
            if (localPlayerIndex < 0 || localPlayerIndex > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localPlayerIndex));
            }
        }

        private static ViewPlayerState CreatePlayer(
            int playerIndex,
            in TrackEndpoints endpoints)
        {
            SimulationPlayerState from = GetPlayer(
                endpoints.From,
                playerIndex);
            SimulationPlayerState to = GetPlayer(
                endpoints.To,
                playerIndex);
            return new ViewPlayerState(
                playerIndex,
                from,
                to,
                endpoints.FromFrame,
                endpoints.ToFrame,
                endpoints.Source);
        }

        private static TrackEndpoints SelectBallEndpoints(
            int localPlayerIndex,
            int remotePlayerIndex,
            in TrackEndpoints predicted,
            in TrackEndpoints confirmed)
        {
            SimulationBallState predictedBall = predicted.To.ball;
            SimulationBallState confirmedBall = confirmed.To.ball;
            if (IsHeldBy(confirmedBall, remotePlayerIndex))
                return confirmed;
            if (IsHeldBy(predictedBall, localPlayerIndex))
                return predicted;

            bool confirmedLocalHeld =
                IsHeldBy(confirmedBall, localPlayerIndex);
            bool predictedWasLocalHeld = IsHeldBy(
                predicted.From.ball,
                localPlayerIndex);
            bool predictedLocalReleased =
                (confirmedLocalHeld || predictedWasLocalHeld) &&
                predictedBall.state != (int)BallEntity.EState.Held &&
                predictedBall.holderPlayerIndex < 0;
            if (predictedLocalReleased)
                return predicted;

            return confirmed;
        }

        private static bool IsHeldBy(
            in SimulationBallState ball,
            int playerIndex)
        {
            return ball.state == (int)BallEntity.EState.Held &&
                ball.holderPlayerIndex == playerIndex;
        }

        private static SimulationPlayerState GetPlayer(
            in SimulationWorldState world,
            int playerIndex)
        {
            return playerIndex == 0 ? world.player0 : world.player1;
        }

        private static bool TryGetPredictedEndpoints(
            FrameSyncCoordinator coordinator,
            out TrackEndpoints endpoints)
        {
            int newestFrame = coordinator.PredictedFrame;
            if (newestFrame < 0)
            {
                SimulationWorldState initial = coordinator.InitialWorld;
                endpoints = new TrackEndpoints(
                    initial,
                    initial,
                    -1,
                    -1,
                    ViewSampleSource.PredictedSingleEndpoint);
                return true;
            }

            if (!coordinator.TryGetSnapshot(
                    WorldTrack.Predicted,
                    newestFrame,
                    out SimulationWorldState newest))
            {
                endpoints = default;
                return false;
            }

            int previousFrame = newestFrame - 1;
            if (previousFrame >= 0 &&
                coordinator.TryGetSnapshot(
                    WorldTrack.Predicted,
                    previousFrame,
                    out SimulationWorldState previous))
            {
                endpoints = new TrackEndpoints(
                    previous,
                    newest,
                    previousFrame,
                    newestFrame,
                    ViewSampleSource.Predicted);
                return true;
            }

            endpoints = new TrackEndpoints(
                newest,
                newest,
                newestFrame,
                newestFrame,
                ViewSampleSource.PredictedSingleEndpoint);
            return true;
        }

        private static bool TryGetConfirmedEndpoints(
            FrameSyncCoordinator coordinator,
            int newestFrame,
            bool requireConsecutiveInterval,
            out TrackEndpoints endpoints)
        {
            if (newestFrame < 0)
            {
                SimulationWorldState initial = coordinator.InitialWorld;
                endpoints = new TrackEndpoints(
                    initial,
                    initial,
                    -1,
                    -1,
                    ViewSampleSource.InitialConfirmed);
                return true;
            }

            if (!coordinator.TryGetSnapshot(
                    WorldTrack.Confirmed,
                    newestFrame,
                    out SimulationWorldState newest))
            {
                endpoints = default;
                return false;
            }

            int previousFrame = newestFrame - 1;
            if (previousFrame == -1 && requireConsecutiveInterval)
            {
                endpoints = new TrackEndpoints(
                    coordinator.InitialWorld,
                    newest,
                    -1,
                    newestFrame,
                    ViewSampleSource.Confirmed);
                return true;
            }

            if (previousFrame >= 0 &&
                coordinator.TryGetSnapshot(
                    WorldTrack.Confirmed,
                    previousFrame,
                    out SimulationWorldState previous))
            {
                endpoints = new TrackEndpoints(
                    previous,
                    newest,
                    previousFrame,
                    newestFrame,
                    ViewSampleSource.Confirmed);
                return true;
            }

            if (requireConsecutiveInterval)
            {
                endpoints = default;
                return false;
            }

            endpoints = new TrackEndpoints(
                newest,
                newest,
                newestFrame,
                newestFrame,
                ViewSampleSource.ConfirmedSingleEndpoint);
            return true;
        }
    }
}
