using System;

namespace SerialPortDemo.Transport
{
    /// <summary>
    /// 通道在请求过程中被关闭（例如用户点了“关闭串口”，或 USB 转串口被拔出）。
    /// 收到该异常时应提示用户重新打开端口，而不是当作协议错误处理。
    /// </summary>
    public sealed class TransportClosedException : Exception
    {
        public TransportClosedException(string message) : base(message)
        {
        }

        public TransportClosedException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
