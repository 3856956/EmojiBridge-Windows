using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace EmojiBridge
{
    public sealed class WeChatResult
    {
        public string Directory;
        public int Total, Ok, Failed;
        public bool CleanupFailed;
    }

    public static class WeChatExtractor
    {
        public const string EngineSha256 = "be1eb21cd8809fcfe386f2333c4bc420ba7b0e694142586ee159fcb238f0259f";
        public static string EnginePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "third_party", "wxemoticon", "wxemoticon.exe"); } }

        public static bool IsWeChatRunning()
        {
            foreach (string name in new[] { "Weixin", "WeChat" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) { try { if (!process.HasExited) return true; } catch { } }
            return false;
        }

        public static string DetectExecutable()
        {
            foreach (string name in new[] { "Weixin", "WeChat" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) { try { return process.MainModule.FileName; } catch { } }
            foreach (string keyName in new[] { @"Software\Tencent\Weixin", @"Software\Tencent\WeChat" })
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(keyName))
                    {
                        if (key == null) continue;
                        string location = key.GetValue("InstallPath") as string;
                        if (string.IsNullOrEmpty(location)) continue;
                        foreach (string file in new[] { "Weixin.exe", "WeChat.exe" })
                            if (File.Exists(Path.Combine(location, file))) return Path.Combine(location, file);
                    }
                }
                catch { }
            }
            foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"D:\weChat", @"D:\Program Files" })
                foreach (string relative in new[] { @"Tencent\Weixin\Weixin.exe", @"Tencent\WeChat\WeChat.exe", @"Weixin\Weixin.exe", "Weixin.exe" })
                {
                    string path = Path.Combine(folder, relative);
                    if (File.Exists(path)) return path;
                }
            return "";
        }

        public static List<string> Accounts(string root)
        {
            var result = new List<string>();
            try
            {
                if (File.Exists(Path.Combine(root, @"db_storage\emoticon\emoticon.db")))
                    result.Add(Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)));
                else if (System.IO.Directory.Exists(root))
                    foreach (string folder in System.IO.Directory.GetDirectories(root))
                        if (File.Exists(Path.Combine(folder, @"db_storage\emoticon\emoticon.db"))) result.Add(Path.GetFileName(folder));
            }
            catch { }
            return result.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> DetectRoots(string executable)
        {
            var candidates = new List<string> {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "xwechat_files"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "xwechat_files")
            };
            if (!string.IsNullOrWhiteSpace(executable))
            {
                try
                {
                    var folder = new DirectoryInfo(Path.GetDirectoryName(executable));
                    for (int i = 0; i < 4 && folder != null; i++, folder = folder.Parent)
                        candidates.Add(Path.Combine(folder.FullName, "xwechat_files"));
                }
                catch { }
            }
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(s => Accounts(s).Count > 0).ToList();
        }

        // Windows CommandLineToArgvW rules, including trailing backslashes.
        public static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
                else { result.Append('\\', slashes); result.Append(c); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }

        public static string Redact(string text)
        {
            text = Regex.Replace(text ?? "", @"https?://[^\s\""<>]+", "[图片链接]", RegexOptions.IgnoreCase);
            return Regex.Replace(text, @"\b[0-9a-fA-F]{64,}\b", "[已隐藏的解密信息]");
        }

        public static WeChatResult ParseResult(string json, string directory)
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json.Trim());
            if (data == null || !data.ContainsKey("total") || !data.ContainsKey("ok") || !data.ContainsKey("failed"))
                throw new InvalidDataException("提取组件返回了无法识别的结果。");
            return new WeChatResult { Directory = directory, Total = Convert.ToInt32(data["total"]), Ok = Convert.ToInt32(data["ok"]) + (data.ContainsKey("skipped") ? Convert.ToInt32(data["skipped"]) : 0), Failed = Convert.ToInt32(data["failed"]) };
        }

        public static WeChatResult Extract(string executable, string dataRoot, string account, string outputParent, Func<bool> cancelled, Action<string> report)
        {
            if (!File.Exists(EnginePath) || Converter.HashFile(EnginePath) != EngineSha256)
                throw new InvalidOperationException("缺少提取组件，或组件校验失败。请使用完整便携包解压后的程序。");
            if (!IsWeChatRunning()) throw new InvalidOperationException("请先打开并登录电脑版微信，再打开一次「我添加的表情」面板。");
            if (!File.Exists(executable)) throw new FileNotFoundException("请选择有效的 Weixin.exe / WeChat.exe。");
            dataRoot = Path.GetFullPath(dataRoot);
            // Accept either the xwechat_files directory or a user-selected account directory.
            if (File.Exists(Path.Combine(dataRoot, @"db_storage\emoticon\emoticon.db"))) dataRoot = Path.GetDirectoryName(dataRoot.TrimEnd(Path.DirectorySeparatorChar));
            if (!Accounts(dataRoot).Contains(account)) throw new InvalidOperationException("所选账号没有找到表情数据库，请重新检测。");
            string parent = Path.GetFullPath(outputParent);
            string output = Path.Combine(parent, "微信提取_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            System.IO.Directory.CreateDirectory(output);
            string session = Path.Combine(Path.GetTempPath(), "EmojiBridge-extract-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(session);
            WeChatResult result = null;
            try
            {
                var args = new[] { "--wechat-bin", executable, "--wechat-data-dir", dataRoot, "--no-interactive", "export", "--wxid", account, "--out-dir", output, "--flat", "--json", "--timeout", "120", "--http-timeout", "30", "--concurrency", "6" };
                var start = new ProcessStartInfo(EnginePath, string.Join(" ", args.Select(Quote).ToArray())) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                // Scope component key/URL caches to this extraction. Never change the user's environment.
                start.EnvironmentVariables["LOCALAPPDATA"] = session;
                start.EnvironmentVariables["TEMP"] = session;
                start.EnvironmentVariables["TMP"] = session;
                var stdout = new StringBuilder(); var stderr = new StringBuilder();
                using (var process = new Process { StartInfo = start })
                {
                    process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                    process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) {
                        if (e.Data == null) return;
                        string safe = Redact(e.Data);
                        lock (stderr) { if (stderr.Length < 16000) stderr.AppendLine(safe); }
                        report(safe);
                    };
                    process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    var clock = Stopwatch.StartNew();
                    while (!process.WaitForExit(200))
                    {
                        if (cancelled() || clock.Elapsed > TimeSpan.FromMinutes(15))
                        {
                            try { process.Kill(); } catch { }
                            process.WaitForExit();
                            throw new OperationCanceledException(cancelled() ? "已停止提取；已完成的图片仍保留在：" + output : "提取超过 15 分钟，已停止。已完成图片保留在：" + output);
                        }
                    }
                    process.WaitForExit(); // Drain redirected callbacks before reading the final JSON.
                    if (process.ExitCode != 0)
                    {
                        string details = stderr.ToString();
                        File.WriteAllText(Path.Combine(output, "提取错误.txt"), details, Encoding.UTF8);
                        throw new InvalidOperationException("提取未完成。确认所选账号已登录，并打开过表情面板；如果微信以管理员身份运行，程序需使用相同权限。\r\n\r\n" + details.Substring(Math.Max(0, details.Length - 1800)) + "\r\n\r\n详情保存在：" + Path.Combine(output, "提取错误.txt"));
                    }
                    result = ParseResult(stdout.ToString(), output);
                }
                File.WriteAllText(Path.Combine(output, "提取结果.json"), new JavaScriptSerializer().Serialize(new { total = result.Total, success = result.Ok, failed = result.Failed, engine = "wxemoticon v0.3.0" }), Encoding.UTF8);
                if (result.Failed > 0) File.WriteAllText(Path.Combine(output, "提取提示.txt"), "部分表情下载失败，可能是图片链接过期或网络不可达。可打开微信表情面板让图片加载后再次尝试。\r\n" + stderr, Encoding.UTF8);
                return result;
            }
            finally
            {
                // The upstream component includes the source links for troubleshooting. The images are sufficient here.
                try { File.Delete(Path.Combine(output, "导出信息", "emoticon_urls.txt")); }
                catch { report("图片链接清单未能清理，请勿分享导出信息目录。"); }
                // This is only the unique temporary folder created above, never a caller-supplied path.
                try { System.IO.Directory.Delete(session, true); }
                catch { if (result != null) result.CleanupFailed = true; report("临时缓存清理失败，位置：" + session); }
            }
        }
    }

    public sealed class WeChatForm : Form
    {
        TextBox executable, destination, log;
        ComboBox roots, accounts;
        Button start, finish, cancel, open;
        readonly List<Control> editing = new List<Control>();
        bool busy;
        volatile bool cancelRequested;
        public string ExtractedDirectory;

        public WeChatForm(string outputParent)
        {
            Text = "从微信提取 · 我添加的表情";
            Font = new Font("Microsoft YaHei UI", 9F); BackColor = Color.FromArgb(244, 247, 251);
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(740, 560); MinimumSize = new Size(740, 560);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 3, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(layout);
            var intro = new Label { Text = "提取你在微信里添加 / 收藏的表情\r\n先登录电脑版微信 4.x，打开一次「我添加的表情」面板，保持微信运行。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(29, 45, 68), Font = new Font(Font.FontFamily, 11), AutoSize = false };
            layout.Controls.Add(intro, 0, 0); layout.SetColumnSpan(intro, 3);
            executable = new TextBox { Text = WeChatExtractor.DetectExecutable(), Dock = DockStyle.Fill };
            roots = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            accounts = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            destination = new TextBox { Dock = DockStyle.Fill, Text = outputParent };
            AddRow(layout, "微信程序", executable, 1, "浏览…", delegate { using (var d = new OpenFileDialog { Filter = "微信程序|Weixin.exe;WeChat.exe" }) if (d.ShowDialog(this) == DialogResult.OK) { executable.Text = d.FileName; Detect(); } });
            AddRow(layout, "数据目录", roots, 2, "浏览…", delegate { using (var d = new FolderBrowserDialog { Description = "选择 xwechat_files 目录，或其中的账号目录" }) if (d.ShowDialog(this) == DialogResult.OK) { roots.Text = d.SelectedPath; RefreshAccounts(); } });
            AddRow(layout, "微信账号", accounts, 3, "重新检测", delegate { Detect(); });
            AddRow(layout, "提取到", destination, 4, "浏览…", delegate { using (var d = new FolderBrowserDialog { Description = "选择提取图片的保存目录" }) if (d.ShowDialog(this) == DialogResult.OK) destination.Text = d.SelectedPath; });
            var note = new Label { Text = "读取表情库所需的解密信息只在本机临时使用。图片从微信表情库记录的地址下载。\r\n提取成功后，可导入主窗口继续整理；不会直接修改微信或 QQ 的收藏。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(102, 119, 139) };
            layout.Controls.Add(note, 0, 5); layout.SetColumnSpan(note, 3);
            log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Text = "准备就绪。选择账号后点击「开始提取」。" };
            layout.Controls.Add(log, 0, 6); layout.SetColumnSpan(log, 3);
            var hint = new Label { Text = "部分旧表情可能因链接过期无法恢复；微信更新后可能需要适配。", ForeColor = Color.FromArgb(102, 119, 139), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(hint, 0, 7); layout.SetColumnSpan(hint, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            start = Button("开始提取", Start); start.BackColor = Color.FromArgb(37, 99, 235); start.ForeColor = Color.White;
            finish = Button("导入主窗口", delegate { DialogResult = DialogResult.OK; Close(); }); finish.Enabled = false;
            open = Button("打开图片目录", delegate { if (ExtractedDirectory != null) Process.Start("explorer.exe", WeChatExtractor.Quote(ExtractedDirectory)); }); open.Enabled = false;
            cancel = Button("关闭", delegate { if (busy) { cancelRequested = true; cancel.Enabled = false; Append("正在停止提取，请稍候…"); } else Close(); });
            buttons.Controls.Add(start); buttons.Controls.Add(finish); buttons.Controls.Add(open); buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, 8); layout.SetColumnSpan(buttons, 3);
            editing.AddRange(new Control[] { executable, roots, accounts, destination });
            roots.SelectedIndexChanged += delegate { RefreshAccounts(); };
            roots.Leave += delegate { RefreshAccounts(); };
            FormClosing += delegate(object s, FormClosingEventArgs e) { if (busy) { e.Cancel = true; cancelRequested = true; Append("正在停止提取，请稍候…"); } };
            Detect();
        }

        Button Button(string text, EventHandler click)
        {
            var button = new Button { Text = text, Width = 112, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            button.FlatAppearance.BorderColor = Color.FromArgb(219, 226, 236); button.Click += click; return button;
        }

        void AddRow(TableLayoutPanel layout, string text, Control control, int row, string buttonText, EventHandler click)
        {
            layout.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            layout.Controls.Add(control, 1, row); var button = Button(buttonText, click); button.Dock = DockStyle.Fill; layout.Controls.Add(button, 2, row); editing.Add(button);
        }

        void Detect()
        {
            string previous = roots.Text;
            var found = WeChatExtractor.DetectRoots(executable.Text);
            roots.Items.Clear(); roots.Items.AddRange(found.Cast<object>().ToArray());
            roots.Text = found.Contains(previous) || found.Count == 0 ? previous : found[0]; RefreshAccounts();
        }

        void RefreshAccounts()
        {
            string previous = accounts.Text;
            var found = WeChatExtractor.Accounts(roots.Text);
            accounts.Items.Clear(); accounts.Items.AddRange(found.Cast<object>().ToArray());
            if (found.Contains(previous)) accounts.SelectedItem = previous; else if (found.Count > 0) accounts.SelectedIndex = 0;
        }

        void Append(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Append), text); } catch (InvalidOperationException) { } return; }
            if (log.TextLength > 20000) log.Text = log.Text.Substring(log.TextLength - 10000);
            log.AppendText("\r\n" + text);
        }

        void Start(object sender, EventArgs e)
        {
            RefreshAccounts();
            if (accounts.SelectedIndex < 0) { Append("未找到账号：请选择包含表情数据库的 xwechat_files 数据目录。"); return; }
            if (!WeChatExtractor.IsWeChatRunning()) { Append("请先打开并登录电脑版微信，打开一次「我添加的表情」面板后重试。"); return; }
            if (string.IsNullOrWhiteSpace(destination.Text)) { Append("请选择保存目录。"); return; }
            string exe = executable.Text, data = roots.Text, account = accounts.Text, output = destination.Text;
            busy = true; cancelRequested = false; start.Enabled = false; finish.Enabled = false; open.Enabled = false; cancel.Text = "停止提取";
            foreach (var control in editing) control.Enabled = false;
            Append("正在读取表情列表并下载图片，首次提取可能需要一段时间…");
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object s, DoWorkEventArgs args) { args.Result = WeChatExtractor.Extract(exe, data, account, output, delegate { return cancelRequested; }, Append); };
            worker.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs args) {
                busy = false; start.Enabled = true; cancel.Enabled = true; cancel.Text = "关闭";
                foreach (var control in editing) control.Enabled = true;
                if (args.Error != null) Append(WeChatExtractor.Redact(args.Error.Message));
                else
                {
                    var result = (WeChatResult)args.Result; ExtractedDirectory = result.Directory;
                    Append("提取完成：共 " + result.Total + " 张，成功 " + result.Ok + " 张，失败 " + result.Failed + " 张。\r\n保存位置：" + result.Directory);
                    if (result.CleanupFailed) Append("临时缓存清理失败，请按上方位置手动清理。");
                    open.Enabled = true; finish.Enabled = result.Ok > 0;
                }
                worker.Dispose();
            };
            worker.RunWorkerAsync();
        }
    }
}
