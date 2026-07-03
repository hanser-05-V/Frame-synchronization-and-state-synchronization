using UnityEngine;

namespace FrameSyncDemo
{
    //定点数  游戏内数据  消除精度影响 (保证每台计算机结构一致)
    [System.Serializable]
    public struct FixedNumber
    {
        public const int SCALE = 1000;  //倍率  避免小数 
        public int _raw; 
        
        public FixedNumber(int raw)
        {
            _raw = raw;
        }
        
        //定点数常量
        public static readonly FixedNumber Zero = new FixedNumber(0); //定点数为0 
        public static readonly FixedNumber One = new FixedNumber(SCALE); // 1倍
        public static readonly FixedNumber Half = new FixedNumber(SCALE / 2); //0.5倍
        
        #region 数据转化
        //float -> 定点
        public static FixedNumber FloatToFixedInput(float value)
        {
            return new FixedNumber(Mathf.RoundToInt(value * SCALE));
        }
        //Int  -> 定点
        public static FixedNumber IntToFixedInput(int value)
        {
            return new FixedNumber(value * SCALE);
        }
        //定点 -> float 
        public float FixedInputToFloat()
        {
            return _raw /(float)SCALE;  
        }

        public int FixedInputToInt()
        {
            return _raw / SCALE;
        }
        #endregion

        #region 运算符重载
        public static FixedNumber operator +(FixedNumber a, FixedNumber b)
        {
            return new FixedNumber(a._raw + b._raw);
        }

        public static FixedNumber operator -(FixedNumber a, FixedNumber b)
        {
            return new FixedNumber(a._raw - b._raw);
        }
        
        public static FixedNumber operator *(FixedNumber a, FixedNumber b) => new FixedNumber((a._raw * b._raw)/SCALE);
        
        public static FixedNumber operator /(FixedNumber a, FixedNumber b) => new FixedNumber((a._raw * SCALE) / b._raw);

        public static bool operator ==(FixedNumber a, FixedNumber b) => a._raw == b._raw; 
        public static bool operator !=(FixedNumber a, FixedNumber b) => a._raw != b._raw;  
        public static bool operator >=(FixedNumber a ,FixedNumber b) => a._raw >= b._raw; 
        public static bool operator <=(FixedNumber a, FixedNumber b) => a._raw <= b._raw; 
        public static bool operator > (FixedNumber a, FixedNumber b) => a._raw > b._raw;
        public static bool operator < (FixedNumber a, FixedNumber b) => a._raw < b._raw;  
        
        public static FixedNumber operator -(FixedNumber a) => new FixedNumber(-a._raw);
        
        #endregion

        #region 重写方法
        public override string ToString()
        {
            float value = FixedInputToFloat();
            return value.ToString("F3"); //默认保留 3位 小数
        }

        public override bool Equals(object obj)
        {
           return obj is FixedNumber other && _raw ==  other._raw;
        }

        public override int GetHashCode()
        {
            return _raw.GetHashCode();
        }
        
        #endregion
    }
}