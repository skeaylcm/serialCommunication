using System;
using System.Threading;
using System.Threading.Tasks;
using SerialPortDemo.Transport;

namespace SerialPortDemo.Protocol
{
    /// <summary>
    /// 上位机（主站）会话：把字节流通道 + 帧解析器组合起来，提供
    ///   - 发送请求并等待应答（一问一答，超时可控）；
    ///   - 上报所有收到的帧（含设备主动上报的帧）。
    ///
    /// 232 是点对点、半双工的，所以这里用信号量保证同一时刻只有一个未完成请求，
    /// 避免两条请求的应答互相串台。
    /// </summary>
    public sealed class ProtocolSession : IDisposable
    {
        private readonly ITransport _transport;
        private readonly FrameParser _parser = new FrameParser();
        private readonly SemaphoreSlim _requestGate = new SemaphoreSlim(1, 1);
        private TaskCompletionSource<ProtocolFrame>? _pending;
        private byte _pendingResponseCommand;
        private bool _disposed;

        public ProtocolSession(ITransport transport, TimeSpan? responseTimeout = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            ResponseTimeout = responseTimeout ?? TimeSpan.FromSeconds(1);

            _parser.FrameReceived += OnFrameReceived;
            _parser.FrameError += (sender, e) => Raise(FrameError, e);
            _transport.DataReceived += OnDataReceived;
            _transport.Error += (sender, e) => Raise(TransportError, e);
            _transport.Closed += OnTransportClosed;
        }

        /// <summary>默认应答超时。</summary>
        public TimeSpan ResponseTimeout { get; set; }

        public ITransport Transport => _transport;

        public FrameParser Parser => _parser;

        public ParserStatistics Statistics => _parser.Statistics;

        /// <summary>收到任意完整帧（含设备主动上报）。</summary>
        public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

        /// <summary>线路异常（噪声、CRC 错误）。</summary>
        public event EventHandler<FrameErrorEventArgs>? FrameError;

        /// <summary>通道异常。</summary>
        public event EventHandler<TransportErrorEventArgs>? TransportError;

        /// <summary>通道已关闭。</summary>
        public event EventHandler? Closed;

        public bool IsOpen => _transport.IsOpen;

        /// <summary>打开串口并开始接收。</summary>
        public void Start()
        {
            ThrowIfDisposed();
            if (!_transport.IsOpen)
            {
                _transport.Open();
            }
        }

        /// <summary>关闭串口。</summary>
        public void Stop()
        {
            if (_transport.IsOpen)
            {
                _transport.Close();
            }
        }

        /// <summary>发送请求并阻塞等待应答。</summary>
        public ProtocolFrame SendCommand(byte command, byte[]? payload = null, TimeSpan? timeout = null)
            => SendCommandAsync(command, payload, timeout).GetAwaiter().GetResult();

        /// <summary>发送请求并异步等待应答。</summary>
        public async Task<ProtocolFrame> SendCommandAsync(byte command, byte[]? payload = null, TimeSpan? timeout = null)
        {
            ThrowIfDisposed();

            if (CommandCode.IsResponse(command))
            {
                throw new ArgumentException("请求命令码不能带应答标志 0x80：0x" + command.ToString("X2"), nameof(command));
            }
            if (!_transport.IsOpen)
            {
                throw new InvalidOperationException("串口未打开，无法发送数据");
            }

            await _requestGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var completion = new TaskCompletionSource<ProtocolFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingResponseCommand = CommandCode.ToResponse(command);
                _pending = completion;
                try
                {
                    var bytes = new ProtocolFrame(command, payload).ToArray();
                    _transport.Write(bytes, 0, bytes.Length);

                    var wait = timeout ?? ResponseTimeout;
                    var finished = await Task.WhenAny(completion.Task, Task.Delay(wait)).ConfigureAwait(false);
                    if (finished != completion.Task)
                    {
                        throw new TimeoutException(
                            "等待应答超时（" + wait.TotalMilliseconds.ToString("F0") + " ms）：" +
                            CommandCode.Describe(command) + "，已发送 " + SerialPortDemo.Hex.ToString(bytes));
                    }

                    return await completion.Task.ConfigureAwait(false);
                }
                finally
                {
                    _pending = null;
                }
            }
            finally
            {
                _requestGate.Release();
            }
        }

        // ---------- 协议语义封装 ----------

        /// <summary>PING 握手，返回设备运行时间。</summary>
        public TimeSpan Ping(TimeSpan? timeout = null)
        {
            var response = SendCommand(CommandCode.Ping, null, timeout).EnsureSuccess();
            if (response.Payload.Length < 5)
            {
                throw new ProtocolException("PING 应答数据长度不足：期望 5 字节，实际 " + response.Payload.Length + " 字节");
            }
            uint milliseconds = (uint)(response.Payload[1]
                                      | (response.Payload[2] << 8)
                                      | (response.Payload[3] << 16)
                                      | (response.Payload[4] << 24));
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        /// <summary>读保持寄存器。</summary>
        public ushort[] ReadHoldingRegisters(ushort startAddress, byte count, TimeSpan? timeout = null)
        {
            if (count == 0) throw new ArgumentOutOfRangeException(nameof(count), "读取个数必须大于 0");

            var payload = new byte[3];
            payload[0] = (byte)(startAddress & 0xFF);
            payload[1] = (byte)((startAddress >> 8) & 0xFF);
            payload[2] = count;

            var response = SendCommand(CommandCode.ReadHoldingRegisters, payload, timeout).EnsureSuccess();
            if (response.Payload.Length != 1 + count * 2)
            {
                throw new ProtocolException("READ 应答数据长度不符：期望 " + (1 + count * 2) + " 字节，实际 " + response.Payload.Length + " 字节");
            }

            var values = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = (ushort)(response.Payload[1 + i * 2] | (response.Payload[2 + i * 2] << 8));
            }
            return values;
        }

        /// <summary>写单个寄存器。</summary>
        public void WriteRegister(ushort address, ushort value, TimeSpan? timeout = null)
        {
            var payload = new byte[4];
            payload[0] = (byte)(address & 0xFF);
            payload[1] = (byte)((address >> 8) & 0xFF);
            payload[2] = (byte)(value & 0xFF);
            payload[3] = (byte)((value >> 8) & 0xFF);

            SendCommand(CommandCode.WriteRegister, payload, timeout).EnsureSuccess();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _parser.FrameReceived -= OnFrameReceived;
            _transport.DataReceived -= OnDataReceived;
            _transport.Closed -= OnTransportClosed;

            var pending = _pending;
            if (pending != null)
            {
                pending.TrySetException(new ObjectDisposedException(nameof(ProtocolSession), "会话已释放，请求被取消"));
            }

            _requestGate.Dispose();
        }

        private void OnDataReceived(object? sender, TransportDataEventArgs e)
        {
            // 通道收到的是原始字节流，交给解析器拼帧
            _parser.Append(e.Data, 0, e.Data.Length);
        }

        private void OnFrameReceived(object? sender, FrameReceivedEventArgs e)
        {
            Raise(FrameReceived, e);

            var pending = _pending;
            if (pending != null && e.Frame.Command == _pendingResponseCommand)
            {
                pending.TrySetResult(e.Frame);
            }
        }

        private void OnTransportClosed(object? sender, EventArgs e)
        {
            var pending = _pending;
            if (pending != null)
            {
                pending.TrySetException(new TransportClosedException("串口已关闭，等待中的请求被中断"));
            }

            var handler = Closed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void Raise<T>(EventHandler<T>? handler, T args) where T : EventArgs
        {
            if (handler != null) handler(this, args);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ProtocolSession));
        }
    }
}
