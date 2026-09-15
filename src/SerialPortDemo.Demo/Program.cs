using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using SerialPortDemo.Protocol;
using SerialPortDemo.Transport;
using SerialPortDemo.Usb;

namespace SerialPortDemo.Demo
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception)
            {
                // 某些宿主不支持改编码，忽略
            }

            var commandLine = CommandLine.Parse(args);

            if (commandLine.Has("help") || commandLine.Has("h") || commandLine.Has("?"))
            {
                PrintUsage();
                return 0;
            }

            try
            {
                if (commandLine.Has("selftest")) return SelfTest.Run();
                if (commandLine.Has("list")) return ListPorts();
                if (commandLine.Has("loopback")) return RunLoopback(commandLine);
                if (commandLine.Has("monitor")) return RunMonitor(commandLine);

                string? port = commandLine.Get("port");
                if (!string.IsNullOrEmpty(port)) return RunOneShot(commandLine, port!);

                using (var app = new SerialConsole())
                {
                    return app.RunMenu();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("RS-232 串口通讯协议 Demo");
            Console.WriteLine();
            Console.WriteLine("用法：");
            Console.WriteLine("  SerialPortDemo.Demo                        交互式菜单");
            Console.WriteLine("  SerialPortDemo.Demo --list                 列出串口及 USB 设备信息");
            Console.WriteLine("  SerialPortDemo.Demo --selftest             协议自检（虚拟链路，无需硬件）");
            Console.WriteLine("  SerialPortDemo.Demo --loopback COM3        硬件回环测试（需短接 TX/RX）");
            Console.WriteLine("  SerialPortDemo.Demo --monitor COM3         只接收并解析报文");
            Console.WriteLine("  SerialPortDemo.Demo --port COM3 --ping     打开端口并 PING");
            Console.WriteLine("  SerialPortDemo.Demo --port COM3 --read 0x0000 --count 8");
            Console.WriteLine("  SerialPortDemo.Demo --port COM3 --write 0x0010 --value 0xABCD");
            Console.WriteLine("  SerialPortDemo.Demo --port COM3 --send \"AA 01 00 00 20 00 55\"");
            Console.WriteLine();
            Console.WriteLine("可选参数：--baud 115200（默认）  --timeout 1000（应答超时，毫秒）");
        }

        private static int ListPorts()
        {
            var devices = UsbSerialDeviceEnumerator.Enumerate();
            if (devices.Count == 0)
            {
                var ports = SerialPortSettings.GetPortNames();
                if (ports.Length == 0)
                {
                    Console.WriteLine("未发现任何串口。请插入 USB 转串口设备并确认驱动已安装。");
                    return 1;
                }
                Console.WriteLine("串口列表（未能获取 USB 设备信息）：");
                foreach (var port in ports) Console.WriteLine("  " + port);
                return 0;
            }

            Console.WriteLine("串口列表：");
            foreach (var device in devices)
            {
                Console.WriteLine("  " + device);
                if (device.IsUsbDevice)
                {
                    Console.WriteLine("        " + device.DeviceId);
                }
            }
            return 0;
        }

        private static int RunLoopback(CommandLine commandLine)
        {
            string port = commandLine.Get("loopback")!;
            int baudRate = commandLine.GetInt("baud", 115200);
            int rounds = commandLine.GetInt("rounds", 5);
            int size = commandLine.GetInt("size", 8);
            return LoopbackTester.Run(port, baudRate, rounds, size);
        }

        private static int RunMonitor(CommandLine commandLine)
        {
            string port = commandLine.Get("monitor")!;
            var settings = new SerialPortSettings(port) { BaudRate = commandLine.GetInt("baud", 115200) };

            using (var transport = new SerialTransport(settings))
            using (var session = new ProtocolSession(transport))
            {
                session.FrameReceived += (sender, e) =>
                    Log.Line("← " + e.Timestamp.ToString("HH:mm:ss.fff") + "  " + e.Frame
                             + Environment.NewLine + "    " + e.Frame.ToHexString());
                session.FrameError += (sender, e) =>
                {
                    if (!e.IsResynchronization) Log.Warn(e.Message);
                };
                session.TransportError += (sender, e) => Log.Error(e.Message);

                var stop = new ManualResetEventSlim(false);
                session.Closed += (sender, e) =>
                {
                    Log.Warn("串口已关闭，监听结束。");
                    stop.Set();
                };
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    stop.Set();
                };

                session.Start();
                Log.Info("正在监听 " + settings + "，按 Ctrl+C 退出...");
                stop.Wait();

                if (session.IsOpen) session.Stop();
            }
            return 0;
        }

        private static int RunOneShot(CommandLine commandLine, string portName)
        {
            int timeoutMs = commandLine.GetInt("timeout", 1000);
            var settings = new SerialPortSettings(portName) { BaudRate = commandLine.GetInt("baud", 115200) };

            using (var transport = new SerialTransport(settings))
            using (var session = new ProtocolSession(transport, TimeSpan.FromMilliseconds(timeoutMs)))
            {
                session.FrameReceived += (sender, e) =>
                    Log.Line("← " + e.Timestamp.ToString("HH:mm:ss.fff") + "  " + e.Frame
                             + Environment.NewLine + "    " + e.Frame.ToHexString());
                session.FrameError += (sender, e) =>
                {
                    if (!e.IsResynchronization) Log.Warn(e.Message);
                };
                session.TransportError += (sender, e) => Log.Error(e.Message);

                session.Start();
                Log.Info("已打开 " + settings);

                if (commandLine.Has("read"))
                {
                    ushort address = (ushort)commandLine.GetInt("read", 0);
                    int count = commandLine.GetInt("count", 1);
                    if (count < 1 || count > 125) throw new ArgumentException("--count 必须在 1 ~ 125 之间");

                    var values = session.ReadHoldingRegisters(address, (byte)count);
                    for (int i = 0; i < values.Length; i++)
                    {
                        Console.WriteLine(string.Format("  [{0}] = 0x{1:X4}  ({1})", (address + i).ToString("X4"), values[i]));
                    }
                    return 0;
                }

                if (commandLine.Has("write"))
                {
                    string writeText = commandLine.Get("write")!;
                    ushort address;
                    ushort value;

                    int separator = writeText.IndexOf('=');
                    if (separator > 0)
                    {
                        address = (ushort)ParseNumber(writeText.Substring(0, separator));
                        value = (ushort)ParseNumber(writeText.Substring(separator + 1));
                    }
                    else
                    {
                        address = (ushort)ParseNumber(writeText);
                        value = (ushort)commandLine.GetInt("value", 0);
                    }

                    session.WriteRegister(address, value);
                    Console.WriteLine(string.Format("写入成功：寄存器 [{0}] = 0x{1:X4}", address.ToString("X4"), value));
                    return 0;
                }

                if (commandLine.Has("send"))
                {
                    if (!Hex.TryParse(commandLine.Get("send")!, out var bytes, out string? error))
                    {
                        throw new ArgumentException("--send 参数无效：" + error);
                    }
                    transport.Write(bytes, 0, bytes.Length);
                    Console.WriteLine("已发送 " + bytes.Length + " 字节：" + Hex.ToString(bytes));

                    // 给设备一点时间应答，便于观察
                    Thread.Sleep(Math.Min(timeoutMs, 2000));
                    return 0;
                }

                // 默认动作：PING
                var uptime = session.Ping();
                Console.WriteLine(string.Format("PING 成功，设备运行时间 {0:F3} 秒", uptime.TotalSeconds));

                if (session.IsOpen) session.Stop();
                return 0;
            }
        }

        private static int ParseNumber(string text)
        {
            string trimmed = text.Trim();
            bool hex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("$", StringComparison.Ordinal);
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(2);
            else if (trimmed.StartsWith("$", StringComparison.Ordinal)) trimmed = trimmed.Substring(1);

            int value = hex
                ? int.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : int.Parse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture);
            return value;
        }

        /// <summary>极简命令行解析：支持 --key value、--key=value、--flag。</summary>
        private sealed class CommandLine
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public static CommandLine Parse(string[] args)
            {
                var result = new CommandLine();
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (string.IsNullOrEmpty(arg)) continue;
                    if (arg[0] != '-' && arg[0] != '/') continue;

                    string text = arg.TrimStart('-', '/');
                    int separator = text.IndexOf('=');
                    if (separator > 0)
                    {
                        result._values[text.Substring(0, separator)] = text.Substring(separator + 1);
                        continue;
                    }

                    bool hasValue = i + 1 < args.Length
                                    && !string.IsNullOrEmpty(args[i + 1])
                                    && args[i + 1][0] != '-';
                    if (hasValue)
                    {
                        result._values[text] = args[++i];
                    }
                    else
                    {
                        result._flags.Add(text);
                    }
                }
                return result;
            }

            public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);

            public string? Get(string name)
            {
                string? value;
                return _values.TryGetValue(name, out value) ? value : null;
            }

            public int GetInt(string name, int fallback)
            {
                string? text = Get(name);
                if (string.IsNullOrWhiteSpace(text)) return fallback;
                try
                {
                    return ParseNumber(text!);
                }
                catch (Exception)
                {
                    return fallback;
                }
            }
        }
    }
}
