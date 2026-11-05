using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace EmojiBridge
{
    public sealed class QqStickerSource
    {
        static readonly string[] RelativeDirectories = {
            @"nt_qq\nt_data\Emoji\personal_emoji\Ori",
            @"nt_qq\nt_data\Emoji\personal-emoji\Ori",
            @"Image\Image\CustomFace",
            "CustomFace"
        };

        public readonly string Account, Directory;

        QqStickerSource(string account, string directory)
        {
            Account = account;
            Directory = directory;
        }

        public override string ToString()
        {
            return (string.IsNullOrEmpty(Account) ? "手动选择" : "QQ " + Account) + " · " + Path.GetFileName(Directory);
        }

        public static List<QqStickerSource> Discover()
        {
            var roots = new[] {
                ReadConfiguredDirectory(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tencent Files"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Tencent Files")
            };
            return roots.Where(root => !string.IsNullOrEmpty(root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(Find)
                .GroupBy(source => source.Directory, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
        }

        static string ReadConfiguredDirectory()
        {
            try
            {
                string publicProfile = Environment.GetEnvironmentVariable("PUBLIC");
                if (string.IsNullOrEmpty(publicProfile)) return null;
                var config = new FileInfo(Path.Combine(publicProfile, @"Documents\Tencent\QQ\UserDataInfo.ini"));
                if (!config.Exists || config.Length > 65536) return null;
                string[] lines;
                try { lines = File.ReadAllLines(config.FullName, new UTF8Encoding(false, true)); }
                catch (DecoderFallbackException) { lines = File.ReadAllLines(config.FullName, Encoding.GetEncoding(936)); }
                foreach (string line in lines)
                {
                    int equals = line.IndexOf('=');
                    if (equals < 0 || !line.Substring(0, equals).Trim().Equals("UserDataSavePath", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string path = Environment.ExpandEnvironmentVariables(line.Substring(equals + 1).Trim().Trim('"'));
                    if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
                        (path[2] == '\\' || path[2] == '/')) return Path.GetFullPath(path);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return null;
        }

        public static List<QqStickerSource> Find(string directory)
        {
            var sources = new List<QqStickerSource>();
            if (!System.IO.Directory.Exists(directory)) return sources;
            var root = new DirectoryInfo(directory);
            if (root.Name.Equals("Ori", StringComparison.OrdinalIgnoreCase) ||
                root.Name.Equals("CustomFace", StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(new QqStickerSource("", root.FullName));
                return sources;
            }
            if (root.Name.Equals("personal_emoji", StringComparison.OrdinalIgnoreCase) ||
                root.Name.Equals("personal-emoji", StringComparison.OrdinalIgnoreCase))
            {
                string originals = Path.Combine(root.FullName, "Ori");
                if (System.IO.Directory.Exists(originals)) sources.Add(new QqStickerSource("", originals));
                return sources;
            }
            AddAccount(sources, root);
            if (sources.Count > 0) return sources;
            try
            {
                foreach (var account in root.GetDirectories().Where(folder => folder.Name.Length > 0 && folder.Name.All(char.IsDigit))
                    .OrderBy(folder => folder.Name, StringComparer.Ordinal))
                    AddAccount(sources, account);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return sources;
        }

        static void AddAccount(List<QqStickerSource> sources, DirectoryInfo account)
        {
            foreach (string relative in RelativeDirectories)
            {
                string folder = Path.Combine(account.FullName, relative);
                if (System.IO.Directory.Exists(folder)) sources.Add(new QqStickerSource(account.Name, folder));
            }
        }
    }

    sealed class QqSourcePicker : Form
    {
        readonly ListBox sources;
        readonly Label path;
        readonly Button apply;

        public QqStickerSource Source { get { return sources.SelectedItem as QqStickerSource; } }

        public QqSourcePicker()
        {
            Text = "从 QQ 提取收藏表情";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(640, 390);
            MinimumSize = new Size(520, 350);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(244, 247, 251);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill,
                Text = "选择要读取的 QQ 账号或表情目录。程序读取电脑上已加载的收藏原图。"
                    + "\r\n如果表情不全，请先在 QQ 的收藏表情面板往下滚动，让图片加载。"
            }, 0, 0);
            sources = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "QQ 表情来源" };
            layout.Controls.Add(sources, 0, 1);
            path = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(path, 0, 2);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            apply = new Button { Text = "导入并挑选", Width = 115, Height = 32, DialogResult = DialogResult.OK };
            var browse = new Button { Text = "选择目录…", Width = 105, Height = 32 };
            browse.Click += Browse;
            var cancel = new Button { Text = "取消", Width = 80, Height = 32, DialogResult = DialogResult.Cancel };
            actions.Controls.Add(apply);
            actions.Controls.Add(browse);
            actions.Controls.Add(cancel);
            layout.Controls.Add(actions, 0, 3);
            AcceptButton = apply;
            CancelButton = cancel;
            sources.SelectedIndexChanged += delegate { UpdateSource(); };
            SetSources(QqStickerSource.Discover());
        }

        void Browse(object sender, EventArgs args)
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择 Tencent Files、QQ 账号目录、personal_emoji、Ori 或 CustomFace" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var found = QqStickerSource.Find(dialog.SelectedPath);
                if (found.Count > 0) SetSources(found);
                else MessageBox.Show(this, "此目录没有找到 QQ 收藏表情原图。请选择 QQ 账号目录或其中的 Ori 文件夹。", Text);
            }
        }

        void SetSources(List<QqStickerSource> found)
        {
            sources.Items.Clear();
            sources.Items.AddRange(found.Cast<object>().ToArray());
            if (found.Count > 0) sources.SelectedIndex = 0;
            UpdateSource();
        }

        void UpdateSource()
        {
            var selected = Source;
            apply.Enabled = selected != null;
            path.Text = selected == null ? "没有检测到收藏原图目录。可用「选择目录」手动定位。" : selected.Directory;
        }
    }
}
