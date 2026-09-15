using System;
using System.Globalization;
using SerialPortDemo.Device;
using SerialPortDemo.Protocol;
using SerialPortDemo.Simulation;
using SerialPortDemo.Transport;
using SerialPortDemo.Usb;

namespace SerialPortDemo.Demo
{
    /// <summary>
    /// 交互式上位机 Demo：用菜单完成“选端口 → 打开 → 发命令 → 看应答”。
    /// </summary>
    internal sealed class SerialConsole : IDisposable
    {
        private ProtocolSession? _session;
        private VirtualSerialLink? _virtualLink;
        private ProtocolDevice? _virtualDevice;
        private bool _showRaw = true;
        private bool _disposed;

        // ---------- 菜单 ----------

        public int RunMenu()
        {
            Console.WriteLine("RS-232 串口通讯协议 Demo（C# / .NET Framework 4.8）");
            Console.WriteLine("USB 转串口设备（CH340 / CP2102 / FT232 / PL2303 …）插入后会作为 COM 口出现。");
            Console.WriteLine();

            while (true)
            {
                PrintStatus();
                PrintMenu();
                Console.Write("请选择: ");

                string? input = Console.ReadLine();
                if (input == null) return 0; // 输入被重定向或 Ctrl+Z

                Console.WriteLine();
                switch (input.Trim())
                {
                    case "0": return 0;
                    case "1": ListPorts(); break;
                    case "2": OpenRealPort(); break;
                    case "3": OpenVirtualLink(); break;
                    case "4": CloseCurrent(); break;
                    case "5": DoPing(); break;
                    case "6": DoReadRegisters(); break;
                    case "7": DoWriteRegister(); break;
                    case "8": DoSendCustomFrame(); break;
                    case "9": DoSendRawHex(); break;
                    case "10": ShowStatistics(); break;
                    case "11":
                        _showRaw = !_showRaw;
                        ApplyRawDisplay();
                        Console.WriteLine("原始报文显示已" + (_showRaw ? "打开" : "关闭"));
                        break;
                    case "12": RunHardwareLoopback(); break;
                    case "13": SelfTest.Run(); break;
                    default: Console.WriteLine("无效的选择：请输入 0 ~ 13"); break;
                }
                Console.WriteLine();
            }
        }

        private void PrintStatus()
        {
            if (_session != null && _session.IsOpen)
            {
                if (_session.Transport is SerialTransport serial)
                {
                    Console.WriteLine("当前状态：已连接 " + serial.Settings + "  " + Describe(serial.Name));
                }
                else
                {
                    Console.WriteLine("当前状态：已连接虚拟链路（VIRTUAL-COM1 上位机 <-> VIRTUAL-COM2 下位机）");
                }
            }
            else
            {
                Console.WriteLine("当前状态：未连接");
            }
        }

        private static void PrintMenu()
        {
            Console.WriteLine("┌── 连接 ──────────────────────────────────────");
            Console.WriteLine("│  1. 列出串口 / USB 串口设备");
            Console.WriteLine("│  2. 打开串口（真实 USB 转串口）");
            Console.WriteLine("│  3. 打开虚拟链路（无需硬件，内置虚拟下位机）");
            Console.WriteLine("│  4. 关闭");
            Console.WriteLine("├── 协议操作 ──────────────────────────────────");
            Console.WriteLine("│  5. PING（读设备运行时间）");
            Console.WriteLine("│  6. 读保持寄存器");
            Console.WriteLine("│  7. 写单个寄存器");
            Console.WriteLine("│  8. 按协议发送自定义帧（命令码 + 数据）");
            Console.WriteLine("│  9. 发送原始 HEX 字节（不经协议层）");
            Console.WriteLine("├── 其它 ──────────────────────────────────────");
            Console.WriteLine("│ 10. 显示收发统计");
            Console.WriteLine("│ 11. 切换原始报文显示");
            Console.WriteLine("│ 12. 硬件回环测试（需短接 TX/RX）");
            Console.WriteLine("│ 13. 协议自检（虚拟链路）");
            Console.WriteLine("└  0. 退出");
        }

