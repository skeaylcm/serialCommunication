using System;

namespace SerialPortDemo.Protocol
{
    /// <summary>帧解码失败的原因。</summary>
    public enum FrameDecodeError
    {
        None = 0,
        /// <summary>数据长度不足一帧。</summary>
        TooShort,
        /// <summary>帧头不是 0xAA。</summary>
        BadStartOfFrame,
        /// <summary>LEN 字段超过 MaxPayloadLength。</summary>
        PayloadTooLarge,
        /// <summary>LEN 与实际数据长度不一致。</summary>
        LengthMismatch,
        /// <summary>CRC 校验失败。</summary>
        CrcMismatch,
        /// <summary>帧尾不是 0x55。</summary>
        BadEndOfFrame,
    }

    /// <summary>
    /// 应用层协议帧：“帧头 + 命令 + 长度 + 数据 + 校验 + 帧尾”，小端字节序。
    ///
    /// <code>
    /// +--------+--------+--------+----------------+---------+--------+
    /// |  SOF   |  CMD   |  LEN   |      DATA      |  CRC16  |  EOF   |
    /// | 1 字节 | 1 字节 | 2 字节 |   LEN 字节     | 2 字节  | 1 字节 |
    /// +--------+--------+--------+----------------+---------+--------+
    /// SOF   = 0xAA                       帧头
    /// CMD   = 命令码，bit7=1 表示应答帧（见 CommandCode）
    /// LEN   = DATA 字节数，0 ~ 1024，不包含帧头/校验/帧尾
    /// CRC16 = CRC16/MODBUS，覆盖 CMD + LEN + DATA，低字节在前
    /// EOF   = 0x55                       帧尾
    /// </code>
    ///
    /// 应答帧的 DATA[0] 为状态码（见 <see cref="StatusCode"/>）。
    /// </summary>
    public sealed class ProtocolFrame
    {
        public const byte StartOfFrame = 0xAA;
        public const byte EndOfFrame = 0x55;
        public const int MaxPayloadLength = 1024;

        /// <summary>SOF + CMD + LEN(2)</summary>
        public const int HeaderLength = 4;
        /// <summary>CRC(2) + EOF</summary>
        public const int TrailerLength = 3;
        /// <summary>帧固定开销：7 字节</summary>
        public const int OverheadLength = HeaderLength + TrailerLength;
        /// <summary>最长帧长度：7 + 1024 = 1031 字节</summary>
        public const int MaxFrameLength = OverheadLength + MaxPayloadLength;

        private static readonly byte[] EmptyPayload = new byte[0];

        public ProtocolFrame(byte command, byte[]? payload)
        {
            if (payload != null && payload.Length > MaxPayloadLength)
            {
                throw new ArgumentException("DATA 长度超过 " + MaxPayloadLength + " 字节", nameof(payload));
            }

            Command = command;
            Payload = payload == null || payload.Length == 0 ? EmptyPayload : payload;
        }

        /// <summary>命令码（请求帧 bit7=0，应答帧 bit7=1）。</summary>
        public byte Command { get; }

        /// <summary>DATA 字段（不含帧头、长度、CRC、帧尾）。</summary>
        public byte[] Payload { get; }

        /// <summary>整帧字节数。</summary>
        public int Length => OverheadLength + Payload.Length;

        public bool IsResponse => CommandCode.IsResponse(Command);

        public bool IsRequest => !IsResponse;

        /// <summary>应答帧的状态码；请求帧或空 DATA 返回 null。</summary>
        public byte? ResponseStatus => IsResponse && Payload.Length > 0 ? Payload[0] : (byte?)null;

        /// <summary>应答帧状态码非 0 时抛出 <see cref="ProtocolException"/>。</summary>
        public ProtocolFrame EnsureSuccess()
        {
            if (!IsResponse) return this;
            byte? status = ResponseStatus;
            if (status == null) throw new ProtocolException("应答帧没有状态码");
            if (status.Value != SerialPortDemo.Protocol.StatusCode.Ok)
            {
                throw new ProtocolException(status.Value,
                    "设备返回错误：" + SerialPortDemo.Protocol.StatusCode.Describe(status.Value)
                    + " (0x" + status.Value.ToString("X2") + ")");
            }
            return this;
        }

