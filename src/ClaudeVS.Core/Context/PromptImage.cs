using System;

namespace ClaudeVS.Core.Context
{
    /// <summary>An image sent with a prompt (a pasted screenshot or an image file), already encoded.</summary>
    public sealed class PromptImage
    {
        /// <summary>"image/png", "image/jpeg", "image/gif" or "image/webp".</summary>
        public string MediaType { get; set; }
        public string Base64 { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>Where it came from, for the attachment line ("Screenshot", a file name).</summary>
        public string Name { get; set; }

        public int EstimatedTokens => ImageTokens.Estimate(Width, Height);
        public long Bytes => Base64 == null ? 0 : Base64.Length / 4 * 3;
    }

    /// <summary>
    /// Image size and cost rules from Anthropic's vision docs: an image costs about width × height / 750 tokens,
    /// and anything with a long edge over 1568 px is scaled down by the API anyway (so sending it larger only costs
    /// upload time). Each image may be at most 5 MB.
    /// </summary>
    public static class ImageTokens
    {
        public const int MaxLongEdge = 1568;
        public const long MaxBytes = 5 * 1024 * 1024;

        public static int Estimate(int width, int height) =>
            width <= 0 || height <= 0 ? 0 : (int)Math.Ceiling(width * (double)height / 750);

        /// <summary>Size that fits within <paramref name="maxLongEdge"/>, keeping the aspect ratio; unchanged when it already fits.</summary>
        public static (int Width, int Height) FitWithin(int width, int height, int maxLongEdge = MaxLongEdge)
        {
            if (width <= 0 || height <= 0) return (width, height);
            int longEdge = Math.Max(width, height);
            if (longEdge <= maxLongEdge) return (width, height);
            double scale = (double)maxLongEdge / longEdge;
            return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        /// <summary>Media type for an image file extension; null when it is not a format Claude accepts.</summary>
        public static string MediaTypeFor(string path)
        {
            switch (System.IO.Path.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return null;
            }
        }
    }
}
