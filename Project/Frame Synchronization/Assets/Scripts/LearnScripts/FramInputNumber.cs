namespace FrameSyncDemo
{
    /// 玩家输入 位域布局:
    ///   bit 31-16: 保留
    ///   bit 15-8:  moveDir (8方向, 0=停止, 1-8=8方向)
    ///   bit 7-0:   buttons  (bitmask, 每个bit=一个按键)
    /// </summary>
    [System.Serializable]
    public struct FramInputNumber
    {
        //本地存储 无符号 int
        public uint _raw; 
        
        //0=停止, 1=上, 2=右上, 3=右, 4=右下, 5=下, 6=左下, 7=左, 8=左上
        public byte moveDir
        {
            get { return (byte)((_raw >> 8) & 0xFF);  }  //先把 move 移动到低位 再取出来
            set {_raw = (_raw & 0xFFFF00FF)  | (uint)((value & 0xFF)<<8); } //单独清空 中8位 
        }
        //点击的按钮
        public byte buttons
        {
            get {return (byte)(_raw  & 0xFF);  }  //只留 最后 8位 
            set {_raw = (_raw & 0xFFFFFF00)  | value; } // 先清理 低位8 ， 再填入新数据
        }
        //构造函数
        public FramInputNumber(uint raw)
        {
            _raw = raw; 
        }

        public FramInputNumber(byte buttons, byte moveDir)
        {
            _raw = 0;
            this.buttons = buttons;
            this.moveDir = moveDir;
        }
        //比较
        public bool Compare(FramInputNumber other)
        {
            return (_raw == other._raw);
        }
        //重置
        public void Reset()
        {
            _raw = 0;   
        }
        //初始化 值构造 -> 回放录制
        public static FramInputNumber FromRaw(uint raw)
        {
            return new FramInputNumber(raw);
        }
        //调试时才能在 Editor Window 里看到 Input 的值
        public override string ToString()
        {
            return $"dir:{moveDir} btn:0x{buttons:X2} raw:0x{_raw:X8}";
        }
    }
}