namespace FrameSyncDemo
{
    /// <summary>
    /// 聚合 LateUpdate 前发现的回滚请求，只保留最早错误帧。
    /// </summary>
    public sealed class RollbackRequestBuffer
    {
        private bool _hasRequest;
        private int _errorFrame;
        private uint _correctRaw;

        public bool HasRequest => _hasRequest;

        public void Request(int errorFrame, uint correctRaw)
        {
            if (!_hasRequest || errorFrame < _errorFrame)
            {
                _hasRequest = true;
                _errorFrame = errorFrame;
                _correctRaw = correctRaw;
            }
        }

        public bool TryTake(out int errorFrame, out uint correctRaw)
        {
            if (!_hasRequest)
            {
                errorFrame = 0;
                correctRaw = 0;
                return false;
            }

            errorFrame = _errorFrame;
            correctRaw = _correctRaw;
            _hasRequest = false;
            return true;
        }
    }
}
