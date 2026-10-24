using Microsoft.Win32;
using NasWallpaper.Models;
using System.IO;
using System.Windows;

namespace NasWallpaper.UI;

/// <summary>在深色/浅色资源字典之间切换，供运行时预览与启动恢复使用。</summary>
public static class ThemeManager
{
    private const string ThemePath = "/UI/Themes/{0}.xaml";

    public static bool IsLight { get; private set; }

    /// <summary>主题真正发生变化时触发。</summary>
    public static event Action? Changed;

    public static void Apply(AppTheme mode) => ApplyVariant(Resolve(mode) == AppTheme.Light);

    /// <summary>把“跟随系统”解析成明确的深色或浅色。</summary>
    public static AppTheme Resolve(AppTheme mode) =>
        mode == AppTheme.System ? (IsSystemLight() ? AppTheme.Light : AppTheme.Dark) : mode;

    public static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void ApplyVariant(bool light)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(string.Format(ThemePath, light ? "Light" : "Dark"), UriKind.Relative)
        };
        if (app.Resources.MergedDictionaries.Count == 0) app.Resources.MergedDictionaries.Add(dictionary);
        else app.Resources.MergedDictionaries[0] = dictionary;

        if (IsLight == light) return;
        IsLight = light;
        Changed?.Invoke();
    }
}
