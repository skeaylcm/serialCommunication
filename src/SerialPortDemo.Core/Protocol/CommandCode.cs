namespace SerialPortDemo.Protocol
{
    /// <summary>
    /// 命令码定义。约定：最高位 0x80 为应答标志，
    /// 应答帧的命令码 = 请求帧命令码 | 0x80。
    /// </summary>
    public static class CommandCode
    {
        /// <summary>握手：读设备运行时间。</summary>
        public const byte Ping = 0x01;

        /// <summary>读保持寄存器：请求 [起始地址(2) 个数(1)]，应答 [状态(1) 数据(2*个数)]。</summary>
        public const byte ReadHoldingRegisters = 0x02;

        /// <summary>写单个寄存器：请求 [地址(2) 数值(2)]，应答 [状态(1)]。</summary>
        public const byte WriteRegister = 0x03;

        /// <summary>应答标志位。</summary>
        public const byte ResponseFlag = 0x80;

        public static byte ToResponse(byte requestCommand) => (byte)(requestCommand | ResponseFlag);

        public static byte ToRequest(byte responseCommand) => (byte)(responseCommand & ~ResponseFlag);

        public static bool IsResponse(byte command) => (command & ResponseFlag) != 0;

        public static string Describe(byte command)
        {
            switch (ToRequest(command))
            {
                case Ping: return IsResponse(command) ? "PING-ACK" : "PING";
                case ReadHoldingRegisters: return IsResponse(command) ? "READ-ACK" : "READ";
                case WriteRegister: return IsResponse(command) ? "WRITE-ACK" : "WRITE";
                default: return "CMD-0x" + command.ToString("X2");
            }
        }
    }
}
