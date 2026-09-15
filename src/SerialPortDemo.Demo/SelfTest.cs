using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using SerialPortDemo.Device;
using SerialPortDemo.Protocol;
using SerialPortDemo.Simulation;
using SerialPortDemo.Transport;

namespace SerialPortDemo.Demo
{
    /// <summary>
    /// 协议自检：不接任何硬件，用虚拟串口链路把协议栈跑一遍。
    /// 上板子之前先跑这个，可以提前发现帧格式/字节序/校验的问题。
    /// </summary>
    internal static class SelfTest
    {
        private static int _passed;
        private static int _failed;

        public static int Run()
        {
            _passed = 0;
            _failed = 0;

            Console.WriteLine("=== 232 协议自检（虚拟链路，无需硬件）===");

            TestCrc();
            TestFrameRoundTrip();
            TestPingFrameBytes();
            TestStreamParsing();
            TestCrcFailure();
            TestVirtualLinkEndToEnd();
            TestErrorStatus();
            TestTimeout();

            Console.WriteLine();
            Console.WriteLine(string.Format("自检结果：通过 {0} 项，失败 {1} 项", _passed, _failed));
            return _failed == 0 ? 0 : 1;
        }

        // ---------- 1. CRC ----------
        private static void TestCrc()
        {
            Section("1. CRC16/MODBUS 已知向量");

            ushort crc = Crc16.Compute(Encoding.ASCII.GetBytes("123456789"));
            Check("CRC16(\"123456789\") == 0x4B37", crc == 0x4B37, "实际 0x" + crc.ToString("X4"));

            ushort crc2 = Crc16.Compute(new byte[] { 0x01, 0x00, 0x00 });
            Check("CRC16(01 00 00) == 0x0020", crc2 == 0x0020, "实际 0x" + crc2.ToString("X4"));

            var buffer = new byte[2];
            Crc16.WriteLittleEndian(buffer, 0, 0x1234);
            Check("CRC 低字节在前的写入/读取往返",
                buffer[0] == 0x34 && buffer[1] == 0x12 && Crc16.ReadLittleEndian(buffer, 0) == 0x1234,
                "实际 " + Hex.ToString(buffer));
        }

        // ---------- 2. 帧编解码 ----------
        private static void TestFrameRoundTrip()
        {
            Section("2. 帧编解码往返");

            foreach (int length in new[] { 0, 1, 2, 3, 255, 256, 1024 })
            {
                var payload = new byte[length];
                new Random(1234 + length).NextBytes(payload);

                var original = new ProtocolFrame(0x10, payload);
                var bytes = original.ToArray();
                bool ok = ProtocolFrame.TryDecode(bytes, 0, bytes.Length, out var decoded, out var error)
                          && decoded != null
                          && decoded.Command == original.Command
                          && Bytes.AreEqual(decoded.Payload, payload);

                Check(string.Format("LEN={0} 编解码往返（整帧 {1} 字节）", length, bytes.Length), ok,
                    ok ? null : "error=" + error);
            }

            // 超过上限应被拒绝
            Throws<ArgumentException>("LEN 超过 1024 时构造报错",
                () => new ProtocolFrame(0x10, new byte[ProtocolFrame.MaxPayloadLength + 1]));
        }

        // ---------- 3. 固定字节序列 ----------
        private static void TestPingFrameBytes()
        {
            Section("3. 报文字节序（与协议文档对照）");

            var bytes = new ProtocolFrame(CommandCode.Ping, null).ToArray();
            var expected = new byte[] { 0xAA, 0x01, 0x00, 0x00, 0x20, 0x00, 0x55 };
            Check("PING 帧 == AA 01 00 00 20 00 55", Bytes.AreEqual(bytes, expected), "实际 " + Hex.ToString(bytes));

            var write = new ProtocolFrame(CommandCode.WriteRegister, new byte[] { 0x10, 0x00, 0x34, 0x12 }).ToArray();
            Check("WRITE 帧以 0xAA 开头、0x55 结尾", write[0] == 0xAA && write[write.Length - 1] == 0x55);
            Check("WRITE 帧长度 == 7 + 4", write.Length == ProtocolFrame.OverheadLength + 4);
        }

