using System;

namespace SerialPortDemo.Transport
{
    /// <summary>收发数据事件。</summary>
    public sealed class TransportDataEventArgs : EventArgs
    {
        public TransportDataEventArgs(byte[] data)
        {
            Data = data ?? new byte[0];
            Timestamp = DateTime.Now;
        }

        /// <summary>本次读/写的原始字节（事件参数持有该数组的所有权）。</summary>
        public byte[] Data { get; }

        public DateTime Timestamp { get; }

        public string Hex => SerialPortDemo.Hex.ToString(Data);

        public override string ToString() => Data.Length + " 字节 | " + Hex;
    }

    /// <summary>通道/设备错误事件。</summary>
    public sealed class TransportErrorEventArgs : EventArgs
    {
        public TransportErrorEventArgs(string message, Exception? exception = null)
        {
            Message = message;
            Exception = exception;
            Timestamp = DateTime.Now;
        }

        public string Message { get; }

        public Exception? Exception { get; }

        public DateTime Timestamp { get; }

        public override string ToString() => Message;
    }
}
