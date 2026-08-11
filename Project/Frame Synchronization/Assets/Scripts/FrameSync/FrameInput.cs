using System;

namespace FrameSyncDemo
{
    /// <summary>
    /// 帧输入 — 帧同步最小数据单元，4字节压缩
    /// 参考街篮2 FrameBuffer.Input (32-bit位域设计)
    ///
    /// 位域布局:
    ///   bit 31-16: 保留
    ///   bit 15-8:  moveDir (8方向, 0=停止, 1-8=8方向)
    ///   bit 7-0:   buttons (bitmask)
    ///     bit 0 (0x01): ShootReleased — 投篮松开边沿
    ///     bit 1 (0x02): Pass     — 传球
    ///     bit 2 (0x04): Steal    — 抢断
    ///     bit 3 (0x08): Block    — 盖帽
    ///     bit 4 (0x10): Sprint   — 冲刺
    ///     bit 5 (0x20): PickupPressed — 捡球按下边沿
    ///     bit 6 (0x40): EndMatchPressed — 结束比赛按下边沿
    ///     bit 7: 保留
    /// </summary>
    [System.Serializable]
    public struct FrameInput
    {
        private const uint TransientActionMask = 0x61u;

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

        // ----- 扩展按键属性（阶段1+）-----
        /// <summary>投篮松开边沿的兼容别名</summary>
        public bool shoot
        {
            get { return (_raw & 0x01) != 0; }
            set { _raw = value ? (_raw | 0x01) : (_raw & ~0x01u); }
        }

        /// <summary>投篮松开边沿</summary>
        public bool shootReleased
        {
            get { return shoot; }
            set { shoot = value; }
        }

        /// <summary>传球按键</summary>
        public bool pass
        {
            get { return (_raw & 0x02) != 0; }
            set { _raw = value ? (_raw | 0x02) : (_raw & ~0x02u); }
        }

        /// <summary>抢断按键</summary>
        public bool steal
        {
            get { return (_raw & 0x04) != 0; }
            set { _raw = value ? (_raw | 0x04) : (_raw & ~0x04u); }
        }

        /// <summary>盖帽按键</summary>
        public bool block
        {
            get { return (_raw & 0x08) != 0; }
            set { _raw = value ? (_raw | 0x08) : (_raw & ~0x08u); }
        }

        /// <summary>冲刺按键</summary>
        public bool sprint
        {
            get { return (_raw & 0x10) != 0; }
            set { _raw = value ? (_raw | 0x10) : (_raw & ~0x10u); }
        }

        /// <summary>捡球按下边沿</summary>
        public bool pickupPressed
        {
            get { return (_raw & 0x20) != 0; }
            set { _raw = value ? (_raw | 0x20) : (_raw & ~0x20u); }
        }

        public bool endMatchPressed
        {
            get { return (_raw & 0x40) != 0; }
            set { _raw = value ? (_raw | 0x40) : (_raw & ~0x40u); }
        }

        public FrameInput ToPredictionInput()
        {
            return new FrameInput(_raw & ~TransientActionMask);
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
