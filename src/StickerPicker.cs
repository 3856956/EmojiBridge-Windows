using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EmojiBridge
{
    sealed class StickerPicker : Form
    {
        readonly Sticker[] stickers;
        readonly ListView gallery;
        readonly ImageList thumbnails;
        readonly Label summary;
        bool changingChecks;

        public HashSet<Sticker> Selection { get; private set; }

        public StickerPicker(IEnumerable<Sticker> all, IEnumerable<Sticker> selected)
        {
            stickers = all.ToArray();
            Selection = new HashSet<Sticker>(selected);
            Text = "看图挑选表情";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(800, 570);
            MinimumSize = new Size(520, 410);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(244, 247, 251);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(layout);
            summary = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(summary, 0, 0);

            int edge = Math.Min(192, (int)Math.Round(88 * DeviceDpi / 96.0));
            thumbnails = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(edge, edge) };
            gallery = new ListView { Dock = DockStyle.Fill, View = View.LargeIcon, CheckBoxes = true, MultiSelect = false,
                ShowItemToolTips = true, LargeImageList = thumbnails, AccessibleName = "表情缩略图列表" };
            layout.Controls.Add(gallery, 0, 1);
            IntPtr imageListHandle = thumbnails.Handle;
            using (var placeholder = new Bitmap(edge, edge))
            {
                using (var graphics = Graphics.FromImage(placeholder)) graphics.Clear(Color.FromArgb(233, 238, 246));
                thumbnails.Images.Add(placeholder);
            }
            gallery.BeginUpdate();
            foreach (var sticker in stickers)
            {
                string name = Path.GetFileName(sticker.Path);
                string label = sticker.Frames > 1 ? "动图 · " + name : name;
                gallery.Items.Add(new ListViewItem(label, 0) {
                    Tag = sticker, Checked = Selection.Contains(sticker), ToolTipText = name
                });
            }
            gallery.EndUpdate();
            gallery.ItemChecked += delegate { if (!changingChecks) UpdateSummary(); };
            gallery.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                var hit = gallery.HitTest(e.Location);
                if (hit.Item != null && (hit.Location & (ListViewHitTestLocations.Image | ListViewHitTestLocations.Label)) != 0)
                    hit.Item.Checked = !hit.Item.Checked;
            };

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            actions.Controls.Add(MakeButton("全选", delegate { ChangeChecks(row => true); }));
            actions.Controls.Add(MakeButton("全不选", delegate { ChangeChecks(row => false); }));
            actions.Controls.Add(MakeButton("反选", delegate { ChangeChecks(row => !row.Checked); }));
            var apply = MakeButton("应用选择", delegate
            {
                Selection = new HashSet<Sticker>(gallery.CheckedItems.Cast<ListViewItem>().Select(row => (Sticker)row.Tag));
                DialogResult = DialogResult.OK;
            });
            apply.BackColor = Color.FromArgb(37, 99, 235);
            apply.ForeColor = Color.White;
            actions.Controls.Add(apply);
            var cancel = MakeButton("取消", delegate { DialogResult = DialogResult.Cancel; });
            actions.Controls.Add(cancel);
            CancelButton = cancel;
            layout.Controls.Add(actions, 0, 2);
            UpdateSummary();
            Shown += async delegate { await LoadThumbnails(edge); };
        }

        static Button MakeButton(string text, EventHandler click)
        {
            var button = new Button { Text = text, Width = 82, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            button.FlatAppearance.BorderColor = Color.FromArgb(219, 226, 236);
            button.Click += click;
            return button;
        }

        void ChangeChecks(Func<ListViewItem, bool> choose)
        {
            changingChecks = true;
            gallery.BeginUpdate();
            try
            {
                foreach (ListViewItem row in gallery.Items) row.Checked = choose(row);
            }
            finally
            {
                gallery.EndUpdate();
                changingChecks = false;
            }
            UpdateSummary();
        }

        void UpdateSummary()
        {
            summary.Text = "已选 " + gallery.CheckedItems.Count + " / " + stickers.Length + " 张\r\n点击缩略图或勾选框选择表情。";
        }

        async Task LoadThumbnails(int edge)
        {
            const int batchSize = 12;
            for (int start = 0; start < stickers.Length; start += batchSize)
            {
                int first = start;
                int count = Math.Min(batchSize, stickers.Length - first);
                var images = await Task.Run(() => Enumerable.Range(first, count)
                    .Select(index => CreateThumbnail(stickers[index].Path, edge)).ToArray());
                try
                {
                    if (IsDisposed || !Visible) return;
                    gallery.BeginUpdate();
                    try
                    {
                        for (int i = 0; i < images.Length; i++)
                        {
                            if (images[i] == null) continue;
                            thumbnails.Images.Add(images[i]);
                            gallery.Items[first + i].ImageIndex = thumbnails.Images.Count - 1;
                        }
                    }
                    finally { gallery.EndUpdate(); }
                }
                finally
                {
                    foreach (var image in images) if (image != null) image.Dispose();
                }
            }
        }

        static Bitmap CreateThumbnail(string path, int edge)
        {
            Bitmap thumbnail = null;
            try
            {
                using (var image = Image.FromFile(path))
                {
                    Size size = Converter.Fit(image.Width, image.Height, edge - 8);
                    thumbnail = new Bitmap(edge, edge, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(thumbnail))
                    {
                        graphics.Clear(Color.FromArgb(247, 249, 252));
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(image, new Rectangle((edge - size.Width) / 2, (edge - size.Height) / 2, size.Width, size.Height));
                    }
                }
                return thumbnail;
            }
            catch
            {
                if (thumbnail != null) thumbnail.Dispose();
                return null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && thumbnails != null)
            {
                gallery.LargeImageList = null;
                thumbnails.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