        /// <summary>编码成字节数组。</summary>
        public byte[] ToArray()
        {
            var buffer = new byte[Length];
            Encode(buffer, 0);
            return buffer;
        }

        /// <summary>把整帧写入目标缓冲区，返回写入的字节数。</summary>
        public int Encode(byte[] buffer, int offset)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset + Length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));

            buffer[offset] = StartOfFrame;
            buffer[offset + 1] = Command;
            buffer[offset + 2] = (byte)(Payload.Length & 0xFF);
            buffer[offset + 3] = (byte)((Payload.Length >> 8) & 0xFF);
            if (Payload.Length > 0)
            {
                Buffer.BlockCopy(Payload, 0, buffer, offset + HeaderLength, Payload.Length);
            }

            ushort crc = Crc16.Compute(buffer, offset + 1, 3 + Payload.Length);
            Crc16.WriteLittleEndian(buffer, offset + HeaderLength + Payload.Length, crc);
            buffer[offset + OverheadLength + Payload.Length - 1] = EndOfFrame;
            return Length;
        }

        /// <summary>解析一整帧，失败时抛出 <see cref="ProtocolException"/>。</summary>
        public static ProtocolFrame Decode(byte[] buffer, int offset, int count)
        {
            if (!TryDecode(buffer, offset, count, out var frame, out var error))
            {
                throw new ProtocolException("帧解析失败：" + Describe(error));
            }
            return frame!;
        }

        /// <summary>解析一整帧（必须正好一个完整帧）。</summary>
        public static bool TryDecode(byte[] buffer, int offset, int count, out ProtocolFrame? frame, out FrameDecodeError error)
        {
            frame = null;
            error = FrameDecodeError.None;

            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));

            if (count < OverheadLength)
            {
                error = FrameDecodeError.TooShort;
                return false;
            }
            if (buffer[offset] != StartOfFrame)
            {
                error = FrameDecodeError.BadStartOfFrame;
                return false;
            }

            byte command = buffer[offset + 1];
            int payloadLength = buffer[offset + 2] | (buffer[offset + 3] << 8);
            if (payloadLength > MaxPayloadLength)
            {
                error = FrameDecodeError.PayloadTooLarge;
                return false;
            }
            if (count != OverheadLength + payloadLength)
            {
                error = FrameDecodeError.LengthMismatch;
                return false;
            }

            ushort expected = Crc16.ReadLittleEndian(buffer, offset + HeaderLength + payloadLength);
            ushort actual = Crc16.Compute(buffer, offset + 1, 3 + payloadLength);
            if (expected != actual)
            {
                error = FrameDecodeError.CrcMismatch;
                return false;
            }
            if (buffer[offset + OverheadLength + payloadLength - 1] != EndOfFrame)
            {
                error = FrameDecodeError.BadEndOfFrame;
                return false;
            }

            var payload = new byte[payloadLength];
            if (payloadLength > 0)
            {
                Buffer.BlockCopy(buffer, offset + HeaderLength, payload, 0, payloadLength);
            }
            frame = new ProtocolFrame(command, payload);
            return true;
        }

        public static string Describe(FrameDecodeError error)
        {
            switch (error)
            {
                case FrameDecodeError.None: return "无错误";
                case FrameDecodeError.TooShort: return "数据长度不足一帧";
                case FrameDecodeError.BadStartOfFrame: return "帧头错误（不是 0xAA）";
                case FrameDecodeError.PayloadTooLarge: return "LEN 超出上限";
                case FrameDecodeError.LengthMismatch: return "LEN 与实际长度不一致";
                case FrameDecodeError.CrcMismatch: return "CRC 校验失败";
                case FrameDecodeError.BadEndOfFrame: return "帧尾错误（不是 0x55）";
                default: return error.ToString();
            }
        }

        /// <summary>形如 "PING  AA 01 00 00 20 00 55"。</summary>
        public string ToHexString() => SerialPortDemo.Hex.ToString(ToArray());

        public override string ToString()
        {
            return string.Format("{0} CMD=0x{1:X2} LEN={2}{3} | {4}",
                CommandCode.Describe(Command),
                Command,
                Payload.Length,
                ResponseStatus.HasValue ? " STATUS=" + ResponseStatus.Value.ToString("X2") : string.Empty,
                Payload.Length == 0 ? "(无数据)" : SerialPortDemo.Hex.ToString(Payload));
        }
    }
}
