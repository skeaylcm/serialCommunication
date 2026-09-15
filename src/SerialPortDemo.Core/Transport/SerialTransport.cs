using System;
using System.IO;
using System.IO.Ports;
using System.Threading;

namespace SerialPortDemo.Transport
{
    /// <summary>
    /// 基于 System.IO.Ports.SerialPort 的通道实现。
    ///
    /// 设计要点：
    ///   1. 用独立后台线程 + BaseStream.Read 收数据，不用 DataReceived 事件。
    ///      SerialPort.DataReceived 的触发时机和分片方式由驱动决定，容易让人误以为“一次事件=一帧”；
    ///      自己收字节再交给 <c>FrameParser</c> 才可靠。
    ///   2. 读线程里拿到的是原始字节数组，直接抛给协议层解析。
    ///   3. USB 转串口被拔掉时读操作会抛 IOException/ObjectDisposedException，
    ///      这里转成 Error 事件并主动收尾，上层可据此提示“设备已断开”。
    /// </summary>
    public sealed class SerialTransport : ITransport
    {
        private readonly SerialPortSettings _settings;
        private readonly object _sync = new object();
        private readonly object _writeLock = new object();
        private SerialPort? _port;
        private Thread? _readThread;
        private volatile bool _running;
        private bool _disposed;
        private long _bytesSent;
        private long _bytesReceived;

        public SerialTransport(SerialPortSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.PortName))
            {
                throw new ArgumentException("必须指定串口名（例如 COM3）", nameof(settings));
            }
        }

        public SerialPortSettings Settings => _settings;

        public string Name => _settings.PortName;

        public bool IsOpen
        {
            get
            {
                var port = _port;
                try
                {
                    return port != null && port.IsOpen;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        /// <summary>累计发送字节数。</summary>
        public long BytesSent => Interlocked.Read(ref _bytesSent);

        /// <summary>累计接收字节数。</summary>
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        public event EventHandler<TransportDataEventArgs>? DataReceived;
        public event EventHandler<TransportDataEventArgs>? DataSent;
        public event EventHandler<TransportErrorEventArgs>? Error;
        public event EventHandler? Closed;

        public void Open()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (IsOpen) return;

                var port = new SerialPort(
                    _settings.PortName,
                    _settings.BaudRate,
                    _settings.Parity,
                    _settings.DataBits,
                    _settings.StopBits)
                {
                    Handshake = _settings.Handshake,
                    ReadTimeout = _settings.ReadTimeout,
                    WriteTimeout = _settings.WriteTimeout,
                    DtrEnable = _settings.DtrEnable,
                    RtsEnable = _settings.RtsEnable,
                };

                try
                {
                    port.Open();
                }
                catch (Exception ex)
                {
                    port.Dispose();
                    throw new IOException(
                        "打开 " + _settings.PortName + " 失败：" + ex.Message +
                        "（请检查 USB 转串口设备是否插好、驱动是否安装、端口是否被其它程序占用）", ex);
                }

                try
                {
                    port.DiscardInBuffer();
                    port.DiscardOutBuffer();
                }
                catch (Exception)
                {
                    // 部分虚拟串口驱动不支持 Discard，忽略即可
                }

                _port = port;
                _running = true;

                var thread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "SerialPortDemo.Read(" + _settings.PortName + ")",
                };
                _readThread = thread;
                thread.Start();
            }
        }

        public void Close()
        {
            SerialPort? port;
            Thread? thread;
            lock (_sync)
            {
                port = _port;
                thread = _readThread;
                _port = null;
                _readThread = null;
                _running = false;
            }

            if (port == null) return;

            try
            {
                port.Close();
            }
            catch (Exception)
            {
                // 设备已被拔出时会抛异常，忽略
            }

            // 注意：若是在读线程回调里调用 Close()，不能 Join 自己
            if (thread != null && thread != Thread.CurrentThread)
            {
                try
                {
                    thread.Join(500);
                }
                catch (Exception)
                {
                    // 忽略
                }
            }

            try
            {
                port.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }

            var handler = Closed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));

            var port = _port;
            if (port == null || !IsOpen)
            {
                throw new InvalidOperationException("串口 " + Name + " 未打开，无法发送数据");
            }

            lock (_writeLock)
            {
                port.Write(buffer, offset, count);
            }

            Interlocked.Add(ref _bytesSent, count);

            var handler = DataSent;
            if (handler != null)
            {
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                handler(this, new TransportDataEventArgs(copy));
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Close();
        }

        private void ReadLoop()
        {
            var buffer = new byte[4096];

            while (_running)
            {
                var port = _port;
                if (port == null) break;

                int read;
                try
                {
                    // ReadTimeout 到期会抛 TimeoutException，正好用来周期性检查退出标志
                    read = port.BaseStream.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    // 端口在读取过程中被关闭
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        RaiseError("串口读取失败：" + ex.Message +
                                   "（USB 转串口设备可能已被拔出）", ex);
                        HandlePortLost();
                    }
                    break;
                }

                if (read <= 0) continue;

                Interlocked.Add(ref _bytesReceived, read);

                var handler = DataReceived;
                if (handler == null) continue;

                var data = new byte[read];
                Buffer.BlockCopy(buffer, 0, data, 0, read);
                try
                {
                    handler(this, new TransportDataEventArgs(data));
                }
                catch (Exception ex)
                {
                    // 上层处理数据时抛出异常，不能拖垮读线程
                    RaiseError("处理接收数据时发生异常：" + ex.Message, ex);
                }
            }
        }

        /// <summary>设备消失（USB 被拔出）时的收尾：不做线程 Join，避免读线程 Join 自己。</summary>
        private void HandlePortLost()
        {
            SerialPort? port;
            lock (_sync)
            {
                port = _port;
                _port = null;
                _running = false;
            }

            try
            {
                port?.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }

            var handler = Closed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseError(string message, Exception exception)
        {
            var handler = Error;
            if (handler != null)
            {
                handler(this, new TransportErrorEventArgs(message, exception));
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SerialTransport));
        }
    }
}
