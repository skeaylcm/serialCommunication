using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;

namespace SerialPortDemo.Usb
{
    /// <summary>通过 USB 接入的串口设备信息。</summary>
    public sealed class UsbSerialDevice
    {
        public UsbSerialDevice(string portName, string caption, string deviceId, string? manufacturer)
        {
            PortName = portName;
            Caption = caption;
            DeviceId = deviceId;
            Manufacturer = manufacturer;
        }

        /// <summary>COM 口名，如 COM3。</summary>
        public string PortName { get; }

        /// <summary>设备友好名，如 "USB-SERIAL CH340 (COM3)"。</summary>
        public string Caption { get; }

        /// <summary>PNP 设备实例 ID，如 "USB\VID_1A86&PID_7523\6&2A6E1B7&0&1"。</summary>
        public string DeviceId { get; }

        public string? Manufacturer { get; }

        /// <summary>是否 USB 总线设备（USB 转串口芯片；主板自带的 232 口为 false）。</summary>
        public bool IsUsbDevice => DeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);

        public override string ToString()
        {
            return PortName + "  " + Caption + (IsUsbDevice ? "  [USB]" : "  [板载/其它]");
        }
    }

    /// <summary>
    /// 用 WMI 查询系统里带 COM 口名的 PNP 设备，用于把
    /// “COM3” 显示成 “USB-SERIAL CH340 (COM3)”，方便在插了多个 USB 转串口时确认端口。
    /// </summary>
    public static class UsbSerialDeviceEnumerator
    {
        private static readonly Regex PortRegex = new Regex(@"\((COM\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>枚举所有带 COM 口名的设备；查询失败时返回空集合（不抛异常）。</summary>
        public static IReadOnlyList<UsbSerialDevice> Enumerate()
        {
            var result = new List<UsbSerialDevice>();

            try
            {
                const string query = "SELECT Caption, DeviceID, Manufacturer FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%)'";
                using (var searcher = new ManagementObjectSearcher(query))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementBaseObject device in collection)
                    {
                        string caption = device["Caption"] as string ?? string.Empty;
                        var match = PortRegex.Match(caption);
                        if (!match.Success) continue;

                        result.Add(new UsbSerialDevice(
                            match.Groups[1].Value.ToUpperInvariant(),
                            caption,
                            device["DeviceID"] as string ?? string.Empty,
                            device["Manufacturer"] as string));
                    }
                }
            }
            catch (Exception)
            {
                // 某些精简系统 / 权限受限环境不支持 WMI 查询，返回空列表即可
            }

            result.Sort((x, y) => string.CompareOrdinal(x.PortName, y.PortName));
            return result;
        }

        /// <summary>把串口名翻译成带设备友好名的描述；查不到时原样返回。</summary>
        public static string Describe(string portName)
        {
            foreach (var device in Enumerate())
            {
                if (string.Equals(device.PortName, portName, StringComparison.OrdinalIgnoreCase))
                {
                    return device.ToString();
                }
            }
            return portName;
        }
    }
}