        // ---------- 连接管理 ----------

        private void ListPorts()
        {
            Console.WriteLine("串口列表：");
            var devices = UsbSerialDeviceEnumerator.Enumerate();
            if (devices.Count > 0)
            {
                foreach (var device in devices)
                {
                    Console.WriteLine("  " + device);
                    if (device.IsUsbDevice)
                    {
                        Console.WriteLine("        " + device.DeviceId
                                          + (string.IsNullOrEmpty(device.Manufacturer) ? string.Empty : "  [" + device.Manufacturer + "]"));
                    }
                }
            }
            else
            {
                var ports = SerialPortSettings.GetPortNames();
                if (ports.Length == 0)
                {
                    Console.WriteLine("  未发现任何串口。请插入 USB 转串口设备并确认驱动已安装。");
                }
                foreach (var port in ports)
                {
                    Console.WriteLine("  " + port);
                }
            }
        }

        private void OpenRealPort()
        {
            var ports = SerialPortSettings.GetPortNames();
            if (ports.Length == 0)
            {
                Console.WriteLine("未发现任何串口。请插入 USB 转串口设备并确认驱动已安装。");
                return;
            }

            Console.WriteLine("可用串口：");
            foreach (var port in ports)
            {
                Console.WriteLine("  " + Describe(port));
            }

            Console.Write("串口名（默认 " + ports[0] + "）: ");
            string name = (Console.ReadLine() ?? string.Empty).Trim();
            if (name.Length == 0) name = ports[0];

            Console.Write("波特率（默认 115200）: ");
            string baudText = (Console.ReadLine() ?? string.Empty).Trim();
            int baudRate = 115200;
            if (baudText.Length > 0 && !int.TryParse(baudText, out baudRate))
            {
                Console.WriteLine("波特率无效，使用默认 115200。");
                baudRate = 115200;
            }

            try
            {
                CloseCurrent();
                ConnectCore(new SerialTransport(new SerialPortSettings(name) { BaudRate = baudRate }));
                Console.WriteLine("已打开 " + name + "  " + Describe(name));
            }
            catch (Exception ex)
            {
                CloseCurrent();
                Log.Error(ex.Message);
            }
        }

        private void OpenVirtualLink()
        {
            CloseCurrent();

            _virtualLink = VirtualSerialLink.Create(1);
            _virtualDevice = new ProtocolDevice(_virtualLink.Slave, new RegisterFile(256));

            // 预置几个寄存器，方便观察读命令的效果
            _virtualDevice.Registers.TryWrite(0x0000, 0x1234);
            _virtualDevice.Registers.TryWrite(0x0001, 0x5678);
            _virtualDevice.Registers.TryWrite(0x0002, 0x0001);

            ConnectCore(_virtualLink.Master);
            Console.WriteLine("已建立虚拟链路：上位机 VIRTUAL-COM1 <-> 下位机 VIRTUAL-COM2（寄存器 0x0000=0x1234，0x0001=0x5678，0x0002=0x0001）");
        }

        /// <summary>根据 _showRaw 订阅/取消订阅“原始字节”日志。</summary>
        private void ApplyRawDisplay()
        {
            if (_session == null) return;

            var transport = _session.Transport;
            transport.DataSent -= OnRawSent;
            transport.DataReceived -= OnRawReceived;
            if (_showRaw)
            {
                transport.DataSent += OnRawSent;
                transport.DataReceived += OnRawReceived;
            }
        }

        /// <summary>建立会话并开始收发（调用前需先 CloseCurrent）。</summary>
        private void ConnectCore(ITransport transport)
        {
            var session = new ProtocolSession(transport, TimeSpan.FromSeconds(1));
            _session = session;

            session.FrameReceived += OnFrameReceived;
            session.FrameError += OnFrameError;
            session.TransportError += OnSessionTransportError;
            session.Closed += OnSessionClosed;

            if (_showRaw)
            {
                transport.DataSent += OnRawSent;
                transport.DataReceived += OnRawReceived;
            }

            session.Start();
        }

