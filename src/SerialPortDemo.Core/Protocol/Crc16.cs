using System;

namespace SerialPortDemo.Protocol
{
    /// <summary>
    /// CRC16/MODBUS 校验：多项式 0xA001（0x8005 反射式），初值 0xFFFF，输出不取反。
    /// 与常见 PLC / 仪表的 Modbus-RTU 校验一致，方便对接第三方设备。
    /// </summary>
    public static class Crc16
    {
        private static readonly ushort[] Table = BuildTable();

        /// <summary>计算指定范围的 CRC16。</summary>
        public static ushort Compute(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));

            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc = (ushort)((crc >> 8) ^ Table[(crc ^ buffer[offset + i]) & 0xFF]);
            }
            return crc;
        }

        /// <summary>计算整个数组的 CRC16。</summary>
        public static ushort Compute(byte[] buffer) => Compute(buffer, 0, buffer.Length);

        /// <summary>按报文约定写入 CRC（低字节在前）。</summary>
        public static void WriteLittleEndian(byte[] destination, int offset, ushort crc)
        {
            destination[offset] = (byte)(crc & 0xFF);
            destination[offset + 1] = (byte)((crc >> 8) & 0xFF);
        }

        /// <summary>按报文约定读取 CRC（低字节在前）。</summary>
        public static ushort ReadLittleEndian(byte[] source, int offset)
            => (ushort)(source[offset] | (source[offset + 1] << 8));

        private static ushort[] BuildTable()
        {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                ushort value = (ushort)i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? (ushort)((value >> 1) ^ 0xA001)
                        : (ushort)(value >> 1);
                }
                table[i] = value;
            }
            return table;
        }
    }
}
