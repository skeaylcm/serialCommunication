using System;

namespace SerialPortDemo.Transport
{
    /// <summary>
    /// 字节流通道抽象。上层协议只依赖它，因此可以：
    ///   - 用 <see cref="SerialTransport"/> 走真实 USB 转串口；
    ///   - 用 <c>VirtualEndpoint</c> 在内存中做无硬件自测。
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>通道名（串口名，如 COM3）。</summary>
        string Name { get; }

        bool IsOpen { get; }

        /// <summary>收到数据（可能在任意后台线程触发）。</summary>
        event EventHandler<TransportDataEventArgs>? DataReceived;

        /// <summary>数据已发出。</summary>
        event EventHandler<TransportDataEventArgs>? DataSent;

        /// <summary>通道异常（读取失败、设备被拔出等）。</summary>
        event EventHandler<TransportErrorEventArgs>? Error;

        /// <summary>通道已关闭。</summary>
        event EventHandler? Closed;

        void Open();

        void Close();

        /// <summary>发送数据（同步阻塞，直到写入驱动缓冲）。</summary>
        void Write(byte[] buffer, int offset, int count);
    }
}
