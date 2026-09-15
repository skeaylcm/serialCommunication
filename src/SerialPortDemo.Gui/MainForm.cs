using System;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Windows.Forms;

namespace SerialPortDemo.Gui
{
    /// <summary>一个极简的输入框（读/写寄存器时用）。</summary>
    internal static class PromptDialog
    {
        public static string? Show(IWin32Window owner, string title, string label, string defaultValue)
        {
            using (var form = new Form())
            {
                form.Text = title;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterParent;
                form.ClientSize = new System.Drawing.Size(320, 116);
                form.MinimizeBox = false;
                form.MaximizeBox = false;

                var text = new Label { Text = label, Left = 12, Top = 14, Width = 288 };
                var input = new TextBox { Text = defaultValue, Left = 12, Top = 40, Width = 296 };
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 150, Top = 74, Width = 75 };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 233, Top = 74, Width = 75 };

                form.Controls.Add(text);
                form.Controls.Add(input);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                return form.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
            }
        }
    }

    /// <summary>
    /// 串口调试助手：选择端口 → 打开 → 收发报文。
    /// 收到的数据一方面按原始 HEX 显示，另一方面交给协议层解析成帧，
    /// 便于同时观察“字节流”和“协议帧”两个层次。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const int MaxLogLength = 200000;

        private readonly ComboBox _portCombo = new ComboBox();
        private readonly ComboBox _baudCombo = new ComboBox();
        private readonly ComboBox _dataBitsCombo = new ComboBox();
        private readonly ComboBox _parityCombo = new ComboBox();
        private readonly ComboBox _stopBitsCombo = new ComboBox();
        private readonly Button _refreshButton = new Button();
        private readonly Button _openButton = new Button();

        private readonly RichTextBox _receiveBox = new RichTextBox();
        private readonly CheckBox _hexDisplayCheck = new CheckBox();
        private readonly CheckBox _rawCheck = new CheckBox();
        private readonly CheckBox _timestampCheck = new CheckBox();
        private readonly Button _clearButton = new Button();
        private readonly Button _statsButton = new Button();

        private readonly TextBox _sendBox = new TextBox();
        private readonly CheckBox _sendHexCheck = new CheckBox();
        private readonly Button _sendButton = new Button();
        private readonly Button _pingButton = new Button();
        private readonly Button _readButton = new Button();
        private readonly Button _writeButton = new Button();

        private readonly Label _statusLabel = new Label();

        private Transport.SerialTransport? _transport;
        private Protocol.ProtocolSession? _session;

        public MainForm()
        {
            Text = "RS-232 串口通讯调试助手（C# Demo）";
            ClientSize = new System.Drawing.Size(880, 600);
            MinimumSize = new System.Drawing.Size(720, 480);
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;

            BuildLayout();

            Load += (sender, e) => RefreshPorts();
            FormClosing += (sender, e) => CloseSerial();
        }

        // ---------- 界面 ----------

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));

            layout.Controls.Add(BuildConnectionPanel(), 0, 0);
            layout.Controls.Add(BuildReceiveArea(), 0, 1);
            layout.Controls.Add(BuildSendArea(), 0, 2);
            layout.Controls.Add(BuildStatusBar(), 0, 3);

            Controls.Add(layout);
        }

        private Control BuildConnectionPanel()
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), WrapContents = false };

            _portCombo.DropDownStyle = ComboBoxStyle.DropDown;
            _portCombo.Width = 200;
            _refreshButton.Text = "刷新";
            _refreshButton.Width = 60;
            _refreshButton.Click += (sender, e) => RefreshPorts();

            foreach (var baud in new[] { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 })
            {
                _baudCombo.Items.Add(baud);
            }
            _baudCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _baudCombo.Width = 90;
            _baudCombo.SelectedItem = 115200;

            foreach (var bits in new[] { 5, 6, 7, 8 }) _dataBitsCombo.Items.Add(bits);
            _dataBitsCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _dataBitsCombo.Width = 50;
            _dataBitsCombo.SelectedItem = 8;

            foreach (var parity in new[] { Parity.None, Parity.Odd, Parity.Even, Parity.Mark, Parity.Space }) _parityCombo.Items.Add(parity);
            _parityCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _parityCombo.Width = 70;
            _parityCombo.SelectedItem = Parity.None;

            foreach (var stop in new[] { StopBits.One, StopBits.OnePointFive, StopBits.Two }) _stopBitsCombo.Items.Add(stop);
            _stopBitsCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _stopBitsCombo.Width = 60;
            _stopBitsCombo.SelectedItem = StopBits.One;

            _openButton.Text = "打开串口";
            _openButton.Width = 90;
            _openButton.Click += (sender, e) => ToggleSerial();

            panel.Controls.Add(Label("端口"));
            panel.Controls.Add(_portCombo);
            panel.Controls.Add(_refreshButton);
            panel.Controls.Add(Label("波特率"));
            panel.Controls.Add(_baudCombo);
            panel.Controls.Add(Label("数据位"));
            panel.Controls.Add(_dataBitsCombo);
            panel.Controls.Add(Label("校验"));
            panel.Controls.Add(_parityCombo);
            panel.Controls.Add(Label("停止位"));
            panel.Controls.Add(_stopBitsCombo);
            panel.Controls.Add(_openButton);
            return panel;
        }

        private Control BuildReceiveArea()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 0, 8, 0) };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };

            _hexDisplayCheck.Text = "HEX 显示";
            _hexDisplayCheck.Checked = true;
            _hexDisplayCheck.Width = 90;

            _rawCheck.Text = "显示原始字节";
            _rawCheck.Width = 110;

            _timestampCheck.Text = "时间戳";
            _timestampCheck.Checked = true;
            _timestampCheck.Width = 80;

            _clearButton.Text = "清空";
            _clearButton.Width = 60;
            _clearButton.Click += (sender, e) => _receiveBox.Clear();

            _statsButton.Text = "统计";
            _statsButton.Width = 60;
            _statsButton.Click += (sender, e) => ShowStatistics();

            toolbar.Controls.Add(_hexDisplayCheck);
            toolbar.Controls.Add(_rawCheck);
            toolbar.Controls.Add(_timestampCheck);
            toolbar.Controls.Add(_clearButton);
            toolbar.Controls.Add(_statsButton);

            _receiveBox.Dock = DockStyle.Fill;
            _receiveBox.ReadOnly = true;
            _receiveBox.BackColor = System.Drawing.Color.White;
            _receiveBox.Font = new System.Drawing.Font("Consolas", 9.5F);
            _receiveBox.WordWrap = false;
            _receiveBox.ScrollBars = RichTextBoxScrollBars.Both;
            _receiveBox.HideSelection = false;

            panel.Controls.Add(toolbar, 0, 0);
            panel.Controls.Add(_receiveBox, 0, 1);
            return panel;
        }

        private Control BuildSendArea()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));

            var quick = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            _pingButton.Text = "PING";
            _pingButton.Width = 70;
            _pingButton.Click += (sender, e) => DoPing();

            _readButton.Text = "读寄存器";
            _readButton.Width = 90;
            _readButton.Click += (sender, e) => DoReadRegisters();

            _writeButton.Text = "写寄存器";
            _writeButton.Width = 90;
            _writeButton.Click += (sender, e) => DoWriteRegister();

            quick.Controls.Add(Label("协议快捷命令："));
            quick.Controls.Add(_pingButton);
            quick.Controls.Add(_readButton);
            quick.Controls.Add(_writeButton);

            var sendRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            _sendBox.Width = 560;
            _sendBox.Font = new System.Drawing.Font("Consolas", 9.5F);
            _sendBox.Text = "AA 01 00 00 20 00 55";

            _sendHexCheck.Text = "HEX";
            _sendHexCheck.Checked = true;
            _sendHexCheck.Width = 50;

            _sendButton.Text = "发送";
            _sendButton.Width = 80;
            _sendButton.Click += (sender, e) => DoSend();

            sendRow.Controls.Add(Label("发送内容："));
            sendRow.Controls.Add(_sendBox);
            sendRow.Controls.Add(_sendHexCheck);
            sendRow.Controls.Add(_sendButton);

            var hint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "提示：勾选 HEX 时按十六进制发送（如 AA 01 00 00 20 00 55）；取消勾选按 UTF-8 文本发送。协议帧格式：AA | CMD | LEN(2,小端) | DATA | CRC16(2,小端) | 55。",
                ForeColor = System.Drawing.Color.DimGray,
            };

            panel.Controls.Add(quick, 0, 0);
            panel.Controls.Add(sendRow, 0, 1);
            panel.Controls.Add(hint, 0, 2);
            return panel;
        }

        private Control BuildStatusBar()
        {
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.Text = "未连接";
            _statusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            _statusLabel.Padding = new Padding(10, 0, 0, 0);
            return _statusLabel;
        }

        private static Label Label(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 6, 0, 0),
            };
        }

        // ---------- 连接 ----------

        private void RefreshPorts()
        {
            string? current = (_portCombo.SelectedItem as PortEntry)?.Name ?? _portCombo.Text;

            _portCombo.Items.Clear();
            var devices = Usb.UsbSerialDeviceEnumerator.Enumerate();
            if (devices.Count > 0)
            {
                foreach (var device in devices) _portCombo.Items.Add(new PortEntry(device.PortName, device.ToString()));
            }
            else
            {
                foreach (var name in Transport.SerialPortSettings.GetPortNames()) _portCombo.Items.Add(new PortEntry(name, name));
            }

            if (_portCombo.Items.Count > 0)
            {
                _portCombo.SelectedIndex = 0;
                if (!string.IsNullOrEmpty(current))
                {
                    for (int i = 0; i < _portCombo.Items.Count; i++)
                    {
                        if (string.Equals(((PortEntry)_portCombo.Items[i]).Name, current, StringComparison.OrdinalIgnoreCase))
                        {
                            _portCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            else
            {
                _portCombo.Text = string.Empty;
                AppendLine("未发现串口，请插入 USB 转串口设备并确认驱动已安装。", false);
            }
        }

        private string? SelectedPortName()
        {
            var entry = _portCombo.SelectedItem as PortEntry;
            if (entry != null && _portCombo.Text == entry.ToString()) return entry.Name;

            string text = _portCombo.Text.Trim();
            if (text.Length == 0) return null;

            // 允许直接输入 "COM3" 或粘贴 "COM3   USB-SERIAL CH340 (COM3)"
            int space = text.IndexOfAny(new[] { ' ', '\t' });
            return space > 0 ? text.Substring(0, space) : text;
        }

        private void ToggleSerial()
        {
            if (_session != null)
            {
                CloseSerial();
                return;
            }

            string? portName = SelectedPortName();
            if (string.IsNullOrEmpty(portName))
            {
                MessageBox.Show(this, "请先选择串口。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var settings = new Transport.SerialPortSettings(portName!)
            {
                BaudRate = (int)_baudCombo.SelectedItem,
                DataBits = (int)_dataBitsCombo.SelectedItem,
                Parity = (Parity)_parityCombo.SelectedItem,
                StopBits = (StopBits)_stopBitsCombo.SelectedItem,
            };

            try
            {
                var transport = new Transport.SerialTransport(settings);
                var session = new Protocol.ProtocolSession(transport, TimeSpan.FromSeconds(1));

                session.FrameReceived += OnFrameReceived;
                session.FrameError += OnFrameError;
                session.TransportError += OnTransportError;
                session.Closed += OnTransportClosed;
                transport.DataSent += OnRawSent;
                transport.DataReceived += OnRawReceived;

                session.Start();

                _transport = transport;
                _session = session;
                _openButton.Text = "关闭串口";
                SetParametersEnabled(false);
                AppendLine("已打开 " + settings, false);
                UpdateStatus();
            }
            catch (Exception ex)
            {
                AppendLine("打开失败：" + ex.Message, true);
                MessageBox.Show(this, ex.Message, "打开串口失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CloseSerial()
        {
            var session = _session;
            var transport = _transport;
            _session = null;
            _transport = null;

            if (session != null)
            {
                session.FrameReceived -= OnFrameReceived;
                session.FrameError -= OnFrameError;
                session.TransportError -= OnTransportError;
                session.Closed -= OnTransportClosed;
                session.Stop();
                session.Dispose();
            }

            if (transport != null)
            {
                transport.DataSent -= OnRawSent;
                transport.DataReceived -= OnRawReceived;
                transport.Dispose();
            }

            _openButton.Text = "打开串口";
            SetParametersEnabled(true);
            if (transport != null) AppendLine("串口已关闭。", false);
            UpdateStatus();
        }

        private void SetParametersEnabled(bool enabled)
        {
            _portCombo.Enabled = enabled;
            _baudCombo.Enabled = enabled;
            _dataBitsCombo.Enabled = enabled;
            _parityCombo.Enabled = enabled;
            _stopBitsCombo.Enabled = enabled;
            _refreshButton.Enabled = enabled;
        }

        // ---------- 收发 ----------

        private void DoSend()
        {
            if (_transport == null)
            {
                MessageBox.Show(this, "请先打开串口。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string text = _sendBox.Text;
            byte[] bytes;

            if (_sendHexCheck.Checked)
            {
                if (!Hex.TryParse(text, out bytes, out string? error))
                {
                    MessageBox.Show(this, "十六进制内容无效：" + error, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else
            {
                bytes = Encoding.UTF8.GetBytes(text);
            }

            if (bytes.Length == 0)
            {
                MessageBox.Show(this, "发送内容为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _transport.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                AppendLine("发送失败：" + ex.Message, true);
            }
        }

        private void DoPing()
        {
            var session = _session;
            if (session == null)
            {
                MessageBox.Show(this, "请先打开串口。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var uptime = session.Ping();
                AppendLine(string.Format("PING 成功，设备运行时间 {0:F3} 秒", uptime.TotalSeconds), false);
            }
            catch (Exception ex)
            {
                AppendLine("PING 失败：" + ex.Message, true);
            }
        }

        private void DoReadRegisters()
        {
            var session = _session;
            if (session == null)
            {
                MessageBox.Show(this, "请先打开串口。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string? addressText = PromptDialog.Show(this, "读保持寄存器", "起始地址（HEX）", "0000");
            if (addressText == null) return;
            if (!TryParseHex(addressText, out int address) || address > 0xFFFF)
            {
                AppendLine("地址无效：" + addressText, true);
                return;
            }

            string? countText = PromptDialog.Show(this, "读保持寄存器", "个数（1 ~ 125）", "1");
            if (countText == null) return;
            if (!int.TryParse(countText, out int count) || count < 1 || count > 125)
            {
                AppendLine("个数无效：" + countText, true);
                return;
            }

            try
            {
                var values = session.ReadHoldingRegisters((ushort)address, (byte)count);
                var builder = new StringBuilder();
                builder.Append("读取 ").Append(count).Append(" 个寄存器：");
                for (int i = 0; i < values.Length; i++)
                {
                    builder.Append("  [").Append((address + i).ToString("X4")).Append("]=0x").Append(values[i].ToString("X4"));
                }
                AppendLine(builder.ToString(), false);
            }
            catch (Exception ex)
            {
                AppendLine("读寄存器失败：" + ex.Message, true);
            }
        }

        private void DoWriteRegister()
        {
            var session = _session;
            if (session == null)
            {
                MessageBox.Show(this, "请先打开串口。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string? addressText = PromptDialog.Show(this, "写单个寄存器", "寄存器地址（HEX）", "0010");
            if (addressText == null) return;
            if (!TryParseHex(addressText, out int address) || address > 0xFFFF)
            {
                AppendLine("地址无效：" + addressText, true);
                return;
            }

            string? valueText = PromptDialog.Show(this, "写单个寄存器", "数值（HEX）", "1234");
            if (valueText == null) return;
            if (!TryParseHex(valueText, out int value) || value > 0xFFFF)
            {
                AppendLine("数值无效：" + valueText, true);
                return;
            }

            try
            {
                session.WriteRegister((ushort)address, (ushort)value);
                AppendLine(string.Format("写入成功：[{0}] = 0x{1:X4}", address.ToString("X4"), value), false);
            }
            catch (Exception ex)
            {
                AppendLine("写寄存器失败：" + ex.Message, true);
            }
        }

        private void ShowStatistics()
        {
            var session = _session;
            if (session == null)
            {
                MessageBox.Show(this, "尚未打开串口。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine("通道：" + session.Transport.Name);
            if (_transport is Transport.SerialTransport serial)
            {
                builder.AppendLine("累计发送：" + serial.BytesSent + " 字节");
                builder.AppendLine("累计接收：" + serial.BytesReceived + " 字节");
            }
            builder.AppendLine("协议解析：" + session.Statistics);

            MessageBox.Show(this, builder.ToString(), "收发统计", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------- 事件回调（后台线程 → UI 线程）----------

        private void OnFrameReceived(object? sender, Protocol.FrameReceivedEventArgs e)
        {
            string line = FormatTimestamp(e.Timestamp) + "收帧 " + e.Frame + Environment.NewLine
                          + "      " + e.Frame.ToHexString();
            RunOnUi(() => AppendLine(line, false));
        }

        private void OnFrameError(object? sender, Protocol.FrameErrorEventArgs e)
        {
            if (e.IsResynchronization) return;
            RunOnUi(() => AppendLine("帧错误：" + e.Message, true));
        }

        private void OnTransportError(object? sender, Transport.TransportErrorEventArgs e)
        {
            RunOnUi(() => AppendLine(e.Message, true));
        }

        private void OnTransportClosed(object? sender, EventArgs e)
        {
            RunOnUi(() =>
            {
                AppendLine("串口已关闭（设备可能已被拔出）。", true);
                if (_session != null) CloseSerial();
            });
        }

        private void OnRawSent(object? sender, Transport.TransportDataEventArgs e)
        {
            string line = FormatTimestamp(e.Timestamp) + "发送 " + e.Hex;
            RunOnUi(() => AppendLine(line, false));
        }

        private void OnRawReceived(object? sender, Transport.TransportDataEventArgs e)
        {
            var data = e.Data;

            if (!_rawCheck.Checked)
            {
                // 未勾选“显示原始字节”时只显示文本形式，便于看 ASCII 协议
                string text = ToDisplayText(data);
                string line = FormatTimestamp(e.Timestamp) + "接收 " + text;
                RunOnUi(() => AppendLine(line, false));
                return;
            }

            string hexLine = FormatTimestamp(e.Timestamp) + "接收 " + Hex.ToString(data);
            RunOnUi(() => AppendLine(hexLine, false));
        }

        private string ToDisplayText(byte[] data)
        {
            if (_hexDisplayCheck.Checked)
            {
                return Hex.ToString(data);
            }

            var builder = new StringBuilder(data.Length);
            foreach (byte value in data)
            {
                builder.Append(value >= 0x20 && value <= 0x7E ? (char)value : '.');
            }
            return builder.ToString();
        }

        private string FormatTimestamp(DateTime timestamp)
        {
            return _timestampCheck.Checked ? timestamp.ToString("HH:mm:ss.fff") + "  " : string.Empty;
        }

        private void RunOnUi(Action action)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (Exception)
            {
                // 窗体正在关闭时忽略
            }
        }

        private void AppendLine(string text, bool isWarning)
        {
            if (_receiveBox.TextLength > MaxLogLength) _receiveBox.Clear();

            _receiveBox.SelectionStart = _receiveBox.TextLength;
            _receiveBox.SelectionLength = 0;
            _receiveBox.SelectionColor = isWarning ? System.Drawing.Color.Firebrick : System.Drawing.Color.Black;
            _receiveBox.AppendText(text + Environment.NewLine);
            _receiveBox.SelectionColor = System.Drawing.Color.Black;
            _receiveBox.SelectionStart = _receiveBox.TextLength;
            _receiveBox.ScrollToCaret();

            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_session == null)
            {
                _statusLabel.Text = "未连接";
                return;
            }

            _statusLabel.Text = string.Format("已连接 {0}  |  发送 {1} 字节  接收 {2} 字节  |  {3}",
                _session.Transport.Name,
                _transport?.BytesSent ?? 0,
                _transport?.BytesReceived ?? 0,
                _session.Statistics);
        }

        private static bool TryParseHex(string text, out int value)
        {
            value = 0;
            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(2);
            if (trimmed.Length == 0) return false;
            return int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>端口下拉项：显示“COM3  USB-SERIAL CH340 (COM3)”，值为“COM3”。</summary>
        private sealed class PortEntry
        {
            public PortEntry(string name, string display)
            {
                Name = name;
                Display = display;
            }

            public string Name { get; }

            private string Display { get; }

            public override string ToString() => Display;
        }
    }
}
