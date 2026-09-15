using System;
using System.Collections.Concurrent;
using System.Threading;
using SerialPortDemo.Transport;

namespace SerialPortDemo.Simulation
{
    /// <summary>
    /// 虚拟串口链路：在内存里模拟一根 RS-232 线缆，把“上位机端”和“下位机端”对接起来。
    ///
    /// 用途：在没有 USB 转串口设备 / 板卡的情况下调试协议层（联调、回归测试、CI）。
    /// 由于两端各有独立的投递线程并带一点延时，行为与真实串口接近：
    ///   - 收数据是异步的，不会在 Write 里同步回调；
    ///   - 一次投递的字节数与写入时一致（真实串口会按驱动缓冲重新分片）。
    /// </summary>
    public sealed class VirtualSerialLink : IDisposable
    {
        private readonly VirtualEndpoint _master;
        private readonly VirtualEndpoint _slave;
        private bool _disposed;

        private VirtualSerialLink(VirtualEndpoint master, VirtualEndpoint slave)
        {
            _master = master;
            _slave = slave;
            master.Peer = slave;
            slave.Peer = master;
            master.Start();
            slave.Start();
        }

        /// <summary>上位机端（配合 ProtocolSession 使用）。</summary>
        public ITransport Master => _master;

        /// <summary>下位机端（配合 ProtocolDevice 使用）。</summary>
        public ITransport Slave => _slave;

        /// <summary>创建一条虚拟链路。</summary>
        /// <param name="latencyMilliseconds">每段传输的模拟延时，默认 1 ms。</param>
        public static VirtualSerialLink Create(int latencyMilliseconds = 1)
        {
            return new VirtualSerialLink(
                new VirtualEndpoint("VIRTUAL-COM1(上位机)", latencyMilliseconds),
                new VirtualEndpoint("VIRTUAL-COM2(下位机)", latencyMilliseconds));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _master.Dispose();
            _slave.Dispose();
        }

        internal sealed class VirtualEndpoint : ITransport
        {
            private readonly BlockingCollection<byte[]> _inbox = new BlockingCollection<byte[]>();
            private readonly int _latencyMilliseconds;
            private Thread? _worker;
            private volatile bool _open = true;
            private bool _disposed;

            public VirtualEndpoint(string name, int latencyMilliseconds)
            {
                Name = name;
                _latencyMilliseconds = latencyMilliseconds < 0 ? 0 : latencyMilliseconds;
            }

            /// <summary>对端端点，由链路注入。</summary>
            public VirtualEndpoint? Peer { get; set; }

            public string Name { get; }

            public bool IsOpen => _open;

            public event EventHandler<TransportDataEventArgs>? DataReceived;
            public event EventHandler<TransportDataEventArgs>? DataSent;
            public event EventHandler<TransportErrorEventArgs>? Error;
            public event EventHandler? Closed;

            internal void Start()
            {
                var thread = new Thread(Worker)
                {
                    IsBackground = true,
                    Name = "VirtualSerial(" + Name + ")",
                };
                _worker = thread;
                thread.Start();
            }

            public void Open()
            {
                if (_disposed) throw new ObjectDisposedException(nameof(VirtualEndpoint));
                _open = true;
            }

            public void Close()
            {
                if (!_open) return;
                _open = false;
                try
                {
                    _inbox.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                    // 已释放
                }

                var handler = Closed;
                if (handler != null) handler(this, EventArgs.Empty);
            }

            public void Write(byte[] buffer, int offset, int count)
            {
                if (!_open) throw new InvalidOperationException("虚拟串口 " + Name + " 未打开，无法发送数据");
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));

                var peer = Peer;
                if (peer == null) throw new InvalidOperationException("虚拟串口 " + Name + " 未连线");

                var data = new byte[count];
                Buffer.BlockCopy(buffer, offset, data, 0, count);

                var sentHandler = DataSent;
                if (sentHandler != null) sentHandler(this, new TransportDataEventArgs(data));

                peer.Enqueue(data);
            }

            internal void Enqueue(byte[] data)
            {
                if (!_open) return;
                try
                {
                    _inbox.Add(data);
                }
                catch (Exception)
                {
                    // 对端已关闭，丢弃
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Close();

                var worker = _worker;
                if (worker != null && worker != Thread.CurrentThread)
                {
                    worker.Join(500);
                }
                _inbox.Dispose();
            }

            private void Worker()
            {
                foreach (var data in _inbox.GetConsumingEnumerable())
                {
                    if (_latencyMilliseconds > 0) Thread.Sleep(_latencyMilliseconds);
                    if (!_open) continue;

                    var handler = DataReceived;
                    if (handler == null) continue;

                    try
                    {
                        handler(this, new TransportDataEventArgs(data));
                    }
                    catch (Exception ex)
                    {
                        var errorHandler = Error;
                        if (errorHandler != null)
                        {
                            errorHandler(this, new TransportErrorEventArgs("虚拟串口回调异常：" + ex.Message, ex));
                        }
                    }
                }
            }
        }
    }
}
