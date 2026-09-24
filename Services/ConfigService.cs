using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComicViewer.Models;

namespace ComicViewer.Services;

/// <summary>
/// 配置服务：负责将 <see cref="AppConfig"/> 序列化/反序列化到 exe 同目录的 JSON 文件中，
/// 实现便携式持久化记忆。
/// </summary>
public static class ConfigService
{
    private static readonly string ConfigPath = GetConfigPath();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>配置文件路径：exe 同目录下 config.json。</summary>
    public static string GetConfigPath()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(exeDir, "config.json");
    }

    /// <summary>加载配置；若不存在或损坏则返回默认配置。</summary>
    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();

            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
            return cfg ?? new AppConfig();
        }
        catch
        {
            // 便携式软件容错：任何异常都回退默认，不弹窗打断用户。
            return new AppConfig();
        }
    }

    /// <summary>保存配置到 exe 同目录。</summary>
    public static void Save(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 静默失败：便携式软件不应因写配置失败而崩溃。
        }
    }
}
