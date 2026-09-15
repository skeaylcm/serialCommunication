using System;

namespace SerialPortDemo.Protocol
{
    /// <summary>
    /// 流式帧解析器（状态机 + 滚动缓冲）。
    ///
    /// 串口是字节流：一次 Read 可能读到半个帧，也可能读到多个帧，还可能有线路噪声。
    /// 本类负责：
    ///   1. 按帧头 0xAA 重新同步，丢弃噪声字节；
    ///   2. 按 LEN 字段判断帧是否收齐，未收齐则等待后续字节（解决粘包/半包）；
    ///   3. 校验 CRC 与帧尾，只有完全正确的帧才通过 <see cref="FrameReceived"/> 上报；
    ///   4. 统计接收质量，便于判断是波特率不匹配还是设备异常。
    /// </summary>
    public sealed class FrameParser
    {
        private readonly byte[] _buffer = new byte[ProtocolFrame.MaxFrameLength];
        private int _count;

        /// <summary>解析统计。</summary>
        public ParserStatistics Statistics { get; } = new ParserStatistics();

        /// <summary>成功解析出一个完整帧。</summary>
        public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

        /// <summary>线路异常（噪声、CRC 错误、帧尾错误等）。</summary>
        public event EventHandler<FrameErrorEventArgs>? FrameError;

        /// <summary>喂入收到的字节。</summary>
        public void Append(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));

            Statistics.AddBytes(count);

            while (count > 0)
            {
                int room = _buffer.Length - _count;
                if (room == 0)
                {
                    // 防御性分支：缓冲区已满却凑不出完整帧，丢弃 1 字节继续同步
                    Statistics.AddResynchronizedBytes(1);
                    Discard(1);
                    room = _buffer.Length - _count;
                }

                int take = Math.Min(room, count);
                Buffer.BlockCopy(buffer, offset, _buffer, _count, take);
                _count += take;
                offset += take;
                count -= take;

                ExtractFrames();
            }
        }

        /// <summary>清空待解析数据（统计不清零）。</summary>
        public void Reset() => _count = 0;

        private void ExtractFrames()
        {
            while (_count > 0)
            {
                // 1) 重新同步：跳过帧头之前的噪声字节
                if (_buffer[0] != ProtocolFrame.StartOfFrame)
                {
                    int index = IndexOfStartOfFrame(1);
                    int drop = index < 0 ? _count : index;
                    Statistics.AddResynchronizedBytes(drop);
                    var noise = new byte[drop];
                    Buffer.BlockCopy(_buffer, 0, noise, 0, drop);
                    Discard(drop);
                    RaiseError(FrameDecodeError.BadStartOfFrame, "丢弃 " + drop + " 个噪声字节", noise);
                    continue;
                }

                // 2) 头还没收齐
                if (_count < ProtocolFrame.HeaderLength) return;

                int payloadLength = _buffer[2] | (_buffer[3] << 8);
                if (payloadLength > ProtocolFrame.MaxPayloadLength)
                {
                    // LEN 字段不合法：当前这个 0xAA 是假帧头，只丢 1 字节重新同步
                    Statistics.AddError(FrameDecodeError.PayloadTooLarge);
                    RaiseError(FrameDecodeError.PayloadTooLarge, "LEN=" + payloadLength + " 超出上限，丢弃 1 字节重新同步",
                        TakeBuffer(Math.Min(_count, ProtocolFrame.OverheadLength)));
                    Statistics.AddResynchronizedBytes(1);
                    Discard(1);
                    continue;
                }

                int total = ProtocolFrame.OverheadLength + payloadLength;

                // 3) 帧还没收齐（半包），等待后续字节
                if (_count < total) return;

                // 4) 校验 CRC / 帧尾
                if (ProtocolFrame.TryDecode(_buffer, 0, total, out var frame, out var error))
                {
                    Discard(total);
                    Statistics.AddFrame();
                    FrameReceived?.Invoke(this, new FrameReceivedEventArgs(frame!));
                    continue;
                }

                Statistics.AddError(error);
                RaiseError(error, "帧解析失败：" + ProtocolFrame.Describe(error), TakeBuffer(total));
                Statistics.AddResynchronizedBytes(1);
                Discard(1);
            }
        }

        private int IndexOfStartOfFrame(int from)
        {
            for (int i = from; i < _count; i++)
            {
                if (_buffer[i] == ProtocolFrame.StartOfFrame) return i;
            }
            return -1;
        }

        private byte[] TakeBuffer(int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(_buffer, 0, copy, 0, count);
            return copy;
        }

        private void Discard(int count)
        {
            if (count <= 0) return;
            _count -= count;
            if (_count > 0)
            {
                Buffer.BlockCopy(_buffer, count, _buffer, 0, _count);
            }
        }

        private void RaiseError(FrameDecodeError error, string message, byte[] rawBytes)
        {
            var handler = FrameError;
            if (handler != null)
            {
                handler(this, new FrameErrorEventArgs(error, message, rawBytes));
            }
        }
    }
}
