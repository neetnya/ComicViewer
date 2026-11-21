using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComicViewer.Models;

namespace ComicViewer.Services;

/// <summary>
/// 配置服务：优先将 <see cref="AppConfig"/> 读写到 exe 同目录的 config.json，
/// 实现便携式持久化；若 exe 位于只读目录（如 Program Files），则自动回退到 LocalAppData。
/// </summary>
public static class ConfigService
{
    private static readonly string PortableConfigPath = GetPortableConfigPath();
    private static readonly string FallbackConfigPath = GetFallbackConfigPath();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>便携配置文件路径：exe 同目录下 config.json。</summary>
    public static string GetPortableConfigPath()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(exeDir, "config.json");
    }

    /// <summary>只读目录回退路径：%LocalAppData%\ComicViewer\config.json。</summary>
    public static string GetFallbackConfigPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "ComicViewer", "config.json");
    }

    /// <summary>加载配置；优先读 exe 同目录，不存在则尝试回退目录，异常时返回默认配置。</summary>
    public static AppConfig Load()
    {
        var path = File.Exists(PortableConfigPath)
            ? PortableConfigPath
            : File.Exists(FallbackConfigPath)
                ? FallbackConfigPath
                : null;

        if (path == null)
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            cfg.Sanitize();
            return cfg;
        }
        catch
        {
            return new AppConfig();
        }
    }

    /// <summary>保存配置；优先写 exe 同目录，无写权限时自动回退到 LocalAppData。</summary>
    public static void Save(AppConfig config)
    {
        config.Sanitize();
        var json = JsonSerializer.Serialize(config, JsonOpts);

        try
        {
            File.WriteAllText(PortableConfigPath, json);
            return;
        }
        catch
        {
            // exe 目录可能只读（如 Program Files），尝试回退目录
        }

        try
        {
            var dir = Path.GetDirectoryName(FallbackConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FallbackConfigPath, json);
        }
        catch
        {
            // 静默失败：不应因写配置失败而崩溃
        }
    }
}
