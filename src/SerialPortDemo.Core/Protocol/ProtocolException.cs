using System;

namespace SerialPortDemo.Protocol
{
    /// <summary>协议层异常：帧格式错误、设备返回错误状态码等。</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message)
        {
        }

        public ProtocolException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public ProtocolException(byte statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }

        /// <summary>设备返回的状态码（非协议层错误时为 null）。</summary>
        public byte? StatusCode { get; }
    }

    /// <summary>收到一个完整帧。</summary>
    public sealed class FrameReceivedEventArgs : EventArgs
    {
        public FrameReceivedEventArgs(ProtocolFrame frame)
        {
            Frame = frame ?? throw new ArgumentNullException(nameof(frame));
            Timestamp = DateTime.Now;
        }

        public ProtocolFrame Frame { get; }

        public DateTime Timestamp { get; }
    }

    /// <summary>解析过程中发现异常（CRC 错误、噪声字节等）。</summary>
    public sealed class FrameErrorEventArgs : EventArgs
    {
        public FrameErrorEventArgs(FrameDecodeError error, string message, byte[] rawBytes)
        {
            Error = error;
            Message = message;
            RawBytes = rawBytes ?? new byte[0];
            Timestamp = DateTime.Now;
        }

        public FrameDecodeError Error { get; }

        public string Message { get; }

        /// <summary>导致该错误的原始字节。</summary>
        public byte[] RawBytes { get; }

        public DateTime Timestamp { get; }

        /// <summary>是否只是丢弃了噪声字节（用于重新同步），不需要报警。</summary>
        public bool IsResynchronization => Error == FrameDecodeError.BadStartOfFrame;

        public string RawHex => SerialPortDemo.Hex.ToString(RawBytes);

        public override string ToString() => Message + (RawBytes.Length > 0 ? " | " + RawHex : string.Empty);
    }
}