        // ---------- 4. 流式解析（粘包 / 半包 / 噪声）----------
        private static void TestStreamParsing()
        {
            Section("4. 流式解析：噪声 + 粘包 + 半包");

            var parser = new FrameParser();
            var frames = new List<ProtocolFrame>();
            parser.FrameReceived += (sender, e) => frames.Add(e.Frame);

            var first = new ProtocolFrame(CommandCode.Ping, null).ToArray();
            var second = new ProtocolFrame(CommandCode.WriteRegister, new byte[] { 0x01, 0x02, 0x03, 0x04 }).ToArray();
            var third = new ProtocolFrame(CommandCode.ReadHoldingRegisters, new byte[] { 0x00, 0x00, 0x08 }).ToArray();

            // 噪声 + 两帧 + 第三帧的前 3 个字节
            var stream = new List<byte>();
            stream.AddRange(new byte[] { 0x00, 0x11, 0x22 });
            stream.AddRange(first);
            stream.AddRange(second);
            for (int i = 0; i < 3; i++) stream.Add(third[i]);

            // 按 5 字节一段喂入，模拟串口驱动的随机分片
            var all = stream.ToArray();
            for (int offset = 0; offset < all.Length; offset += 5)
            {
                int count = Math.Min(5, all.Length - offset);
                parser.Append(all, offset, count);
            }

            Check("解析出 2 个完整帧（第三帧还缺数据）", frames.Count == 2, "实际 " + frames.Count);

            // 补齐第三帧剩余字节
            parser.Append(third, 3, third.Length - 3);
            Check("补齐后解析出 3 个帧", frames.Count == 3, "实际 " + frames.Count);

            if (frames.Count == 3)
            {
                Check("第 1 帧内容正确", Bytes.AreEqual(frames[0].ToArray(), first));
                Check("第 2 帧内容正确", Bytes.AreEqual(frames[1].ToArray(), second));
                Check("第 3 帧内容正确", Bytes.AreEqual(frames[2].ToArray(), third));
            }

            Check("丢弃的噪声字节数为 3", parser.Statistics.ResynchronizedBytes == 3,
                "实际 " + parser.Statistics.ResynchronizedBytes);
            Check("统计：收到 3 帧", parser.Statistics.FramesReceived == 3);
            Check("统计：字节数等于喂入总量", parser.Statistics.BytesReceived == all.Length + (third.Length - 3));
        }

        // ---------- 5. CRC 校验失败 ----------
        private static void TestCrcFailure()
        {
            Section("5. CRC 校验失败");

            var parser = new FrameParser();
            int frameCount = 0;
            var errors = new List<FrameDecodeError>();
            parser.FrameReceived += (sender, e) => frameCount++;
            parser.FrameError += (sender, e) => errors.Add(e.Error);

            var broken = new ProtocolFrame(CommandCode.Ping, null).ToArray();
            broken[4] ^= 0xFF; // 破坏 CRC 低字节

            parser.Append(broken, 0, broken.Length);

            Check("坏帧不会被上报", frameCount == 0, "实际 " + frameCount);
            Check("记录 CRC 错误", parser.Statistics.CrcErrors == 1, "实际 " + parser.Statistics.CrcErrors);
            Check("错误类型为 CrcMismatch", errors.Contains(FrameDecodeError.CrcMismatch), "实际 " + string.Join(",", errors));

            // 帧尾错误（帧尾不在 CRC 覆盖范围内，只能靠帧尾检查发现）
            var badTail = new ProtocolFrame(CommandCode.Ping, null).ToArray();
            badTail[badTail.Length - 1] = 0x00;

            errors.Clear();
            parser.Reset();
            parser.Append(badTail, 0, badTail.Length);
            Check("帧尾错误被识别", errors.Contains(FrameDecodeError.BadEndOfFrame), "实际 " + string.Join(",", errors));
        }

