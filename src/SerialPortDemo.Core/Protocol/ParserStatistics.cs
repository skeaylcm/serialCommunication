using System;

namespace SerialPortDemo.Protocol
{
    /// <summary>解析统计，用于评估链路质量。</summary>
    public sealed class ParserStatistics
    {
        public long BytesReceived { get; private set; }

        public long FramesReceived { get; private set; }

        /// <summary>CRC 校验失败次数。</summary>
        public long CrcErrors { get; private set; }

        /// <summary>帧格式错误次数（帧尾错误、长度错误等）。</summary>
        public long FormatErrors { get; private set; }

        /// <summary>为了重新同步而丢弃的字节数（线路噪声/波特率不匹配的表现）。</summary>
        public long ResynchronizedBytes { get; private set; }

        internal void AddBytes(int count) => BytesReceived += count;

        internal void AddFrame() => FramesReceived++;

        internal void AddError(FrameDecodeError error)
        {
            if (error == FrameDecodeError.CrcMismatch) CrcErrors++;
            else FormatErrors++;
        }

        internal void AddResynchronizedBytes(int count) => ResynchronizedBytes += count;

        public void Reset()
        {
            BytesReceived = 0;
            FramesReceived = 0;
            CrcErrors = 0;
            FormatErrors = 0;
            ResynchronizedBytes = 0;
        }

        public override string ToString()
        {
            return string.Format("接收 {0} 字节 / {1} 帧；CRC 错误 {2} 次；格式错误 {3} 次；丢弃噪声 {4} 字节",
                BytesReceived, FramesReceived, CrcErrors, FormatErrors, ResynchronizedBytes);
        }
    }
}
