using System;
using SerialPortDemo.Protocol;
using SerialPortDemo.Transport;

namespace SerialPortDemo.Device
{
    /// <summary>
    /// 下位机（从站）协议实现：收到请求帧后按命令码执行，并回一个应答帧。
    ///
    /// 它可以挂在任何 <see cref="ITransport"/> 上：
    ///   - 挂在虚拟链路的从端 → 上位机软件联调（无硬件）；
    ///   - 挂在真实串口 → 直接跑在设备侧（.NET Micro Framework / WinCE / Windows 工控机都可用）。
    /// </summary>
    public sealed class ProtocolDevice : IDisposable
    {
        private readonly ITransport _transport;
        private readonly FrameParser _parser = new FrameParser();
        private readonly RegisterFile _registers;
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private bool _disposed;

        public ProtocolDevice(ITransport transport, RegisterFile? registers = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _registers = registers ?? new RegisterFile();

            _parser.FrameReceived += OnFrameReceived;
            _parser.FrameError += (sender, e) => Raise(FrameError, e);
            _transport.DataReceived += OnDataReceived;
        }

        /// <summary>设备里的寄存器区，宿主程序可直接读写。</summary>
        public RegisterFile Registers => _registers;

        public ParserStatistics Statistics => _parser.Statistics;

        /// <summary>收到请求帧（便于日志/监控）。</summary>
        public event EventHandler<FrameReceivedEventArgs>? RequestReceived;

        /// <summary>已发送应答帧。</summary>
        public event EventHandler<FrameReceivedEventArgs>? ResponseSent;

        /// <summary>线路/帧格式异常。</summary>
        public event EventHandler<FrameErrorEventArgs>? FrameError;

        /// <summary>设备内部处理异常。</summary>
        public event EventHandler<TransportErrorEventArgs>? DeviceError;

        /// <summary>设备运行时间。</summary>
        public TimeSpan Uptime => DateTime.UtcNow - _startedAt;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _parser.FrameReceived -= OnFrameReceived;
            _transport.DataReceived -= OnDataReceived;
        }

        private void OnDataReceived(object? sender, TransportDataEventArgs e)
        {
            _parser.Append(e.Data, 0, e.Data.Length);
        }

        private void OnFrameReceived(object? sender, FrameReceivedEventArgs e)
        {
            var request = e.Frame;
            if (request.IsResponse)
            {
                // 从站只处理请求帧，忽略总线上的应答帧
                return;
            }

            Raise(RequestReceived, e);

            ProtocolFrame response;
            try
            {
                response = BuildResponse(request);
            }
            catch (Exception ex)
            {
                Raise(DeviceError, new TransportErrorEventArgs("处理请求时发生异常：" + ex.Message, ex));
                response = Error(request.Command, StatusCode.BadParameter);
            }

            try
            {
                var bytes = response.ToArray();
                _transport.Write(bytes, 0, bytes.Length);
                Raise(ResponseSent, new FrameReceivedEventArgs(response));
            }
            catch (Exception ex)
            {
                Raise(DeviceError, new TransportErrorEventArgs("发送应答失败：" + ex.Message, ex));
            }
        }

        private ProtocolFrame BuildResponse(ProtocolFrame request)
        {
            switch (request.Command)
            {
                case CommandCode.Ping:
                {
                    // 应答数据：[状态(1) 运行时间毫秒(4, 小端)]
                    var payload = new byte[5];
                    payload[0] = StatusCode.Ok;
                    uint uptime = (uint)Uptime.TotalMilliseconds;
                    payload[1] = (byte)(uptime & 0xFF);
                    payload[2] = (byte)((uptime >> 8) & 0xFF);
                    payload[3] = (byte)((uptime >> 16) & 0xFF);
                    payload[4] = (byte)((uptime >> 24) & 0xFF);
                    return new ProtocolFrame(CommandCode.ToResponse(request.Command), payload);
                }

                case CommandCode.ReadHoldingRegisters:
                {
                    if (request.Payload.Length != 3)
                    {
                        return Error(request.Command, StatusCode.BadParameter);
                    }

                    ushort address = (ushort)(request.Payload[0] | (request.Payload[1] << 8));
                    ushort count = request.Payload[2];
                    if (count == 0 || count > 125)
                    {
                        return Error(request.Command, StatusCode.BadParameter);
                    }

                    if (!_registers.TryRead(address, count, out var values))
                    {
                        return Error(request.Command, StatusCode.AddressOutOfRange);
                    }

                    var payload = new byte[1 + count * 2];
                    payload[0] = StatusCode.Ok;
                    for (int i = 0; i < count; i++)
                    {
                        payload[1 + i * 2] = (byte)(values[i] & 0xFF);
                        payload[2 + i * 2] = (byte)((values[i] >> 8) & 0xFF);
                    }
                    return new ProtocolFrame(CommandCode.ToResponse(request.Command), payload);
                }

                case CommandCode.WriteRegister:
                {
                    if (request.Payload.Length != 4)
                    {
                        return Error(request.Command, StatusCode.BadParameter);
                    }

                    ushort address = (ushort)(request.Payload[0] | (request.Payload[1] << 8));
                    ushort value = (ushort)(request.Payload[2] | (request.Payload[3] << 8));
                    if (!_registers.TryWrite(address, value))
                    {
                        return Error(request.Command, StatusCode.AddressOutOfRange);
                    }

                    return new ProtocolFrame(CommandCode.ToResponse(request.Command), new byte[] { StatusCode.Ok });
                }

                default:
                    return Error(request.Command, StatusCode.UnknownCommand);
            }
        }

        private static ProtocolFrame Error(byte requestCommand, byte status)
        {
            return new ProtocolFrame(CommandCode.ToResponse(requestCommand), new byte[] { status });
        }

        private void Raise<T>(EventHandler<T>? handler, T args) where T : EventArgs
        {
            if (handler != null) handler(this, args);
        }
    }
}
