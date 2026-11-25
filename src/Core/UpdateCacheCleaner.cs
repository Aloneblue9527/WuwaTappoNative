using System;
using System.IO;
using System.Text.RegularExpressions;

namespace WuwatappoNative.Core;

/// <summary>
/// 清理 updates 目录中已经过期的安装包。
/// 逐个删除版本早于当前版本的 Setup；同版本、新版本和无法解析版本的文件保留，
/// 避免误删正在使用或仍可能用到的更新包。
/// </summary>
internal static class UpdateCacheCleaner
{
    static readonly Regex InstallerName = new(
        @"^WuwatappoNative-(?<version>.+)-Setup\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 删除目录中早于当前版本的 Setup 安装包，返回实际删除数量。
    /// 目录不存在或当前版本无法解析时不做任何操作；无法解析版本的 Setup 保留。
    /// 单个文件删除失败会被忽略，不影响启动。
    /// </summary>
    public static int CleanupObsoleteInstallers(string directory, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(directory) ||
            !UpdateVersion.TryParse(currentVersion, out var current) ||
            !Directory.Exists(directory))
            return 0;

        string[] paths;
        try { paths = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return 0; }

        int deleted = 0;
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            var match = InstallerName.Match(name);
            if (!match.Success) continue;
            // 文件名像本程序的安装包，但版本无法解析：无法判断新旧，保留该文件。
            if (!UpdateVersion.TryParse(match.Groups["version"].Value, out var version)) continue;
            if (version.CompareTo(current) >= 0) continue;
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch { }
        }
        return deleted;
    }
}
