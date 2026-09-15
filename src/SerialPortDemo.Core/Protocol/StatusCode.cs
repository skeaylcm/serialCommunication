namespace SerialPortDemo.Protocol
{
    /// <summary>
    /// 应答帧 DATA 的第 1 个字节：状态码（0 表示成功，非 0 表示失败）。
    /// 这样从机不需要额外的告警通道，主站按状态码决定是否重试。
    /// </summary>
    public static class StatusCode
    {
        public const byte Ok = 0x00;
        public const byte UnknownCommand = 0x01;
        public const byte BadParameter = 0x02;
        public const byte AddressOutOfRange = 0x03;
        public const byte CrcError = 0x04;
        public const byte PayloadTooLarge = 0x05;

        public static string Describe(byte code)
        {
            switch (code)
            {
                case Ok: return "成功";
                case UnknownCommand: return "未知命令";
                case BadParameter: return "参数错误";
                case AddressOutOfRange: return "地址越界";
                case CrcError: return "校验错误";
                case PayloadTooLarge: return "数据过长";
                default: return "未知状态 0x" + code.ToString("X2");
            }
        }
    }
}
