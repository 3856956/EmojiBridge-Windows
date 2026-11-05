using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace EmojiBridge
{
    static class SelfTest
    {
        static readonly List<string> passed = new List<string>();
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            passed.Add(message);
        }

        public static void Run(string root)
        {
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            string fixtures = Path.Combine(root, "fixtures-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(fixtures);
            string png = Path.Combine(fixtures, "透明表情.png");
            using (var image = new Bitmap(1200, 600))
            {
                using (var g = Graphics.FromImage(image)) { g.Clear(Color.Transparent); g.FillEllipse(Brushes.CornflowerBlue, 300, 100, 600, 400); }
                image.Save(png, ImageFormat.Png);
            }
            // A two-frame GIF fixture, deliberately saved with the wrong extension.
            string gif = Path.Combine(fixtures, "动图.jpg");
            byte[] gifBytes = new byte[] {
                71,73,70,56,57,97, 1,0,1,0,128,0,0, 0,0,0,255,255,255,
                33,255,11,78,69,84,83,67,65,80,69,50,46,48,3,1,0,0,0,
                33,249,4,0,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,68,1,0,
                33,249,4,0,10,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,76,1,0,59 };
            File.WriteAllBytes(gif, gifBytes);
            File.Copy(png, Path.Combine(fixtures, "重复.PNG"));
            string bad = Path.Combine(fixtures, "损坏.png"); File.WriteAllText(bad, "not an image");
            var staticSticker = Converter.Inspect(png);
            var animatedSticker = Converter.Inspect(gif);
            Check(animatedSticker.Format == "GIF" && animatedSticker.Frames == 2 && animatedSticker.Preserve, "Detect GIF by content and retain both frames");
            Check(Converter.Inspect(Path.Combine(fixtures, "重复.PNG")).Hash == staticSticker.Hash, "SHA256 identifies identical content under different names");
            bool rejected = false; try { Converter.Inspect(bad); } catch { rejected = true; }
            Check(rejected, "Reject corrupt image without treating it as valid");
            var enumerationErrors = new List<string>();
            Check(Converter.Enumerate(new[] { fixtures, png }, enumerationErrors.Add).Count() == 4, "Folder import includes images and avoids repeated paths");
            var result = Converter.Export(new[] { staticSticker, animatedSticker }, new ExportOptions { Destination = root, MaxEdge = 512, Zip = true }, delegate { });
            Check(result.Count == 2 && result.Errors.Count == 0, "Batch export succeeds for static and animated images");
            string exportedPng = Directory.GetFiles(result.Directory, "*.png").Single();
            using (var image = new Bitmap(exportedPng))
            {
                Check(image.Width == 512 && image.Height == 256, "Resize preserves aspect ratio");
                Check(image.GetPixel(0, 0).A == 0, "PNG retains transparent background");
            }
            string exportedGif = Directory.GetFiles(result.Directory, "*.gif").Single();
            Check(File.ReadAllBytes(exportedGif).SequenceEqual(gifBytes), "GIF export is byte-for-byte identical");
            Check(Converter.Inspect(exportedGif).Frames == 2, "Exported GIF still has two animation frames");
            using (var archive = ZipFile.OpenRead(result.ZipPath))
            {
                Check(archive.Entries.Count == 5 && archive.Entries.Any(e => e.Name.EndsWith(".gif")), "ZIP includes both images, gallery, manifest and instructions");
            }
            var manifest = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(result.Directory, "manifest.json"))) as object[];
            Check(manifest != null && manifest.Length == 2, "Manifest contains every successful export");
            Check(File.ReadAllText(Path.Combine(result.Directory, "index.html")).Contains("%"), "Offline gallery encodes non-ASCII image URLs");
            Check(Converter.Fit(20, 10, 512) == new Size(20, 10), "Small images are never enlarged");
            Check(Converter.Fit(10000, 1, 128).Height == 1, "Extreme aspect ratios retain at least one pixel");
            var jpegResult = Converter.Export(new[] { staticSticker }, new ExportOptions { Destination = root, MaxEdge = 128, Jpeg = true, Zip = false }, delegate { });
            using (var jpeg = new Bitmap(Directory.GetFiles(jpegResult.Directory, "*.jpg").Single()))
            {
                Color corner = jpeg.GetPixel(0, 0);
                Check(jpeg.Width == 128 && jpeg.Height == 64 && corner.R > 245 && corner.G > 245 && corner.B > 245, "JPG export flattens transparency onto white");
            }
            Check(jpegResult.ZipPath == null, "ZIP creation is optional");
            var again = Converter.Export(new[] { staticSticker }, new ExportOptions { Destination = root, Zip = false }, delegate { });
            Check(again.Directory != jpegResult.Directory && again.Directory != result.Directory, "Each export uses a unique directory without overwriting");
            File.AppendAllText(png, "changed");
            var changed = Converter.Export(new[] { staticSticker, animatedSticker }, new ExportOptions { Destination = root, Zip = false }, delegate { });
            Check(changed.Count == 1 && changed.Errors.Count == 1 && File.Exists(Path.Combine(changed.Directory, "errors.txt")), "Changed source is skipped and other images still export");
            Check(Converter.SafeName("../a<b>\"c|").IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "Export filenames cannot contain path separators or invalid characters");
            Check(File.ReadAllBytes(gif).SequenceEqual(gifBytes), "Export leaves original files unchanged");
            string syntheticData = Path.Combine(fixtures, "xwechat_files");
            string syntheticAccount = Path.Combine(syntheticData, "wxid_test", "db_storage", "emoticon");
            Directory.CreateDirectory(syntheticAccount); File.WriteAllText(Path.Combine(syntheticAccount, "emoticon.db"), "synthetic");
            Directory.CreateDirectory(Path.Combine(syntheticData, "all_users"));
            Check(WeChatExtractor.Accounts(syntheticData).SequenceEqual(new[] { "wxid_test" }), "Discover only accounts containing an emoticon database");
            Check(WeChatExtractor.Accounts(Path.Combine(syntheticData, "wxid_test")).Single() == "wxid_test", "Accept an account folder as well as a data root");
            string fakeInstall = Path.Combine(fixtures, "Weixin", "4.1", "Weixin.exe");
            Check(WeChatExtractor.DetectRoots(fakeInstall).Contains(syntheticData), "Detect redirected data next to versioned WeChat installation");
            Check(WeChatExtractor.Quote("C:\\some path\\") == "\"C:\\some path\\\\\"", "Quote Windows paths with spaces and trailing backslashes");
            Check(WeChatExtractor.Quote("a\"b") == "\"a\\\"b\"", "Escape quotes without invoking a shell");
            string secret = new string('a', 64);
            Check(!WeChatExtractor.Redact("https://cdn.example/image?token=private " + secret).Contains("private") && !WeChatExtractor.Redact(secret).Contains(secret), "Redact download URLs and decryption values from visible logs");
            var parsed = WeChatExtractor.ParseResult("{\"total\":8,\"ok\":5,\"skipped\":1,\"failed\":2}", root);
            Check(parsed.Total == 8 && parsed.Ok == 6 && parsed.Failed == 2, "Parse extraction statistics including partial failures");
            bool invalidResult = false; try { WeChatExtractor.ParseResult("{}", root); } catch { invalidResult = true; }
            Check(invalidResult, "Reject malformed component output instead of claiming success");
            Check(File.Exists(WeChatExtractor.EnginePath) && Converter.HashFile(WeChatExtractor.EnginePath) == WeChatExtractor.EngineSha256, "Bundled extraction component matches pinned SHA256");
            File.WriteAllText(Path.Combine(root, "test-results.json"), new JavaScriptSerializer().Serialize(new { passed = passed.Count, checks = passed }), Encoding.UTF8);
        }
    }
}