        // ---------- 6. 端到端（上位机 <-> 虚拟下位机）----------
        private static void TestVirtualLinkEndToEnd()
        {
            Section("6. 端到端：PING / 写寄存器 / 读寄存器");

            using (var link = VirtualSerialLink.Create(1))
            using (var device = new ProtocolDevice(link.Slave, new RegisterFile(64)))
            using (var session = new ProtocolSession(link.Master, TimeSpan.FromMilliseconds(500)))
            {
                session.Start();

                var uptime = session.Ping();
                Check("PING 得到设备运行时间", uptime >= TimeSpan.Zero, "实际 " + uptime);

                session.WriteRegister(0x0010, 0xABCD);
                Check("写寄存器后设备侧数值同步", device.Registers[0x0010] == 0xABCD);

                device.Registers[0x0011] = 0x00FF;

                var values = session.ReadHoldingRegisters(0x0010, 3);
                bool ok = values.Length == 3 && values[0] == 0xABCD && values[1] == 0x00FF && values[2] == 0x0000;
                Check("连续读 3 个寄存器内容正确", ok,
                    "实际 " + values[0].ToString("X4") + " " + values[1].ToString("X4") + " " + values[2].ToString("X4"));

                Check("下位机统计：收到 3 个请求帧", device.Statistics.FramesReceived == 3,
                    "实际 " + device.Statistics.FramesReceived);
                Check("上位机统计：收到 3 个应答帧", session.Statistics.FramesReceived == 3,
                    "实际 " + session.Statistics.FramesReceived);
                Check("两侧 CRC 错误均为 0", device.Statistics.CrcErrors == 0 && session.Statistics.CrcErrors == 0);
            }
        }

        // ---------- 7. 设备返回错误状态码 ----------
        private static void TestErrorStatus()
        {
            Section("7. 设备错误状态码");

            using (var link = VirtualSerialLink.Create(1))
            using (var device = new ProtocolDevice(link.Slave, new RegisterFile(16)))
            using (var session = new ProtocolSession(link.Master, TimeSpan.FromMilliseconds(500)))
            {
                session.Start();

                byte? unknownCommandStatus = null;
                try
                {
                    session.SendCommand(0x7F).EnsureSuccess();
                }
                catch (ProtocolException ex)
                {
                    unknownCommandStatus = ex.StatusCode;
                }
                Check("未知命令返回“未知命令”状态", unknownCommandStatus == StatusCode.UnknownCommand,
                    "实际 " + unknownCommandStatus);

                byte? rangeStatus = null;
                try
                {
                    session.ReadHoldingRegisters(0x00F0, 8);
                }
                catch (ProtocolException ex)
                {
                    rangeStatus = ex.StatusCode;
                }
                Check("越界读返回“地址越界”状态", rangeStatus == StatusCode.AddressOutOfRange,
                    "实际 " + rangeStatus);

                byte? writeStatus = null;
                try
                {
                    session.WriteRegister(0x00FF, 0x1234);
                }
                catch (ProtocolException ex)
                {
                    writeStatus = ex.StatusCode;
                }
                Check("越界写返回“地址越界”状态", writeStatus == StatusCode.AddressOutOfRange,
                    "实际 " + writeStatus);
            }
        }

        // ---------- 8. 超时 ----------
        private static void TestTimeout()
        {
            Section("8. 应答超时");

            // 只挂上位机，不挂下位机：写入的字节不会有人应答
            using (var link = VirtualSerialLink.Create(0))
            using (var session = new ProtocolSession(link.Master, TimeSpan.FromMilliseconds(200)))
            {
                session.Start();

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    session.SendCommand(CommandCode.Ping);
                    Check("无应答时抛出 TimeoutException", false, "未抛出异常");
                }
                catch (TimeoutException)
                {
                    stopwatch.Stop();
                    Check("无应答时抛出 TimeoutException", true);
                    Check("超时时间符合配置（约 200 ms）", stopwatch.ElapsedMilliseconds >= 150,
                        "实际 " + stopwatch.ElapsedMilliseconds + " ms");
                }
            }
        }

        // ---------- 输出辅助 ----------
        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("-- " + title);
        }

        private static void Check(string name, bool condition, string? detail = null)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  [通过] " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("  [失败] " + name + (string.IsNullOrEmpty(detail) ? string.Empty : " -> " + detail));
            }
        }

        private static void Throws<TException>(string name, Action action) where TException : Exception
        {
            try
            {
                action();
                Check(name, false, "未抛出 " + typeof(TException).Name);
            }
            catch (TException)
            {
                Check(name, true);
            }
            catch (Exception ex)
            {
                Check(name, false, "抛出了 " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
