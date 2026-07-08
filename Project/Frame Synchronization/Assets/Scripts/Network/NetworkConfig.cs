namespace FrameSyncDemo
{
    /// <summary>网络配置（静态，不挂 GameObject）</summary>
    public static class NetworkConfig
    {
        public const string DEFAULT_IP = "127.0.0.1";
        public const int DEFAULT_PORT = 8888;

        /// <summary>当前客户端是 Editor(=0) 还是 exe(=1)，由场景/命令行参数决定</summary>
        public static int LocalPlayerID = 0;  // 默认0，exe 启动时通过参数改为 1
    }
}
