namespace NasWallpaper.Models;

/// <summary>图片旋转方向（每次 90°）。</summary>
public enum RotateDirection
{
    Clockwise = 0,
    CounterClockwise = 1
}

/// <summary>旋转并写回图片源后的结果。</summary>
public sealed record RotateImageResult(
    bool Success,
    string Message,
    long Length = 0,
    DateTimeOffset LastWriteUtc = default);
