namespace FrameSyncDemo
{
    /// <summary>
    /// 联网会话由服务器共同放行，双方从同一个逻辑帧 0 起跑。
    /// </summary>
    public static class NetworkFrameTimeline
    {
        public const int RemoteFrameOffset = 0;

        public static int RemoteFrameForLocal(int localFrame)
        {
            return localFrame;
        }

        public static bool CanStartSession(bool isConnected)
        {
            return isConnected;
        }

        public static bool CanPauseLocally(bool isConnected)
        {
            return !isConnected;
        }

        public static int CalculateRemoteArrivalGap(
            int localFrame,
            int latestRemoteFrame)
        {
            return localFrame - latestRemoteFrame;
        }
    }
}
