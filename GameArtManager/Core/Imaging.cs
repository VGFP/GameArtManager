using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using SkiaSharp;

namespace GameArtManager.Core
{
    /// <summary>Image download + PNG conversion (webp/jpg/bmp/gif/ico -> PNG, <= maxSide).</summary>
    public static class Imaging
    {
        private static readonly HttpClient Http = new HttpClient();

        // hard cap on untrusted CDN downloads; real SGDB art is far smaller
        private const long MaxDownloadBytes = 20 * 1024 * 1024;

        static Imaging()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("GameArtManager/1.0");
        }

        public static async Task<byte[]> DownloadAsync(string url)
        {
            using (var resp = await Http.GetAsync(url))
            {
                resp.EnsureSuccessStatusCode();

                // untrusted CDN content: enforce the size cap on the declared length...
                var declared = resp.Content.Headers.ContentLength;
                if (declared.HasValue && declared.Value > MaxDownloadBytes)
                    throw new InvalidDataException("Image too large");

                // ...and on the actual bytes read
                var data = await resp.Content.ReadAsByteArrayAsync();
                if (data.Length > MaxDownloadBytes)
                    throw new InvalidDataException("Image too large");
                return data;
            }
        }

        /// <summary>Decode any Skia-supported image and re-encode as PNG.</summary>
        public static byte[] ConvertToPng(byte[] data, int maxSide = 1024)
        {
            using (var bitmap = SKBitmap.Decode(data))
            {
                if (bitmap == null)
                    throw new InvalidDataException("Unsupported or corrupt image data");

                SKBitmap source;
                using (source = bitmap)
                {
                    var maxDim = Math.Max(bitmap.Width, bitmap.Height);
                    if (maxDim > maxSide)
                    {
                        var scale = (float)maxSide / maxDim;
                        var resized = bitmap.Resize(
                            new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)),
                            SKFilterQuality.Medium);
                        if (resized != null) source = resized;
                    }

                    using (var image = SKImage.FromBitmap(source))
                    using (var encoded = image.Encode(SKEncodedImageFormat.Png, 90))
                    using (var stream = new MemoryStream())
                    {
                        encoded.SaveTo(stream);
                        return stream.ToArray();
                    }
                }
            }
        }

        public static async Task<byte[]> DownloadAsPngAsync(string url, int maxSide = 1024)
        {
            return ConvertToPng(await DownloadAsync(url), maxSide);
        }

        /// <summary>Download and normalize into a SQUARE png tile: square sources pass
        /// through; non-square art (covers, landscape grids) is placed on a blurred,
        /// zoomed copy of itself so the Xbox app never stretches the art.</summary>
        public static async Task<byte[]> DownloadSquarePngAsync(string url, int maxSide = 1024)
        {
            return ConvertToSquarePng(await DownloadAsync(url), maxSide);
        }

        public static byte[] ConvertToSquarePng(byte[] data, int maxSide = 1024)
        {
            using (var decoded = SKBitmap.Decode(data))
            {
                if (decoded == null)
                    throw new InvalidDataException("Unsupported or corrupt image data");

                if (decoded.Width == decoded.Height)
                    return ConvertToPng(data, maxSide);

                var side = Math.Min(maxSide, Math.Max(decoded.Width, decoded.Height));
                using (var surface = SKSurface.Create(new SKImageInfo(side, side)))
                {
                    var canvas = surface.Canvas;
                    canvas.Clear(SKColors.Transparent);

                    // background: zoomed-to-fill copy, blurred, so bars read as ambience
                    var fillScale = (float)side / Math.Min(decoded.Width, decoded.Height);
                    var fillW = (int)(decoded.Width * fillScale);
                    var fillH = (int)(decoded.Height * fillScale);
                    using (var zoomed = decoded.Resize(new SKImageInfo(Math.Max(1, fillW), Math.Max(1, fillH)), SKFilterQuality.Medium))
                    using (var bgImage = SKImage.FromBitmap(zoomed))
                    using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(side * 0.05f, side * 0.05f) })
                    {
                        canvas.DrawImage(bgImage, (side - fillW) / 2f, (side - fillH) / 2f, paint);
                    }

                    // foreground: the art itself, fit fully inside the square
                    var fitScale = (float)side / Math.Max(decoded.Width, decoded.Height);
                    var fitW = (int)(decoded.Width * fitScale);
                    var fitH = (int)(decoded.Height * fitScale);
                    using (var fitted = decoded.Resize(new SKImageInfo(Math.Max(1, fitW), Math.Max(1, fitH)), SKFilterQuality.High))
                    using (var fgImage = SKImage.FromBitmap(fitted))
                    {
                        canvas.DrawImage(fgImage, (side - fitW) / 2f, (side - fitH) / 2f);
                    }

                    using (var snapshot = surface.Snapshot())
                    using (var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 90))
                    using (var stream = new MemoryStream())
                    {
                        encoded.SaveTo(stream);
                        return stream.ToArray();
                    }
                }
            }
        }

        public static byte[] FileToPng(string path, int maxSide = 1024)
        {
            return ConvertToPng(File.ReadAllBytes(path), maxSide);
        }

        /// <summary>PNG-convert a user-picked file. The broker-backed StorageFile
        /// stream is tried first: direct System.IO reads of picked files come back
        /// "Access denied" when broadFileSystemAccess isn't effectively granted
        /// (common on sideloaded machines) or when the picker grant can't be ACL'd
        /// (e.g. exFAT drives) — the WinRT stream always works. Direct path access
        /// stays as the fallback.</summary>
        public static async Task<byte[]> FileToPngAsync(Windows.Storage.StorageFile file, int maxSide = 1024)
        {
            try
            {
                using (var stream = await file.OpenReadAsync())
                using (var mem = new MemoryStream())
                {
                    await stream.AsStreamForRead().CopyToAsync(mem);
                    return ConvertToPng(mem.ToArray(), maxSide);
                }
            }
            catch (Exception)
            {
                // broker read denied too (rare) — last resort: direct path access
            }
            return FileToPng(file.Path, maxSide);
        }
    }
}
