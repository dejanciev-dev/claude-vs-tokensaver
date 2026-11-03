using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeVS.Core.Context;

namespace ClaudeVS.ToolWindow
{
    /// <summary>An image waiting to be sent: the encoded data for Claude plus a small preview for the chat.</summary>
    internal sealed class ImageAttachment
    {
        public PromptImage Image { get; set; }
        public BitmapSource Preview { get; set; }
    }

    /// <summary>
    /// Turns clipboard images and image files into <see cref="PromptImage"/>s. Images are scaled to fit 1568 px (the
    /// API scales larger ones down anyway, so the extra pixels would only cost upload time) and encoded as PNG, or as
    /// JPEG when the PNG would pass the 5 MB per-image limit (large photos).
    /// </summary>
    internal static class ImageAttachments
    {
        /// <summary>True when the clipboard holds a bitmap, or files that are all images.</summary>
        public static bool ClipboardHasImage()
        {
            try
            {
                if (Clipboard.ContainsImage() || Clipboard.ContainsData("PNG")) return true;
                if (!Clipboard.ContainsFileDropList()) return false;
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                return files.Count > 0 && files.All(f => ImageTokens.MediaTypeFor(f) != null);
            }
            catch (Exception) { return false; } // clipboard busy
        }

        /// <summary>Images on the clipboard: copied image files, or one bitmap (a screenshot).</summary>
        public static List<ImageAttachment> FromClipboard()
        {
            var result = new List<ImageAttachment>();
            if (Clipboard.ContainsFileDropList())
            {
                foreach (string f in Clipboard.GetFileDropList())
                    if (ImageTokens.MediaTypeFor(f) != null) result.Add(FromFile(f));
                if (result.Count > 0) return result;
            }

            BitmapSource bmp = null;
            // Snipping Tool, browsers and Office also put a "PNG" stream on the clipboard; it keeps transparency
            // correct, while the DIB that Clipboard.GetImage() reads often comes out with a broken alpha channel.
            if (Clipboard.GetData("PNG") is MemoryStream png)
            {
                try { bmp = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); }
                catch (Exception) { bmp = null; }
            }
            if (bmp == null)
            {
                var dib = Clipboard.GetImage();
                if (dib != null) bmp = new FormatConvertedBitmap(dib, PixelFormats.Bgr32, null, 0); // drop the unreliable alpha
            }
            if (bmp != null) result.Add(FromBitmap(bmp, "Pasted image"));
            return result;
        }

        public static ImageAttachment FromFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return FromBitmap(frame, Path.GetFileName(path));
            }
        }

        public static ImageAttachment FromBitmap(BitmapSource source, string name)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var (w, h) = ImageTokens.FitWithin(source.PixelWidth, source.PixelHeight);
            BitmapSource scaled = source;
            if (w != source.PixelWidth || h != source.PixelHeight)
                scaled = new TransformedBitmap(source, new ScaleTransform((double)w / source.PixelWidth, (double)h / source.PixelHeight));

            byte[] bytes = Encode(new PngBitmapEncoder(), scaled);
            string mediaType = "image/png";
            if (bytes.Length * 4 / 3 > ImageTokens.MaxBytes) // the limit applies to the base64 text
            {
                bytes = Encode(new JpegBitmapEncoder { QualityLevel = 85 }, new FormatConvertedBitmap(scaled, PixelFormats.Bgr24, null, 0));
                mediaType = "image/jpeg";
            }
            if (bytes.Length * 4 / 3 > ImageTokens.MaxBytes) throw new InvalidOperationException($"{name} is too large to send, even scaled down.");

            return new ImageAttachment
            {
                Image = new PromptImage { MediaType = mediaType, Base64 = Convert.ToBase64String(bytes), Width = scaled.PixelWidth, Height = scaled.PixelHeight, Name = name },
                Preview = Thumbnail(scaled, 96),
            };
        }

        private static byte[] Encode(BitmapEncoder encoder, BitmapSource bmp)
        {
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        private static BitmapSource Thumbnail(BitmapSource bmp, int maxEdge)
        {
            double scale = Math.Min(1.0, (double)maxEdge / Math.Max(bmp.PixelWidth, bmp.PixelHeight));
            BitmapSource thumb = scale < 1 ? new TransformedBitmap(bmp, new ScaleTransform(scale, scale)) : bmp;
            var frozen = new WriteableBitmap(thumb); // a plain, freezable copy that does not keep the full image alive
            frozen.Freeze();
            return frozen;
        }
    }
}
