// XMSPE02 陪伴机 AT 调试工具 (.NET Framework 4.x WinForms)
// 编译: csc /target:winexe /out:xmspe02_at_tool.exe Xmspe02AtTool.cs
// 功能: 串口连接 -> 读取 IMEI / 信号强度 / 运营商 / 设备信息
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Xmspe02AtTool
{
    public class MainForm : Form
    {
        private SerialPort _port;
        private readonly object _lock = new object();

        private ComboBox _cmbBaud;
        private Button _btnConnect;
        private Label _lblPort;
        private Button _btnSms;
        private CheckBox _chkAuto;
        private Button _btnLogBar;
        private Panel _logPanel;
        private GroupBox _grpInfo;
        private bool _logExpanded;
        private TextBox _txtNumber, _txtMessage;
        private Label _lblImei, _lblSignal, _lblCops, _lblDevInfo, _lblDevDetail;
        private TextBox _txtLog;
        private System.Windows.Forms.Timer _pollTimer;
        private System.Windows.Forms.Timer _detectTimer;
        private System.Windows.Forms.Timer _keepAliveTimer;
        private string _detectedPort;
        private bool _driverHintShown;

        public MainForm()
        {
            Text = "喜马拉雅XMSPE02短信猫调试工具";
            // 窗口/任务栏图标：读取 exe 内嵌的 ico 资源
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            // 内容高度 = 连接64 + 信息192 + 短信110 + 日志按钮30 + 余量8，窗口刚好收满无大片空白
            MinimumSize = new Size(620, 445);
            ClientSize = new Size(620, 404);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9F);
            BuildUi();
            RefreshPorts();
        }

        private void BuildUi()
        {
            // ---- 连接区（自动检测设备 + 状态按钮）----
            var grpConn = new GroupBox { Text = " 设备连接 ", Dock = DockStyle.Top, Height = 64, Padding = new Padding(10) };
            var lblDev = new Label { Text = "设备:", Left = 18, Top = 27, AutoSize = true };
            _lblPort = new Label { Text = "检测中...", Left = 60, Top = 27, AutoSize = true, ForeColor = Color.Gray };
            var lblBaud = new Label { Text = "波特率:", Left = 250, Top = 27, AutoSize = true };
            _cmbBaud = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, Left = 308, Top = 24 };
            _cmbBaud.Items.AddRange(new object[] { "9600", "115200", "57600", "38400", "19200" });
            _cmbBaud.SelectedItem = "9600";
            _btnConnect = new Button { Text = "● 未连接", Left = 420, Top = 23, Width = 130, Enabled = false };
            _btnConnect.Click += (s, e) => ToggleConnect();
            grpConn.Controls.AddRange(new Control[] { lblDev, _lblPort, lblBaud, _cmbBaud, _btnConnect });

            // ---- 短信区 ----
            var grpSms = new GroupBox { Text = " 发送短信 ", Dock = DockStyle.Top, Height = 110, Padding = new Padding(10) };
            var lblNum = new Label { Text = "号码:", Left = 15, Top = 27, AutoSize = true };
            _txtNumber = new TextBox { Left = 60, Top = 24, Width = 180 };
            _btnSms = new Button { Text = "发送短信", Left = 250, Top = 23, Width = 90, Enabled = false };
            var lblMsg = new Label { Text = "内容:", Left = 15, Top = 55, AutoSize = true };
            _txtMessage = new TextBox { Left = 60, Top = 52, Width = 420, Height = 45, Multiline = true };
            _btnSms.Click += (s, e) => { _btnSms.Enabled = false; RunAsync(SendSms); };
            grpSms.Controls.AddRange(new Control[] { lblNum, _txtNumber, _btnSms, lblMsg, _txtMessage });

            // ---- 信息区（固定展开，位于发送短信上方）----
            _grpInfo = new GroupBox { Text = " 信息 ", Dock = DockStyle.Top, Height = 192, Padding = new Padding(10) };
            _lblImei = new Label { Text = "IMEI: --", Left = 15, Top = 24, AutoSize = true, Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold) };
            _lblSignal = new Label { Text = "信号: --", Left = 15, Top = 54, AutoSize = true, Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold) };
            _chkAuto = new CheckBox { Text = "自动刷新", Left = 460, Top = 55, AutoSize = true, Enabled = false, Checked = true };
            _chkAuto.CheckedChanged += (s, e) => ChkAutoChanged();
            _lblCops = new Label { Text = "运营商: --", Left = 15, Top = 84, AutoSize = true, Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold) };
            _lblDevInfo = new Label { Text = "设备信息: --", Left = 15, Top = 114, AutoSize = true, Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold) };
            _lblDevDetail = new Label { Text = "", Left = 15, Top = 140, AutoSize = true, ForeColor = Color.DimGray };
            _grpInfo.Controls.AddRange(new Control[] { _lblImei, _lblSignal, _chkAuto, _lblCops, _lblDevInfo, _lblDevDetail });

            // ---- AT 日志区（条形按钮 + 日志面板，默认折叠）----
            _logPanel = new Panel { Dock = DockStyle.Bottom, Height = 0, Padding = new Padding(10) };
            _txtLog = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                BackColor = Color.White
            };
            _logPanel.Controls.Add(_txtLog);
            _btnLogBar = new Button
            {
                Text = "▸ AT 日志（点击展开）",
                Dock = DockStyle.Bottom,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0)
            };
            _btnLogBar.Click += (s, e) => ToggleLog();

            // Dock 布局：后添加的先布局。视觉顺序: 连接(顶) → 信息 → 短信 → 日志面板 → 日志按钮(底)
            Controls.Add(grpSms);
            Controls.Add(_grpInfo);
            Controls.Add(grpConn);
            Controls.Add(_logPanel);
            Controls.Add(_btnLogBar);
            Resize += (s, e) => AdjustLogHeight();

            // 轮询定时器
            _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _pollTimer.Tick += (s, e) => RunAsync(QuerySignal);
            // 保活定时器：防止设备进入休眠（每 25 秒发一次空 AT，不写日志）
            _keepAliveTimer = new System.Windows.Forms.Timer { Interval = 25000 };
            _keepAliveTimer.Tick += (s, e) => { if (_port != null && _port.IsOpen) RunAsync(() => SendAt("AT", 2, false)); };
            FormClosing += (s, e) => { _pollTimer.Stop(); _detectTimer.Stop(); _keepAliveTimer.Stop(); SafeClose(); };
        }

        /// <summary>从注册表 SERIALCOMM 中检测 Quectel/ASR 设备对应的 COM 口</summary>
        private string DetectDevicePort()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
                {
                    if (key == null) return null;
                    foreach (var name in key.GetValueNames())
                    {
                        string value = key.GetValue(name) as string;
                        // 设备名形如 \Device\QuectelModemDevice
                        if (value != null && (name.IndexOf("Quectel", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("2ECC", StringComparison.OrdinalIgnoreCase) >= 0))
                            return value;
                    }
                    // 兜底：已知 COM7
                    foreach (var name in key.GetValueNames())
                    {
                        string value = key.GetValue(name) as string;
                        if (value == "COM7") return value;
                    }
                }
            }
            catch { }
            return null;
        }

        private void RefreshPorts()
        {
            // 启动自动检测定时器：每 1.5 秒检测一次设备，自动连接/断开
            _detectTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _detectTimer.Tick += (s, e) => AutoDetectTick();
            _detectTimer.Start();
            AutoDetectTick();
        }

        private void AutoDetectTick()
        {
            string port = DetectDevicePort();
            bool connected = _port != null && _port.IsOpen;

            if (port == null)
            {
                _detectedPort = null;
                SafeInvoke(() =>
                {
                    _lblPort.Text = "未检测到设备";
                    _lblPort.ForeColor = Color.Firebrick;
                });
                if (connected)
                {
                    _pollTimer.Stop();
                    SafeClose();
                    SetUiState(false);
                    SafeInvoke(() => Log("[连接] 设备已拔出，自动断开"));
                }
                LogDriverHintOnce();
                return;
            }

            _driverHintShown = false;   // 设备已找到，重新允许下次缺失时提示

            if (port != _detectedPort)
            {
                _detectedPort = port;
                SafeInvoke(() =>
                {
                    _lblPort.Text = port + "（Quectel Modem）";
                    _lblPort.ForeColor = Color.DarkGreen;
                });
                SafeInvoke(() => Log("[检测] 发现设备: " + port));
            }

            if (!connected)
            {
                SafeInvoke(() => Log("[连接] 自动连接 " + port + " ..."));
                TryConnect(port);
            }
        }

        /// <summary>未检测到设备时，在日志区提示一次驱动安装方法和下载链接</summary>
        private void LogDriverHintOnce()
        {
            if (_driverHintShown) return;
            _driverHintShown = true;
            SafeInvoke(() =>
            {
                Log("[提示] 未检测到设备。若设备已用数据线连好，可能是串口驱动未安装，处理方法：");
                Log("  安装 Quectel（ASR 平台）USB 驱动后重新插拔设备：");
                Log("     https://developer.quectel.com/wp-content/uploads/2024/09/Quectel_Windows_USB_DriverA_Customer_V1.1.13.zip");
            });
        }

        private void TryConnect(string portName)
        {
            try
            {
                _port = new SerialPort(portName, int.Parse(_cmbBaud.SelectedItem.ToString()))
                { ReadTimeout = 200, WriteTimeout = 500 };
                _port.Open();
                _port.DiscardInBuffer();
                var (ok, _) = SendAt("AT", 2);
                if (!ok) throw new Exception("设备无响应（AT 指令未返回 OK）");
                SetUiState(true);
                SafeInvoke(() => Log(string.Format("[连接] {0} 连接成功，设备响应正常", portName)));
                SafeInvoke(() => Log("[保活] 已发送 AT+QSCLK=0 关闭模组休眠，并每 25 秒自动发送空 AT 保持活跃"));
                // 连接成功后关闭休眠，再自动读取 IMEI / 运营商 / 信号 / 设备信息（顺序执行避免串口竞争）
                RunAsync(() => { SendAt("AT+QSCLK=0", 3); QueryImei(); QueryOperator(); QuerySignal(); QueryDeviceInfo(); });
            }
            catch (Exception ex)
            {
                SafeClose();
                SafeInvoke(() => Log("[连接] 自动连接失败: " + ex.Message + "（将自动重试）"));
            }
        }

        private void ToggleConnect()
        {
            if (_port != null && _port.IsOpen)
            {
                _pollTimer.Stop();
                SafeClose();
                SetUiState(false);
                Log("[连接] 已手动断开");
                _btnConnect.Text = "● 未连接";
                _btnConnect.ForeColor = Color.Firebrick;
                return;
            }
            if (_detectedPort != null)
            {
                TryConnect(_detectedPort);
            }
        }

        private void SetUiState(bool opened)
        {
            _btnConnect.Text = opened ? "● 已连接" : "● 未连接";
            _btnConnect.ForeColor = opened ? Color.Green : Color.Firebrick;
            _chkAuto.Enabled = opened;
            _btnSms.Enabled = opened;
            if (opened)
            {
                _keepAliveTimer.Start();
                // 勾选了自动刷新（默认勾选）则连接后立即开始信号轮询
                if (_chkAuto.Checked)
                {
                    RunAsync(QuerySignal);
                    _pollTimer.Start();
                }
            }
            else
            {
                _keepAliveTimer.Stop();
                _pollTimer.Stop();   // 勾选状态保留，重连后自动恢复刷新
            }
        }

        private void SafeClose()
        {
            try { if (_port != null && _port.IsOpen) _port.Close(); } catch { }
            _port = null;
        }

        // ---------- AT 收发 ----------
        private Tuple<bool, string> SendAt(string cmd, double timeoutSec = 3.0, bool logIt = true)
        {
            lock (_lock)
            {
                if (_port == null || !_port.IsOpen) return Tuple.Create(false, "串口未连接");
                try
                {
                    _port.DiscardInBuffer();
                    _port.Write(cmd + "\r");
                }
                catch (Exception ex) { return Tuple.Create(false, "发送失败: " + ex.Message); }

                var buf = new System.Text.StringBuilder();
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        string chunk = _port.ReadExisting();
                        if (chunk.Length > 0) buf.Append(chunk);
                    }
                    catch { }
                    string text = buf.ToString();
                    if (text.Contains("OK") || text.Contains("ERROR")) break;
                    Thread.Sleep(50);
                }
                string resp = buf.ToString().Trim();
                string cleaned = CleanResp(resp);
                if (logIt) SafeInvoke(() => Log(">> " + cmd + Environment.NewLine + "<< " + cleaned));
                return Tuple.Create(resp.Contains("OK"), resp);
            }
        }

        private void RunAsync(Action action)
        {
            new Thread(() => { try { action(); } catch (Exception ex) { SafeInvoke(() => Log("[异常] " + ex.Message)); } })
            { IsBackground = true }.Start();
        }

        private void SafeInvoke(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        // ---------- 功能 ----------
        private void QueryImei()
        {
            var r = SendAt("AT+CGSN", 3);
            string imei = "";
            foreach (var line in r.Item2.Split('\n'))
            {
                string s = line.Trim().Trim('"').Trim();
                if (s.Length == 15 && IsDigits(s)) { imei = s; break; }
            }
            if (imei.Length > 0) SafeInvoke(() => _lblImei.Text = "IMEI: " + imei);
            else SafeInvoke(() => Log("[IMEI] 读取失败"));
        }

        private static bool IsDigits(string s)
        {
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        private void QuerySignal()
        {
            // 静默查询：自动刷新/启动读取时不把 CSQ 交互写进日志
            var r = SendAt("AT+CSQ", 3, false);
            foreach (var line in r.Item2.Split('\n'))
            {
                string s = line.Trim();
                if (s.StartsWith("+CSQ:"))
                {
                    try
                    {
                        string[] parts = s.Substring(5).Split(',');
                        int csq = int.Parse(parts[0].Trim());
                        string ber = parts.Length > 1 ? parts[1].Trim() : "?";
                        string dbm = csq == 99 ? "未知" : string.Format("{0} dBm", -113 + csq * 2);
                        string bars = csq == 99 ? "" : new string('█', Math.Max(1, (csq + 4) / 5)) + new string('░', 10 - Math.Max(1, (csq + 4) / 5));
                        SafeInvoke(() => _lblSignal.Text = string.Format("信号: CSQ={0} ({1}) {2}", csq, dbm, bars));
                        return;
                    }
                    catch { }
                }
            }
            SafeInvoke(() => Log("[信号] 读取失败"));
        }

        private void QueryOperator()
        {
            // 1. 先查 SIM 状态（无卡时该固件 COPS? 不返回运营商）
            var cpin = SendAt("AT+CPIN?", 3);
            string cresp = cpin.Item2;
            if (cresp.Contains("CME ERROR: 10") || cresp.Contains("SIM not inserted"))
            {
                SafeInvoke(() => _lblCops.Text = "运营商: 未插卡（请插入移动 4G 卡）");
                return;
            }
            if (!cpin.Item1)
            {
                SafeInvoke(() => _lblCops.Text = "运营商: SIM 状态异常");
                return;
            }

            // 2. 查注册运营商
            var r = SendAt("AT+COPS?", 8);
            foreach (var line in r.Item2.Split('\n'))
            {
                string s = line.Trim();
                if (s.StartsWith("+COPS:"))
                {
                    string[] parts = s.Substring(6).Split(',');
                    string oper = parts.Length >= 3 ? parts[2].Trim().Trim('"') : "";
                    string mode = parts.Length >= 1 ? parts[0].Trim() : "";
                    string net = parts.Length >= 4 ? parts[3].Trim() : "";
                    if (oper.Length > 0)
                    {
                        string name = OperatorName(oper);
                        string netName = net == "7" ? "LTE" : net == "2" ? "GSM" : net == "3" ? "TD-SCDMA" : net == "4" ? "WCDMA" : net;
                        SafeInvoke(() => _lblCops.Text = string.Format("运营商: {0} ({1}, {2})", name, oper, netName));
                    }
                    else
                    {
                        SafeInvoke(() => _lblCops.Text = "运营商: 已插卡，尚未注册上网络");
                    }
                    return;
                }
            }
            SafeInvoke(() => Log("[运营商] 读取失败"));
        }

        private static string OperatorName(string code)
        {
            switch (code)
            {
                case "46000": case "46002": case "46004": case "46007": return "中国移动";
                case "46001": case "46006": case "46009": return "中国联通";
                case "46003": case "46005": case "46011": return "中国电信";
                case "46015": return "中国广电";
                default: return code + "（未知编码）";
            }
        }

        private void QueryDeviceInfo()
        {
            var man = SendAt("AT+CGMI", 3);   // 厂商
            var mod = SendAt("AT+CGMM", 3);   // 型号
            var ver = SendAt("AT+CGMR", 3);   // 固件版本
            string manu = ExtractInfoLine(man.Item2);
            string model = ExtractInfoLine(mod.Item2);
            string fw = ExtractInfoLine(ver.Item2);
            if (fw.Length > 70) fw = fw.Substring(0, 70) + "...";
            if (manu.Length > 0 || model.Length > 0 || fw.Length > 0)
            {
                string line1 = string.Format("厂商: {0}    型号: {1}", manu.Length > 0 ? manu : "--", model.Length > 0 ? model : "--");
                string line2 = "固件: " + (fw.Length > 0 ? fw : "--");
                SafeInvoke(() => _lblDevDetail.Text = line1 + Environment.NewLine + line2);
            }
            else SafeInvoke(() => Log("[设备信息] 读取失败"));
        }

        /// <summary>从 AT 响应中提取有效信息值：剔除回显/OK/ERROR/CME，去掉 +CGMI/+CGMM/+CGMR 前缀和引号</summary>
        private static string ExtractInfoLine(string resp)
        {
            string[] stripPrefixes = { "+CGMI", "+CGMM", "+CGMR", "+CGSN" };
            foreach (var raw in resp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = raw.Trim();
                if (s.StartsWith("AT+")) continue;
                if (s == "OK" || s.StartsWith("ERROR") || s.StartsWith("+CME")) continue;
                foreach (var p in stripPrefixes)
                {
                    if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    {
                        // 去掉前缀和冒号，再清理引号与空白（如 +CGMI: "ASR" -> ASR）
                        s = s.Substring(p.Length).TrimStart(':', ' ', '\t').Trim().Trim('"').Trim();
                        break;
                    }
                }
                if (s.Length == 0) continue;   // 该行只剩前缀（值在下一行），继续取
                return s;
            }
            return "";
        }

        private void ChkAutoChanged()
        {
            if (_chkAuto.Checked)
            {
                // 未连接时保持勾选，等连接成功后由 SetUiState 自动开始刷新
                if (_port != null && _port.IsOpen)
                {
                    RunAsync(QuerySignal);
                    _pollTimer.Start();
                }
            }
            else
            {
                _pollTimer.Stop();
            }
        }

        // ---------- 折叠/展开 ----------
        private const int LogExpandedH = 260;   // 日志展开后的面板高度

        private void ToggleLog()
        {
            _logExpanded = !_logExpanded;
            _btnLogBar.Text = _logExpanded ? "▾ AT 日志（点击收起）" : "▸ AT 日志（点击展开）";
            int compactH = GetTopTotal() + _btnLogBar.Height + 8;
            int delta = Math.Max(0, Height - ClientSize.Height);   // 非客户区高度（标题栏+边框）

            if (_logExpanded)
            {
                _txtLog.Visible = true;
                // 窗口不够高时自动增高，保证日志区完整可见
                int need = compactH + LogExpandedH;
                MinimumSize = new Size(MinimumSize.Width, need + delta);
                if (ClientSize.Height < need)
                    ClientSize = new Size(ClientSize.Width, need);
                AdjustLogHeight();
            }
            else
            {
                _logPanel.Height = 0;
                _txtLog.Visible = false;
                // 恢复紧凑高度
                MinimumSize = new Size(MinimumSize.Width, compactH + delta);
                if (ClientSize.Height > compactH)
                    ClientSize = new Size(ClientSize.Width, compactH);
            }
        }

        /// <summary>窗口尺寸变化时，日志面板自动填满剩余高度</summary>
        private void AdjustLogHeight()
        {
            if (!_logExpanded) return;
            int fill = ClientSize.Height - GetTopTotal() - _btnLogBar.Height - 8;
            _logPanel.Height = Math.Max(120, fill);
        }

        private int GetTopTotal()
        {
            int topTotal = 0;
            foreach (Control c in Controls)
                if (c.Dock == DockStyle.Top) topTotal += c.Height;
            return topTotal;
        }

        // ---------- 发送短信（PDU 模式，UCS2 编码，兼容中文/英文/长短信） ----------

        /// <summary>发送 CMGS 并等待 '>' 提示符（不等待 OK）</summary>
        private bool SendAtWaitPrompt(string cmd, double timeoutSec = 8.0)
        {
            lock (_lock)
            {
                if (_port == null || !_port.IsOpen) return false;
                try
                {
                    _port.DiscardInBuffer();
                    _port.Write(cmd + "\r");
                }
                catch { return false; }

                var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
                var buf = new System.Text.StringBuilder();
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        string chunk = _port.ReadExisting();
                        if (chunk.Length > 0) buf.Append(chunk);
                    }
                    catch { }
                    string t = buf.ToString();
                    if (t.Contains(">")) return true;
                    if (t.Contains("ERROR")) return false;
                    Thread.Sleep(50);
                }
                return false;
            }
        }

        /// <summary>短信正文发送后等待提交结果（+CMGS: &lt;mr&gt; / OK / ERROR）</summary>
        private string WaitSmsResult(double timeoutSec = 30.0)
        {
            lock (_lock)
            {
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
                var buf = new System.Text.StringBuilder();
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        string chunk = _port.ReadExisting();
                        if (chunk.Length > 0) buf.Append(chunk);
                    }
                    catch { }
                    string t = buf.ToString();
                    if (t.Contains("+CMGS:") || t.Contains("ERROR")) return t.Trim();
                    Thread.Sleep(100);
                }
                return buf.ToString().Trim();
            }
        }

        /// <summary>号码转为半八位组（swapped nibbles），并返回处理后的号码位数</summary>
        private static string ToSemiOctet(string digits)
        {
            if (digits.Length % 2 == 1) digits += "F";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < digits.Length; i += 2)
                sb.Append(digits[i + 1]).Append(digits[i]);
            return sb.ToString();
        }

        /// <summary>构造 SMS-SUBMIT PDU（UCS2 编码）。支持长短信自动分条（UDH）。</summary>
        private static List<string> BuildSubmitPdus(string number, string text, byte msgRef)
        {
            // 号码标准化：国内手机号自动加 86，带 + 号去 + 号
            string digits = number.Replace(" ", "").Replace("-", "");
            if (digits.StartsWith("+")) digits = digits.Substring(1);
            if (digits.StartsWith("86") == false && digits.Length == 11 && digits.StartsWith("1"))
                digits = "86" + digits;
            int daDigitCount = digits.Length;
            string da = ToSemiOctet(digits);   // TON 国际 / NPI ISDN = 91

            var result = new List<string>();
            byte[] bodyBytes = System.Text.Encoding.BigEndianUnicode.GetBytes(text);
            const int UdhLen = 6;                          // 长短信头长度
            int maxPartBytes = 140 - UdhLen;               // 每条 UD 上限 140 字节
            int totalLen = bodyBytes.Length;
            bool concat = totalLen > 140;
            int partCount = concat
                ? (totalLen + maxPartBytes - 1) / maxPartBytes
                : 1;
            int partSize = concat ? maxPartBytes : 140;

            for (int p = 0; p < partCount; p++)
            {
                int offset = p * partSize;
                int len = Math.Min(partSize, totalLen - offset);
                byte udhLen = 0;
                if (concat)
                {
                    // 拼接 UD 内容：UDH(6字节) + 本段正文
                    var part = new byte[UdhLen + len];
                    part[0] = 0x05; part[1] = 0x00; part[2] = 0x03;
                    part[3] = msgRef; part[4] = (byte)partCount; part[5] = (byte)(p + 1);
                    Array.Copy(bodyBytes, offset, part, UdhLen, len);
                    bodyBytes = null; // 防误用
                    var pduUd = part;
                    var pdu = new System.Text.StringBuilder();
                    pdu.Append("00");                                   // SCA: 使用 SIM 默认短信中心
                    pdu.Append("51");                                   // SMS-SUBMIT + UDHI(长短信)
                    pdu.Append("00");                                   // TP-MR
                    pdu.Append(daDigitCount.ToString("X2"));            // TP-DA 长度（位数）
                    pdu.Append("91").Append(da);                        // TP-DA 地址
                    pdu.Append("00");                                   // TP-PID
                    pdu.Append("08");                                   // TP-DCS: UCS2
                    pdu.Append("AA");                                   // TP-VP: 24 小时
                    pdu.Append(pduUd.Length.ToString("X2"));            // TP-UDL: UD 总字节数(含 UDH)
                    foreach (byte b in pduUd) pdu.Append(b.ToString("X2"));
                    result.Add(pdu.ToString());
                    continue;
                }

                var ud = new byte[len];
                Array.Copy(bodyBytes, offset, ud, 0, len);
                var s = new System.Text.StringBuilder();
                s.Append("00");                                         // SCA
                s.Append("11");                                         // SMS-SUBMIT, VPF=relative
                s.Append("00");                                         // TP-MR
                s.Append(daDigitCount.ToString("X2"));
                s.Append("91").Append(da);
                s.Append("00");                                         // TP-PID
                s.Append("08");                                         // TP-DCS: UCS2
                s.Append("AA");                                         // TP-VP
                s.Append(ud.Length.ToString("X2"));                     // TP-UDL
                foreach (byte b in ud) s.Append(b.ToString("X2"));
                result.Add(s.ToString());
            }
            return result;
        }

        private void SendSms()
        {
            // 同步读取界面输入（异步 BeginInvoke 会导致检查时值还没被赋上）
            string number = "", message = "";
            if (InvokeRequired) Invoke(new Action(() => { number = _txtNumber.Text.Trim(); message = _txtMessage.Text; }));
            else { number = _txtNumber.Text.Trim(); message = _txtMessage.Text; }

            if (number.Length == 0 || message.Length == 0)
            {
                SafeInvoke(() =>
                {
                    MessageBox.Show("请填写接收号码和短信内容", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _btnSms.Enabled = true;
                });
                return;
            }

            SafeInvoke(() => Log(string.Format("[短信] 准备发送到 {0}，内容 {1} 字符（PDU/UCS2 模式）", number, message.Length)));

            // 1. PDU 模式
            var r = SendAt("AT+CMGF=0", 3);
            if (!r.Item1)
            {
                SafeInvoke(() =>
                {
                    Log("[短信] 切换 PDU 模式失败: " + r.Item2);
                    _btnSms.Enabled = true;
                });
                return;
            }

            // 2. 逐条发送
            byte msgRef = (byte)(DateTime.Now.Millisecond & 0xFF);
            var pdus = BuildSubmitPdus(number, message, msgRef);
            bool allOk = true;
            for (int i = 0; i < pdus.Count; i++)
            {
                string pdu = pdus[i];
                int byteLen = pdu.Length / 2;
                string cmd = "AT+CMGS=" + byteLen;
                SafeInvoke(() => Log(string.Format(">> {0}（PDU 长度 {1} 字节，第 {2}/{3} 条）", cmd, byteLen, i + 1, pdus.Count)));

                if (!SendAtWaitPrompt(cmd, 8))
                {
                    SafeInvoke(() => Log("[短信] 未收到 > 提示符"));
                    allOk = false;
                    break;
                }

                lock (_lock)
                {
                    try
                    {
                        _port.Write(pdu);
                        _port.Write(new byte[] { 0x1A }, 0, 1);
                    }
                    catch (Exception ex)
                    {
                        SafeInvoke(() => Log("[短信] 写入 PDU 失败: " + ex.Message));
                        allOk = false;
                        break;
                    }
                }

                string result = WaitSmsResult(30);
                string res = result;
                SafeInvoke(() => Log("<< " + res));
                if (!result.Contains("+CMGS:")) { allOk = false; break; }
            }

            SafeInvoke(() =>
            {
                if (allOk)
                {
                    Log("[短信] ✔ 发送成功！");
                    MessageBox.Show(pdus.Count > 1
                        ? string.Format("长短信已分 {0} 条发送成功！", pdus.Count)
                        : "短信发送成功！", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    Log("[短信] ✘ 发送失败");
                    MessageBox.Show("短信发送失败，详见日志", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                _btnSms.Enabled = true;
            });
        }

        private void Log(string text)
        {
            _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
            // 防止日志无限增长
            if (_txtLog.TextLength > 80000)
            {
                int cut = _txtLog.Text.IndexOf("\r\n", 40000);
                if (cut > 0) _txtLog.Select(0, cut + 2);
                _txtLog.ReadOnly = false;
                _txtLog.Cut();
                _txtLog.ReadOnly = true;
            }
        }

        /// <summary>过滤设备主动推送的 URC 噪音，只保留对人有意义的内容</summary>
        private static string CleanResp(string resp)
        {
            if (string.IsNullOrEmpty(resp)) return resp;
            string[] noisePrefixes = { "+EEMLTE", "+CIREGU", "*PRXYREGSTATE", "+MPBK", "+EEGPRS", "+EEMGPRS" };
            var lines = resp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();
            foreach (var line in lines)
            {
                string s = line.Trim();
                if (s.Length == 0) continue;
                bool isNoise = false;
                foreach (var p in noisePrefixes)
                {
                    if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { isNoise = true; break; }
                }
                if (!isNoise) kept.Add(s);
            }
            if (kept.Count == 0) return "(仅 URC 噪音，已过滤)";
            string joined = string.Join(Environment.NewLine, kept);
            return joined.Length > 500 ? joined.Substring(0, 500) + "...(已截断)" : joined;
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
