using System;

namespace SerialPortDemo.Demo
{
    /// <summary>字节数组比较等小工具。</summary>
    internal static class Bytes
    {
        public static bool AreEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }
            return true;
        }
    }

    /// <summary>统一带锁的日志输出，避免后台接收线程与菜单输出互相覆盖。</summary>
    internal static class Log
    {
        private static readonly object Sync = new object();

        public static void Write(string text)
        {
            lock (Sync)
            {
                Console.Write(text);
            }
        }

        public static void Line(string text)
        {
            lock (Sync)
            {
                Console.WriteLine(text);
            }
        }

        public static void Info(string text) => Line(text);

        public static void Warn(string text)
        {
            var previous = Console.ForegroundColor;
            lock (Sync)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[警告] " + text);
                Console.ForegroundColor = previous;
            }
        }

        public static void Error(string text)
        {
            var previous = Console.ForegroundColor;
            lock (Sync)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[错误] " + text);
                Console.ForegroundColor = previous;
            }
        }
    }
}
