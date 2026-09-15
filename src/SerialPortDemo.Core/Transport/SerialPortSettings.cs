using System;
using System.IO.Ports;

namespace SerialPortDemo.Transport
{
    /// <summary>串口参数。默认 115200-8-N-1，无流控（232 三线制最常用）。</summary>
    public sealed class SerialPortSettings
    {
        public SerialPortSettings(string portName)
        {
            PortName = portName ?? throw new ArgumentNullException(nameof(portName));
        }

        public string PortName { get; set; }

        public int BaudRate { get; set; } = 115200;

        public int DataBits { get; set; } = 8;

        public Parity Parity { get; set; } = Parity.None;

        public StopBits StopBits { get; set; } = StopBits.One;

        /// <summary>流控。232 三线制（TX/RX/GND）请保持 None。</summary>
        public Handshake Handshake { get; set; } = Handshake.None;

        /// <summary>
        /// 读超时（毫秒）。读取线程靠它周期性醒来看是否需要退出。
        /// 不要设为 InfiniteTimeout，否则拔掉 USB 后线程无法及时收尾。
        /// </summary>
        public int ReadTimeout { get; set; } = 500;

        /// <summary>写超时（毫秒）。</summary>
        public int WriteTimeout { get; set; } = 1000;

        public bool DtrEnable { get; set; }

        public bool RtsEnable { get; set; }

        /// <summary>枚举本机串口（USB 转串口设备会作为 COM 口出现）。</summary>
        public static string[] GetPortNames()
        {
            var names = SerialPort.GetPortNames();
            Array.Sort(names, ComparePortName);
            return names;
        }

        public override string ToString()
        {
            return string.Format("{0} {1}-{2}-{3} {4}",
                PortName, BaudRate, DataBits,
                Parity == Parity.None ? "N" : Parity.ToString().Substring(0, 1),
                StopBits == StopBits.One ? "1" : StopBits == StopBits.Two ? "2" : StopBits.ToString());
        }

        /// <summary>让 COM10 排在 COM9 之后，而不是按字符串比较。</summary>
        private static int ComparePortName(string? x, string? y)
        {
            int nx = ParsePortNumber(x);
            int ny = ParsePortNumber(y);
            if (nx >= 0 && ny >= 0) return nx.CompareTo(ny);
            return string.CompareOrdinal(x, y);
        }

        private static int ParsePortNumber(string? name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            string text = name!.Trim();
            if (!text.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return -1;
            return int.TryParse(text.Substring(3), out int number) ? number : -1;
        }
    }
}
