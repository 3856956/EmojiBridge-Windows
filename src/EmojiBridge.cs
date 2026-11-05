using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace EmojiBridge
{
    public sealed class Sticker
    {
        public string Path, Hash, Format;
        public int Width, Height, Frames;
        public long Bytes;
        public bool Preserve;
    }

    public sealed class ExportOptions
    {
        public string Destination;
        public int MaxEdge = 512;
        public bool Zip = true;
        public bool Jpeg;
        public bool ToWeChat;
    }

    public sealed class ExportResult
    {
        public string Directory, ZipPath;
        public int Count, Preserved;
        public readonly List<string> Errors = new List<string>();
    }

    public static class Converter
    {
        public static readonly HashSet<string> Extensions = new HashSet<string>(
            new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico" }, StringComparer.OrdinalIgnoreCase);

        public static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static bool AnimatedPng(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                byte[] header = new byte[8];
                if (stream.Read(header, 0, 8) != 8 || header[0] != 137 || header[1] != 80) return false;
                while (stream.Position + 12 <= stream.Length)
                {
                    if (stream.Read(header, 0, 8) != 8) break;
                    long length = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
                    string type = Encoding.ASCII.GetString(header, 4, 4);
                    if (type == "acTL") return true;
                    if (type == "IDAT" || type == "IEND" || length > stream.Length - stream.Position - 4) break;
                    stream.Seek(length + 4, SeekOrigin.Current);
                }
                return false;
            }
        }

        public static Sticker Inspect(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > 64L * 1024 * 1024) throw new InvalidDataException("超过 64 MB，已跳过");
            using (var image = Image.FromFile(path, false))
            {
                if ((long)image.Width * image.Height > 50000000) throw new InvalidDataException("像素超过 5000 万，已跳过");
                bool gif = image.RawFormat.Guid == ImageFormat.Gif.Guid;
                bool png = image.RawFormat.Guid == ImageFormat.Png.Guid;
                bool apng = png && AnimatedPng(path);
                int frames = 1;
                if (gif && image.FrameDimensionsList.Contains(FrameDimension.Time.Guid))
                    frames = image.GetFrameCount(FrameDimension.Time);
                return new Sticker { Path = System.IO.Path.GetFullPath(path), Hash = HashFile(path), Width = image.Width,
                    Height = image.Height, Bytes = info.Length, Format = gif ? "GIF" : (png ? "PNG" : image.RawFormat.Guid == ImageFormat.Jpeg.Guid ? "JPG" : "图片"),
                    Frames = frames, Preserve = gif || apng };
            }
        }

        public static IEnumerable<string> Enumerate(IEnumerable<string> paths, Action<string> error, bool detectByContents = false)
        {
            var pending = new Stack<string>(paths.Reverse());
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0)
            {
                string path = pending.Pop();
                string full;
                try { full = System.IO.Path.GetFullPath(path); }
                catch (Exception ex) { error(path + "：" + ex.Message); continue; }
                if (!visited.Add(full)) continue;
                if (File.Exists(full))
                {
                    if (detectByContents || Extensions.Contains(System.IO.Path.GetExtension(full))) yield return full;
                    else error(System.IO.Path.GetFileName(full) + "：不支持的文件类型");
                    continue;
                }
                try
                {
                    if (!System.IO.Directory.Exists(full)) { error(full + "：文件不存在"); continue; }
                    if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (string file in System.IO.Directory.GetFiles(full))
                        if (detectByContents || Extensions.Contains(System.IO.Path.GetExtension(file))) pending.Push(file);
                    foreach (string dir in System.IO.Directory.GetDirectories(full)) pending.Push(dir);
                }
                catch (Exception ex) { error(full + "：" + ex.Message); }
            }
        }

        public static string SafeName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
            if (clean.Length > 48) clean = clean.Substring(0, 48);
            return string.IsNullOrEmpty(clean) ? "表情" : clean;
        }

        public static Size Fit(int width, int height, int maxEdge)
        {
            double scale = maxEdge <= 0 ? 1 : Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
            return new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        public static ExportResult Export(IList<Sticker> stickers, ExportOptions options, Action<int, string> progress)
        {
            if (stickers.Count == 0) throw new InvalidOperationException("请先勾选表情。");
            string parent = System.IO.Path.GetFullPath(options.Destination);
            System.IO.Directory.CreateDirectory(parent);
            string appName = options.ToWeChat ? "微信" : "QQ";
            string folder = System.IO.Path.Combine(parent, appName + "表情_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            System.IO.Directory.CreateDirectory(folder);
            var result = new ExportResult { Directory = folder };
            var manifest = new List<object>();
            var gallery = new StringBuilder();
            for (int i = 0; i < stickers.Count; i++)
            {
                var item = stickers[i];
                string target = null;
                try
                {
                    // Detect edits since import so preview, metadata and output describe the same file.
                    if (HashFile(item.Path) != item.Hash) throw new IOException("源文件已改变，请重新导入");
                    string extension = item.Preserve ? (item.Format == "GIF" ? ".gif" : ".png") : (options.Jpeg ? ".jpg" : ".png");
                    string fileName = (i + 1).ToString("D4") + "_" + SafeName(System.IO.Path.GetFileNameWithoutExtension(item.Path)) + extension;
                    target = System.IO.Path.Combine(folder, fileName);
                    Size size = new Size(item.Width, item.Height);
                    if (item.Preserve)
                    {
                        File.Copy(item.Path, target, false);
                        result.Preserved++;
                    }
                    else
                    {
                        using (var image = Image.FromFile(item.Path))
                        {
                            size = Fit(image.Width, image.Height, options.MaxEdge);
                            using (var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
                            {
                                bitmap.SetResolution(96, 96);
                                using (var graphics = Graphics.FromImage(bitmap))
                                {
                                    graphics.Clear(options.Jpeg ? Color.White : Color.Transparent);
                                    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                                    using (var attributes = new ImageAttributes())
                                    {
                                        attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                                        graphics.DrawImage(image, new Rectangle(Point.Empty, size), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                                    }
                                }
                                if (options.Jpeg)
                                {
                                    var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
                                    using (var parameters = new EncoderParameters(1))
                                    {
                                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                                        bitmap.Save(target, encoder, parameters);
                                    }
                                }
                                else bitmap.Save(target, ImageFormat.Png);
                            }
                        }
                    }
                    result.Count++;
                    manifest.Add(new { source = item.Path, sourceSha256 = item.Hash, file = fileName, width = size.Width,
                        height = size.Height, frames = item.Frames, originalPreserved = item.Preserve, bytes = new FileInfo(target).Length });
                    gallery.Append("<figure><img loading=\"lazy\" src=\"").Append(Uri.EscapeDataString(fileName)).Append("\" alt=\"")
                        .Append(HttpUtility.HtmlEncode(fileName)).Append("\"><figcaption>").Append(HttpUtility.HtmlEncode(fileName)).Append("</figcaption></figure>");
                }
                catch (Exception ex)
                {
                    if (target != null && File.Exists(target)) { try { File.Delete(target); } catch { } }
                    result.Errors.Add(System.IO.Path.GetFileName(item.Path) + "：" + ex.Message);
                }
                progress(i + 1, System.IO.Path.GetFileName(item.Path));
            }
            string instructions = options.ToWeChat
                ? "QQ 表情 → 微信\r\n\r\n打开微信聊天窗口的表情面板，进入「我添加的表情」，通过「+」选择导出的图片（如果当前电脑版提供该入口）。\r\n也可把图片存到手机相册，再用手机微信「我添加的表情 → +」逐张添加。\r\n"
                : "微信表情 → QQ\r\n\r\n打开电脑版 QQ 的表情面板，通过自定义 / 收藏表情中的添加入口选择导出的图片。文件选择框支持多选时可以批量选择。\r\n";
            instructions += "\r\n添加入口、数量和文件大小限制取决于" + appName + "版本。\r\n"
                + "GIF 和动画 PNG 原样保留。超出目标应用限制的动画需要另行处理。\r\n"
                + "静态图按你选择的边长缩小，默认输出透明 PNG。选择 JPG 时透明区域填白。\r\n"
                + "index.html 是离线预览，manifest.json 是导出清单（含本机源文件路径）。分享前可移除清单。\r\n"
                + "没有导出的文件见 errors.txt。\r\n";
            File.WriteAllText(System.IO.Path.Combine(folder, "使用说明.txt"), instructions, new UTF8Encoding(true));
            File.WriteAllText(System.IO.Path.Combine(folder, "manifest.json"), new JavaScriptSerializer().Serialize(manifest), Encoding.UTF8);
            File.WriteAllText(System.IO.Path.Combine(folder, "index.html"), "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>" + appName + "表情预览</title><style>body{font:16px 'Microsoft YaHei',sans-serif;background:#f4f7fb;color:#182b44;margin:40px}h1{font-size:26px}main{display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:16px}figure{margin:0;background:white;padding:18px;border-radius:16px;text-align:center}img{width:140px;height:140px;object-fit:contain}figcaption{font-size:12px;overflow-wrap:anywhere;color:#57677c;margin-top:12px}</style><h1>你的表情，准备好了。</h1><p>共 " + result.Count + " 张 · 图片在本目录中，可在" + appName + "手动添加。</p><main>" + gallery + "</main></html>", Encoding.UTF8);
            if (result.Errors.Count > 0) File.WriteAllLines(System.IO.Path.Combine(folder, "errors.txt"), result.Errors, Encoding.UTF8);
            if (options.Zip && result.Count > 0)
            {
                string zipPath = folder + ".zip";
                try { ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Optimal, false, Encoding.UTF8); result.ZipPath = zipPath; }
                catch (Exception ex) { if (File.Exists(zipPath)) { try { File.Delete(zipPath); } catch { } } result.Errors.Add("ZIP 打包失败，图片已保存在导出目录：" + ex.Message); }
            }
            return result;
        }
    }

    public sealed class MainForm : Form
    {
        readonly Color Ink = Color.FromArgb(29, 45, 68), Muted = Color.FromArgb(102, 119, 139), Blue = Color.FromArgb(37, 99, 235);
        readonly List<Sticker> stickers = new List<Sticker>();
        readonly HashSet<string> hashes = new HashSet<string>();
        readonly List<string> importErrors = new List<string>();
        readonly List<Control> lockWhileBusy = new List<Control>();
        ListView list;
        PictureBox preview;
        MemoryStream previewStream;
        Label subtitle, summary, previewInfo, status, previewEmpty;
        ComboBox sizes, formats, targetApp;
        TextBox destination;
        CheckBox zip;
        ProgressBar progress;
        Button export, open;
        bool busy, applyingSelection;
        string lastExport;

        public MainForm()
        {
            Text = "表情搬家 · 微信 ↔ QQ";
            Font = new Font("Microsoft YaHei UI", 9F);
            ForeColor = Ink;
            BackColor = Color.FromArgb(244, 247, 251);
            ClientSize = new Size(1080, 750);
            MinimumSize = new Size(960, 720);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            DragDrop += delegate(object s, DragEventArgs e) { if (!busy) Import((string[])e.Data.GetData(DataFormats.FileDrop)); };
            FormClosing += delegate(object s, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "正在处理，请等待完成后关闭。"; } };

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 18), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            Controls.Add(layout);

            var header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(new Label { Text = "表情搬家", Font = new Font(Font.FontFamily, 25, FontStyle.Bold), AutoSize = true, Location = new Point(0, 0) });
            header.Controls.Add(new Label { Text = "微信  ↔  QQ", Font = new Font(Font.FontFamily, 12, FontStyle.Bold), ForeColor = Blue, AutoSize = true, Location = new Point(177, 16) });
            subtitle = new Label { Text = "从微信或 QQ 提取收藏表情，看图挑选后导出。", AutoSize = true, ForeColor = Muted, Location = new Point(2, 53) };
            header.Controls.Add(subtitle);
            var badge = new Label { Text = "●  本地读取 · 图片按需下载", ForeColor = Color.FromArgb(25, 128, 91), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            header.Controls.Add(badge);
            header.Resize += delegate { badge.Location = new Point(header.Width - badge.Width - 4, 19); };
            layout.Controls.Add(header, 0, 0);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
            toolbar.Controls.Add(MakeButton("从微信提取", ExtractWeChat, true, 125));
            toolbar.Controls.Add(MakeButton("从 QQ 提取", ExtractQq, true, 125));
            toolbar.Controls.Add(MakeButton("＋ 添加图片", delegate { using (var dlg = new OpenFileDialog { Filter = "图片 / 动图|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff;*.ico", Multiselect = true, Title = "选择已保存的微信表情" }) { if (dlg.ShowDialog(this) == DialogResult.OK) Import(dlg.FileNames); } }, false, 125));
            toolbar.Controls.Add(MakeButton("添加文件夹", delegate { using (var dlg = new FolderBrowserDialog { Description = "选择表情所在文件夹（包括子文件夹）" }) { if (dlg.ShowDialog(this) == DialogResult.OK) Import(new[] { dlg.SelectedPath }); } }, false, 110));
            toolbar.Controls.Add(MakeButton("看图挑选…", delegate { PickStickers(); }, false, 105));
            toolbar.Controls.Add(MakeButton("移除勾选", RemoveSelected, false, 105));
            toolbar.Controls.Add(MakeButton("使用帮助", ShowHelp, false, 95));
            layout.Controls.Add(toolbar, 0, 1);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 14) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 268));
            var filePanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14), Margin = new Padding(0, 0, 14, 0) };
            summary = new Label { Dock = DockStyle.Top, Height = 36, Text = "01  导入表情   ·   也可以把图片或文件夹拖入窗口", ForeColor = Muted };
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None };
            list.Columns.Add("文件名", 260); list.Columns.Add("格式", 58); list.Columns.Add("尺寸", 90); list.Columns.Add("大小", 75);
            list.SelectedIndexChanged += PreviewSelected;
            list.ItemChecked += delegate { if (!applyingSelection) UpdateSummary(); };
            list.AllowDrop = true;
            list.DragEnter += delegate(object s, DragEventArgs e) { e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            list.DragDrop += delegate(object s, DragEventArgs e) { if (!busy) Import((string[])e.Data.GetData(DataFormats.FileDrop)); };
            filePanel.Controls.Add(list); filePanel.Controls.Add(summary);
            body.Controls.Add(filePanel, 0, 0);
            var previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16), Margin = Padding.Empty };
            previewInfo = new Label { Dock = DockStyle.Bottom, Height = 75, ForeColor = Muted, Text = "选择左侧图片查看预览\nGIF 会保留原始动画", AutoEllipsis = true };
            preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(247, 249, 252) };
            previewEmpty = new Label { Text = "你的表情，会出现在这里\n\nPNG / JPG / GIF / BMP / TIFF / ICO", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted, BackColor = preview.BackColor };
            preview.Controls.Add(previewEmpty);
            previewPanel.Controls.Add(preview); previewPanel.Controls.Add(previewInfo);
            previewPanel.Controls.Add(new Label { Dock = DockStyle.Top, Height = 32, Text = "表情预览", Font = new Font(Font, FontStyle.Bold) });
            body.Controls.Add(previewPanel, 1, 0);
            layout.Controls.Add(body, 0, 2);

            var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3, BackColor = Color.White, Padding = new Padding(14, 8, 14, 8), Margin = Padding.Empty };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 33)); settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 33)); settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            settings.Controls.Add(new Label { Text = "02  导出设置", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            var formatFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            targetApp = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, AccessibleName = "导出目标" };
            targetApp.Items.AddRange(new object[] { "导出给 QQ", "导出给微信" });
            targetApp.SelectedIndex = 0;
            formats = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 }; formats.Items.AddRange(new object[] { "PNG · 保留透明", "JPG · 白色背景" }); formats.SelectedIndex = 0;
            sizes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 }; sizes.Items.AddRange(new object[] { "原始尺寸", "最长边 512", "最长边 256", "最长边 128" }); sizes.SelectedIndex = 1;
            formatFlow.Controls.Add(targetApp); formatFlow.Controls.Add(formats); formatFlow.Controls.Add(sizes); settings.Controls.Add(formatFlow, 1, 0); settings.SetColumnSpan(formatFlow, 2);
            zip = new CheckBox { Text = "同时打包 ZIP", Checked = true, Dock = DockStyle.Fill }; settings.Controls.Add(zip, 3, 0);
            settings.Controls.Add(new Label { Text = "保存到", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            destination = new TextBox { Dock = DockStyle.Fill, Text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "表情搬家") };
            settings.Controls.Add(destination, 1, 1); settings.SetColumnSpan(destination, 2);
            var browse = MakeButton("选择目录…", delegate { using (var dlg = new FolderBrowserDialog { Description = "选择导出目录", SelectedPath = destination.Text }) { if (dlg.ShowDialog(this) == DialogResult.OK) destination.Text = dlg.SelectedPath; } }, false, 110);
            browse.Height = 27; settings.Controls.Add(browse, 3, 1);
            var note = new Label { Text = "静态图可缩小，GIF / 动画 PNG 原样保留。导出后在目标应用的表情面板添加。", ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            settings.Controls.Add(note, 0, 2); settings.SetColumnSpan(note, 4);
            layout.Controls.Add(settings, 0, 3);
            lockWhileBusy.Add(targetApp); lockWhileBusy.Add(formats); lockWhileBusy.Add(sizes); lockWhileBusy.Add(zip); lockWhileBusy.Add(destination); lockWhileBusy.Add(list);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 12, 0, 0) };
            export = MakeButton("导出勾选表情 →", Export, true, 180);
            open = MakeButton("打开导出目录", delegate { if (lastExport != null) System.Diagnostics.Process.Start("explorer.exe", "\"" + lastExport + "\""); }, false, 140); open.Enabled = false;
            footer.Controls.Add(export); footer.Controls.Add(open);
            footer.Controls.Add(MakeButton("查看处理记录", delegate { MessageBox.Show(this, importErrors.Count == 0 ? "暂无错误记录。" : string.Join("\r\n", importErrors.Take(60).ToArray()) + (importErrors.Count > 60 ? "\r\n（仅显示前 60 条）" : ""), "处理记录", MessageBoxButtons.OK, MessageBoxIcon.Information); }, false, 130));
            layout.Controls.Add(footer, 0, 4);
            var statusPanel = new Panel { Dock = DockStyle.Fill };
            status = new Label { Text = "就绪 · 选择「从微信提取」或「从 QQ 提取」开始", Dock = DockStyle.Fill, ForeColor = Muted, AutoEllipsis = true };
            progress = new ProgressBar { Dock = DockStyle.Right, Width = 160, Height = 14, Visible = false };
            statusPanel.Controls.Add(status); statusPanel.Controls.Add(progress); layout.Controls.Add(statusPanel, 0, 5);
        }

        Button MakeButton(string text, EventHandler click, bool primary, int width)
        {
            var button = new Button { Text = text, Width = width, Height = 36, FlatStyle = FlatStyle.Flat, BackColor = primary ? Blue : Color.White,
                ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 8, 0), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = primary ? Blue : Color.FromArgb(219, 226, 236);
            button.Click += click;
            lockWhileBusy.Add(button);
            return button;
        }

        void SetBusy(bool value)
        {
            busy = value;
            foreach (var control in lockWhileBusy) control.Enabled = !value;
            open.Enabled = !value && lastExport != null;
            progress.Visible = value;
            UseWaitCursor = value;
        }

        void UpdateSummary()
        {
            summary.Text = stickers.Count == 0 ? "01  导入表情   ·   拖入图片或文件夹" : "01  表情列表   ·   " + stickers.Count + " 张，已勾选 " + list.CheckedItems.Count + " 张";
        }

        void RemoveSelected(object sender, EventArgs args)
        {
            foreach (ListViewItem row in list.CheckedItems.Cast<ListViewItem>().ToArray())
            {
                var sticker = (Sticker)row.Tag;
                hashes.Remove(sticker.Hash); stickers.Remove(sticker); list.Items.Remove(row);
            }
            UpdateSummary(); PreviewSelected(null, EventArgs.Empty);
        }

        void PreviewSelected(object sender, EventArgs args)
        {
            ClearPreview();
            previewEmpty.Visible = true;
            if (list.SelectedItems.Count == 0) { previewInfo.Text = "选择左侧图片查看预览\nGIF 会保留原始动画"; return; }
            var sticker = (Sticker)list.SelectedItems[0].Tag;
            try
            {
                // A memory-backed copy avoids holding a lock on the user's original image.
                byte[] bytes = File.ReadAllBytes(sticker.Path);
                var stream = new MemoryStream(bytes);
                Image image;
                try { image = Image.FromStream(stream); }
                catch { stream.Dispose(); throw; }
                previewStream = stream;
                preview.Image = image;
                previewEmpty.Visible = false;
                previewInfo.Text = sticker.Format + "  ·  " + sticker.Width + " × " + sticker.Height + "  ·  " + FormatBytes(sticker.Bytes) + "\n" + (sticker.Preserve ? "原文件保留，不缩放 / 不压缩" : "按导出设置转换，不修改原图") + (sticker.Frames > 1 ? "\n动画 · " + sticker.Frames + " 帧" : "");
            }
            catch (Exception ex) { previewInfo.Text = "无法预览：" + ex.Message; }
        }

        static string FormatBytes(long bytes) { return bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#") + " KB" : (bytes / 1048576.0).ToString("0.#") + " MB"; }

        void ClearPreview()
        {
            if (preview.Image != null) { var image = preview.Image; preview.Image = null; image.Dispose(); }
            if (previewStream != null) { previewStream.Dispose(); previewStream = null; }
        }

        void Import(string[] paths, bool openPicker = false, bool detectByContents = false)
        {
            if (busy || paths == null || paths.Length == 0) return;
            SetBusy(true); progress.Style = ProgressBarStyle.Marquee; status.Text = "正在识别图片并去重…";
            var existing = new HashSet<string>(hashes);
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                var added = new List<Sticker>(); var errors = new List<string>(); int duplicates = 0;
                foreach (string path in Converter.Enumerate(paths, errors.Add, detectByContents))
                {
                    try { var item = Converter.Inspect(path); if (existing.Add(item.Hash)) added.Add(item); else duplicates++; }
                    catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(path) + "：无法读取图片（" + ex.Message + "）"); }
                }
                e.Result = new object[] { added, errors, duplicates };
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                SetBusy(false); progress.Style = ProgressBarStyle.Continuous;
                if (e.Error != null) { status.Text = "导入失败：" + e.Error.Message; return; }
                var data = (object[])e.Result; var added = (List<Sticker>)data[0]; var errors = (List<string>)data[1];
                list.BeginUpdate();
                foreach (var sticker in added)
                {
                    stickers.Add(sticker); hashes.Add(sticker.Hash);
                    var row = new ListViewItem(new[] { System.IO.Path.GetFileName(sticker.Path), sticker.Format + (sticker.Frames > 1 ? " 动" : ""), sticker.Width + "×" + sticker.Height, FormatBytes(sticker.Bytes) }) { Tag = sticker, Checked = true, ToolTipText = sticker.Path };
                    list.Items.Add(row);
                }
                list.EndUpdate(); importErrors.AddRange(errors); UpdateSummary();
                status.Text = "已添加 " + added.Count + " 张 · 去重 " + data[2] + " 张 · 跳过 " + errors.Count + " 个（详情见处理记录）";
                if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true;
                worker.Dispose();
                if (openPicker && added.Count > 0) PickStickers();
            };
            worker.RunWorkerAsync();
        }

        void ExtractWeChat(object sender, EventArgs args)
        {
            using (var dialog = new WeChatForm(destination.Text))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.ExtractedDirectory != null)
                {
                    lastExport = dialog.ExtractedDirectory;
                    open.Enabled = true;
                    targetApp.SelectedIndex = 0;
                    Import(new[] { dialog.ExtractedDirectory }, true);
                }
            }
        }

        void ExtractQq(object sender, EventArgs args)
        {
            using (var dialog = new QqSourcePicker())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Source == null) return;
                targetApp.SelectedIndex = 1;
                Import(new[] { dialog.Source.Directory }, true, true);
            }
        }

        void PickStickers()
        {
            if (list.Items.Count == 0)
            {
                MessageBox.Show(this, "请先提取或添加表情。", "看图挑选");
                return;
            }
            var checkedStickers = list.CheckedItems.Cast<ListViewItem>().Select(row => (Sticker)row.Tag);
            using (var picker = new StickerPicker(stickers, checkedStickers))
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                applyingSelection = true;
                try
                {
                    foreach (ListViewItem row in list.Items)
                        row.Checked = picker.Selection.Contains((Sticker)row.Tag);
                }
                finally { applyingSelection = false; }
            }
            UpdateSummary();
        }

        void Export(object sender, EventArgs args)
        {
            var selected = list.CheckedItems.Cast<ListViewItem>().Select(item => (Sticker)item.Tag).ToList();
            if (selected.Count == 0) { MessageBox.Show(this, "请添加并勾选要导出的表情。", "还没有选择表情"); return; }
            string path = destination.Text.Trim();
            if (path.Length == 0) { MessageBox.Show(this, "请先选择导出目录。", "选择目录"); return; }
            var options = new ExportOptions { Destination = path, MaxEdge = new[] { 0, 512, 256, 128 }[sizes.SelectedIndex], Zip = zip.Checked, Jpeg = formats.SelectedIndex == 1, ToWeChat = targetApp.SelectedIndex == 1 };
            SetBusy(true); progress.Style = ProgressBarStyle.Continuous; progress.Maximum = selected.Count; progress.Value = 0; status.Text = "正在导出…";
            var worker = new BackgroundWorker { WorkerReportsProgress = true };
            worker.DoWork += delegate(object s, DoWorkEventArgs e) { e.Result = Converter.Export(selected, options, delegate(int count, string file) { worker.ReportProgress(count, file); }); };
            worker.ProgressChanged += delegate(object s, ProgressChangedEventArgs e) { progress.Value = Math.Min(e.ProgressPercentage, progress.Maximum); status.Text = "正在整理 " + e.ProgressPercentage + " / " + selected.Count + " · " + e.UserState; };
            worker.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                SetBusy(false);
                if (e.Error != null) { status.Text = "导出未完成：" + e.Error.Message; MessageBox.Show(this, e.Error.Message, "导出未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                var result = (ExportResult)e.Result; lastExport = result.Directory; open.Enabled = true; importErrors.AddRange(result.Errors);
                status.Text = "已导出 " + result.Count + " 张 · 原样保留 " + result.Preserved + " 张 · 失败 / 提示 " + result.Errors.Count + " 条";
                string target = options.ToWeChat ? "微信「我添加的表情」" : "QQ 收藏表情";
                MessageBox.Show(this, "成功导出 " + result.Count + " 张表情。\r\n\r\n" + result.Directory + (result.ZipPath != null ? "\r\n已同时生成 ZIP 压缩包。" : "") + "\r\n\r\n打开导出目录，在" + target + "中手动添加图片。" + (result.Errors.Count > 0 ? "\r\n有文件未完成，请查看处理记录。" : ""), "导出完成", MessageBoxButtons.OK, result.Count > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                worker.Dispose();
            };
            worker.RunWorkerAsync();
        }

        void ShowHelp(object sender, EventArgs args)
        {
            MessageBox.Show(this, "微信 → QQ\r\n登录电脑版微信 4.x 并打开「我添加的表情」。点击「从微信提取」，选择账号，完成后导入主窗口。\r\n\r\nQQ → 微信\r\n点击「从 QQ 提取」，选择 QQ 账号或收藏表情原图目录。缺少表情时，在 QQ 收藏表情面板滚动加载后重新提取。\r\n\r\n看图勾选后导出，再到目标应用的收藏表情面板添加。也可以直接导入 PNG、JPG、GIF、BMP、TIFF 或 ICO。GIF / 动画 PNG 保留原文件。", "表情搬家使用帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && preview != null) ClearPreview();
            base.Dispose(disposing);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 5 && args[0] == "--extract-wechat")
            {
                string report = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-extraction.json");
                try
                {
                    var result = WeChatExtractor.Extract(args[1], args[2], args[3], args[4], delegate { return false; }, delegate { });
                    File.WriteAllText(report, new JavaScriptSerializer().Serialize(new { directory = result.Directory, total = result.Total, success = result.Ok, failed = result.Failed, cleanupFailed = result.CleanupFailed }), Encoding.UTF8);
                    Environment.ExitCode = result.Ok > 0 ? 0 : 2;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(report, new JavaScriptSerializer().Serialize(new { error = WeChatExtractor.Redact(ex.Message) }), Encoding.UTF8);
                    Environment.ExitCode = 1;
                }
                return;
            }
            if (args.Length > 0 && args[0] == "--self-test")
            {
                try { SelfTest.Run(args.Length > 1 ? args[1] : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-output")); Environment.ExitCode = 0; }
                catch (Exception ex) { File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