        private void CloseCurrent()
        {
            if (_session != null)
            {
                var transport = _session.Transport;
                transport.DataSent -= OnRawSent;
                transport.DataReceived -= OnRawReceived;
                _session.FrameReceived -= OnFrameReceived;
                _session.FrameError -= OnFrameError;
                _session.TransportError -= OnSessionTransportError;
                _session.Closed -= OnSessionClosed;
                _session.Stop();
                _session.Dispose();
                transport.Dispose();
                _session = null;
            }

            if (_virtualDevice != null)
            {
                _virtualDevice.Dispose();
                _virtualDevice = null;
            }
            if (_virtualLink != null)
            {
                _virtualLink.Dispose();
                _virtualLink = null;
            }
        }

        // ---------- 协议操作 ----------

        private bool RequireSession()
        {
            if (_session != null && _session.IsOpen) return true;
            Console.WriteLine("请先打开串口（菜单 2 或 3）。");
            return false;
        }

        private void DoPing()
        {
            if (!RequireSession()) return;

            try
            {
                var uptime = _session!.Ping();
                Console.WriteLine(string.Format("PING 成功，设备运行时间 {0:F3} 秒", uptime.TotalSeconds));
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
            }
        }

        private void DoReadRegisters()
        {
            if (!RequireSession()) return;

            Console.Write("起始地址（HEX，如 0000）: ");
            if (!TryReadHex(Console.ReadLine(), out int address) || address > 0xFFFF)
            {
                Console.WriteLine("地址无效。");
                return;
            }

            Console.Write("读取个数（默认 1）: ");
            string countText = (Console.ReadLine() ?? string.Empty).Trim();
            int count = 1;
            if (countText.Length > 0 && !int.TryParse(countText, out count))
            {
                Console.WriteLine("个数无效。");
                return;
            }
            if (count < 1 || count > 125)
            {
                Console.WriteLine("个数必须在 1 ~ 125 之间。");
                return;
            }

            try
            {
                var values = _session!.ReadHoldingRegisters((ushort)address, (byte)count);
                for (int i = 0; i < values.Length; i++)
                {
                    Console.WriteLine(string.Format("  [{0}] = 0x{1:X4}  ({1})", (address + i).ToString("X4"), values[i]));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
            }
        }

        private void DoWriteRegister()
        {
            if (!RequireSession()) return;

            Console.Write("寄存器地址（HEX，如 0000）: ");
            if (!TryReadHex(Console.ReadLine(), out int address) || address > 0xFFFF)
            {
                Console.WriteLine("地址无效。");
                return;
            }

            Console.Write("数值（HEX，如 1234）: ");
            if (!TryReadHex(Console.ReadLine(), out int value) || value > 0xFFFF)
            {
                Console.WriteLine("数值无效。");
                return;
            }

            try
            {
                _session!.WriteRegister((ushort)address, (ushort)value);
                Console.WriteLine(string.Format("写入成功：寄存器 [{0}] = 0x{1:X4}", address.ToString("X4"), value));
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
            }
        }

        private void DoSendCustomFrame()
        {
            if (!RequireSession()) return;

            Console.Write("命令码（HEX，如 01）: ");
            string? commandText = Console.ReadLine();
            if (commandText == null || !byte.TryParse(commandText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte command))
            {
                Console.WriteLine("命令码无效。");
                return;
            }

            Console.Write("数据域（HEX，可留空，如 00 00 02）: ");
            string? payloadText = Console.ReadLine();
            byte[] payload;
            if (string.IsNullOrWhiteSpace(payloadText))
            {
                payload = new byte[0];
            }
            else if (!Hex.TryParse(payloadText!, out payload, out string? error))
            {
                Console.WriteLine("数据域无效：" + error);
                return;
            }

            var frame = new ProtocolFrame(command, payload);
            Console.WriteLine("待发送帧：" + frame);
            Console.WriteLine("           " + frame.ToHexString());

            try
            {
                if (frame.IsRequest)
                {
                    var response = _session!.SendCommand(command, payload);
                    Console.WriteLine("应答：" + response);
                }
                else
                {
                    var bytes = frame.ToArray();
                    _session!.Transport.Write(bytes, 0, bytes.Length);
                    Console.WriteLine("应答帧已直接写入串口（不做等待）。");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
            }
        }

        private void DoSendRawHex()
        {
            if (!RequireSession()) return;

            Console.Write("要发送的字节（HEX，如 AA 01 00 00 20 00 55）: ");
            string? text = Console.ReadLine();
            if (!Hex.TryParse(text, out var bytes, out string? error))
            {
                Console.WriteLine("输入无效：" + error);
                return;
            }

            try
            {
                _session!.Transport.Write(bytes, 0, bytes.Length);
                Console.WriteLine("已发送 " + bytes.Length + " 字节");
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
            }
        }

        private void ShowStatistics()
        {
            if (_session == null)
            {
                Console.WriteLine("尚未打开串口。");
                return;
            }

            Console.WriteLine("通道：" + _session.Transport.Name);
            if (_session.Transport is SerialTransport serial)
            {
                Console.WriteLine("  累计发送：" + serial.BytesSent + " 字节");
                Console.WriteLine("  累计接收：" + serial.BytesReceived + " 字节");
            }
            Console.WriteLine("  协议解析：" + _session.Statistics);
        }

        private void RunHardwareLoopback()
        {
            if (_session != null && _session.IsOpen)
            {
                Console.WriteLine("回环测试需要独占串口，请先用菜单 4 关闭当前连接。");
                return;
            }

            var ports = SerialPortSettings.GetPortNames();
            if (ports.Length == 0)
            {
                Console.WriteLine("未发现任何串口。");
                return;
            }

            Console.WriteLine("可用串口：" + string.Join(" ", ports));
            Console.Write("串口名（默认 " + ports[0] + "）: ");
            string name = (Console.ReadLine() ?? string.Empty).Trim();
            if (name.Length == 0) name = ports[0];

            Console.Write("波特率（默认 115200）: ");
            string baudText = (Console.ReadLine() ?? string.Empty).Trim();
            int baudRate = 115200;
            if (baudText.Length > 0 && !int.TryParse(baudText, out baudRate)) baudRate = 115200;

            LoopbackTester.Run(name, baudRate);
        }

        // ---------- 事件输出 ----------

        private void OnFrameReceived(object? sender, FrameReceivedEventArgs e)
        {
            Log.Line("  ← 收帧 " + e.Timestamp.ToString("HH:mm:ss.fff") + "  " + e.Frame
                     + Environment.NewLine + "        " + e.Frame.ToHexString());
        }

        private void OnFrameError(object? sender, FrameErrorEventArgs e)
        {
            if (e.IsResynchronization)
            {
                return; // 噪声字节重新同步，不必刷屏
            }
            Log.Warn("帧错误：" + e.Message);
        }

        private void OnSessionTransportError(object? sender, TransportErrorEventArgs e)
        {
            Log.Error(e.Message);
        }

        private void OnSessionClosed(object? sender, EventArgs e)
        {
            // 仅在“非主动关闭”时触发（主动关闭前会先取消订阅）
            Log.Warn("串口已关闭（设备可能已被拔出）。");
        }

        private void OnRawSent(object? sender, TransportDataEventArgs e)
        {
            Log.Line("  → 发送 " + e.Timestamp.ToString("HH:mm:ss.fff") + "  " + e.Hex);
        }

        private void OnRawReceived(object? sender, TransportDataEventArgs e)
        {
            Log.Line("  ← 接收 " + e.Timestamp.ToString("HH:mm:ss.fff") + "  " + e.Hex);
        }

        // ---------- 工具 ----------

        private static string Describe(string portName)
        {
            string description = UsbSerialDeviceEnumerator.Describe(portName);
            return description == portName ? portName : description;
        }

        /// <summary>按十六进制解析输入（允许 0x 前缀），用于寄存地址 / 数值。</summary>
        private static bool TryReadHex(string? text, out int value)
        {
            value = 0;
            if (text == null) return false;

            string trimmed = text.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(2);
            if (trimmed.Length == 0) return false;

            return int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseCurrent();
        }
    }
}
