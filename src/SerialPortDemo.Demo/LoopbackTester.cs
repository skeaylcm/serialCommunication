using System;
using System.Collections.Generic;
using System.Threading;

namespace SerialPortDemo.Demo
{
    /// <summary>
    /// 硬件回环测试：把串口的 TX 与 RX 短接（或插回环头），
    /// 发送的帧应当被本机原样收回。用于验证
    /// “USB 转串口芯片 + 驱动 + 线缆 + 协议解析器”整条链路是否正常。
    /// </summary>
    internal static class LoopbackTester
    {
        public static int Run(string portName, int baudRate, int rounds = 5, int payloadSize = 8)
        {
            Console.WriteLine("=== 硬件回环测试 ===");
            Console.WriteLine("前提：把 " + portName + " 的 TX 与 RX 短接（DB9 的 2、3 脚，或 USB 转串口的回环头）。");
            Console.WriteLine("原理：发送自定义帧 → 从本机原样收回 → 逐字节比对。");
            Console.WriteLine();

            var settings = new Transport.SerialPortSettings(portName) { BaudRate = baudRate };
            using (var transport = new Transport.SerialTransport(settings))
            {
                var received = new List<byte>();
                var gate = new object();
                var signal = new AutoResetEvent(false);

                transport.DataReceived += (sender, e) =>
                {
                    lock (gate)
                    {
                        received.AddRange(e.Data);
                    }
                    signal.Set();
                };
                transport.Error += (sender, e) => Log.Error(e.Message);

                try
                {
                    transport.Open();
                }
                catch (Exception ex)
                {
                    Log.Error(ex.Message);
                    return 1;
                }

                Log.Info("已打开 " + settings);
                Console.WriteLine();

                int passed = 0;
                for (int round = 1; round <= rounds; round++)
                {
                    var payload = new byte[payloadSize];
                    new Random(round * 7919).NextBytes(payload);
                    var frame = new Protocol.ProtocolFrame(0x7A, payload).ToArray();

                    lock (gate)
                    {
                        received.Clear();
                    }

                    transport.Write(frame, 0, frame.Length);

                    var deadline = DateTime.UtcNow.AddMilliseconds(1000);
                    while (DateTime.UtcNow < deadline)
                    {
                        lock (gate)
                        {
                            if (received.Count >= frame.Length) break;
                        }
                        signal.WaitOne(20);
                    }

                    byte[] echo;
                    lock (gate)
                    {
                        echo = received.ToArray();
                    }

                    bool match = Bytes.AreEqual(echo, frame);
                    if (match) passed++;

                    Console.WriteLine(string.Format("  第 {0,2} 轮：发送 {1} 字节，收回 {2} 字节 -> {3}",
                        round, frame.Length, echo.Length, match ? "一致" : "不一致"));
                    if (!match)
                    {
                        Console.WriteLine("        发送: " + Hex.ToString(frame));
                        Console.WriteLine("        收回: " + (echo.Length == 0 ? "(无数据)" : Hex.ToString(echo)));
                    }
                }

                transport.Close();

                Console.WriteLine();
                Console.WriteLine(string.Format("回环结果：{0}/{1} 轮通过", passed, rounds));
                if (passed == 0)
                {
                    Console.WriteLine("排查建议：");
                    Console.WriteLine("  1) TX/RX 是否确实短接（或用回环头）；");
                    Console.WriteLine("  2) 端口是否选错（用 --list 查看 USB 转串口设备）；");
                    Console.WriteLine("  3) 端口是否被其它程序（调试助手、Modbus 工具）占用；");
                    Console.WriteLine("  4) 波特率是否与设备一致。");
                }
                return passed == rounds ? 0 : 1;
            }
        }
    }
}
