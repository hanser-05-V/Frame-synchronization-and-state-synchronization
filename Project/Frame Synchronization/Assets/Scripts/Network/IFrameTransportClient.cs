using System;

namespace FrameSyncDemo
{
    public interface IFrameTransportClient : IDisposable
    {
        NetworkSessionState State { get; }

        bool IsRunning { get; }

        bool HasStartedSession { get; }

        int LocalPlayerIndex { get; }

        int LatestRemoteFrameID { get; }

        void Start(string host, int port);

        bool TryEnqueueLocalInput(uint raw, int localFrameID);

        bool TryDequeueRemoteInput(out NetworkPacketArrival arrival);

        bool TryDequeueEvent(out NetworkTransportEvent transportEvent);

        void SubmitResumeReadiness(in ResumeReadiness readiness);

        void RequestStop();

        bool WaitForStop(int millisecondsTimeout);
    }
}
