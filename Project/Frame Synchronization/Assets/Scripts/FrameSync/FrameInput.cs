namespace FrameSyncDemo
{
    /// <summary>
    /// 帧输入 — 帧同步最小数据单元，4字节压缩
    /// 参考街篮2 FrameBuffer.Input (32-bit位域设计)
    ///
    /// 位域布局:
    ///   bit 31-16: 保留
    ///   bit 15-8:  moveDir (8方向, 0=停止, 1-8=8方向)
    ///   bit 7-0:   buttons  (bitmask, 每个bit=一个按键)
    /// </summary>
    [System.Serializable]
    public struct FrameInput
    {
        public uint _raw;

        /// <summary>移动方向: 0=停止, 1=上, 2=右上, 3=右, 4=右下, 5=下, 6=左下, 7=左, 8=左上</summary>
        public byte moveDir
        {
            get { return (byte)((_raw >> 8) & 0xFF); }
            set { _raw = (_raw & 0xFFFF00FF) | (uint)((value & 0xFF) << 8); }
        }

        /// <summary>按键 bitmask</summary>
        public byte buttons
        {
            get { return (byte)(_raw & 0xFF); }
            set { _raw = (_raw & 0xFFFFFF00) | value; }
        }

        public FrameInput(uint raw)
        {
            _raw = raw;
        }

        public FrameInput(byte moveDir, byte buttons)
        {
            _raw = 0;
            this.moveDir = moveDir;
            this.buttons = buttons;
        }

        public void Reset()
        {
            _raw = 0;
        }

        public bool Compare(FrameInput other)
        {
            return _raw == other._raw;
        }

        /// <summary>从原始uint值构造（录放回放用）</summary>
        public static FrameInput FromRaw(uint raw)
        {
            return new FrameInput(raw);
        }

        public override string ToString()
        {
            return $"dir:{moveDir} btn:0x{buttons:X2} raw:0x{_raw:X8}";
        }
    }
}
