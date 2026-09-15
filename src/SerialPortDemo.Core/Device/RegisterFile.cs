using System;

namespace SerialPortDemo.Device
{
    /// <summary>
    /// 保持寄存器区（下位机侧的数据模型）。
    /// 实际项目里可以替换成 Modbus 寄存器表、EEPROM 镜像或设备状态结构体。
    /// </summary>
    public sealed class RegisterFile
    {
        private readonly ushort[] _registers;
        private readonly object _sync = new object();

        public RegisterFile(int size = 256)
        {
            if (size <= 0 || size > 65536) throw new ArgumentOutOfRangeException(nameof(size), "寄存器数量必须在 1 ~ 65536 之间");
            _registers = new ushort[size];
        }

        public int Size => _registers.Length;

        /// <summary>读寄存器；地址越界返回 false。</summary>
        public bool TryRead(ushort address, ushort count, out ushort[] values)
        {
            values = new ushort[0];
            if (count == 0) return false;
            if (address + count > _registers.Length) return false;

            values = new ushort[count];
            lock (_sync)
            {
                for (int i = 0; i < count; i++)
                {
                    values[i] = _registers[address + i];
                }
            }
            return true;
        }

        /// <summary>写单个寄存器；地址越界返回 false。</summary>
        public bool TryWrite(ushort address, ushort value)
        {
            if (address >= _registers.Length) return false;
            lock (_sync)
            {
                _registers[address] = value;
            }
            return true;
        }

        /// <summary>直接访问某个寄存器（越界抛异常）。</summary>
        public ushort this[ushort address]
        {
            get
            {
                if (address >= _registers.Length) throw new ArgumentOutOfRangeException(nameof(address));
                lock (_sync)
                {
                    return _registers[address];
                }
            }
            set => TryWrite(address, value);
        }

        /// <summary>全部清零。</summary>
        public void Clear()
        {
            lock (_sync)
            {
                Array.Clear(_registers, 0, _registers.Length);
            }
        }
    }
}
