using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NasWallpaper.Models;

namespace NasWallpaper.Services.Editing;

/// <summary>
/// 把图片按 90° 旋转后重新编码。
/// 输出不带 EXIF 方向标志，这样旋转结果在任何软件里都和界面上看到的一致。
/// </summary>
public static class ImageRotator
{
    /// <summary>防止超大图片在旋转时把内存撑爆。</summary>
    private const long MaximumPixels = 120_000_000;

    public static byte[] Rotate(byte[] data, string extension, RotateDirection direction)
    {
        using var input = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(
            input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidOperationException("图片内容无法识别。");

        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > MaximumPixels)
            throw new InvalidOperationException("图片像素过大，暂不支持旋转。");

        // WPF 的 RotateTransform 以顺时针为正角。
        var rotated = new TransformedBitmap(frame,
            new RotateTransform(direction == RotateDirection.Clockwise ? 90 : -90));
        rotated.Freeze();

        BitmapEncoder encoder = Path.GetExtension(extension).ToLowerInvariant() switch
        {
            ".png" => new PngBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => new JpegBitmapEncoder { QualityLevel = 92 }
        };
        encoder.Frames.Add(BitmapFrame.Create(rotated));

        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
