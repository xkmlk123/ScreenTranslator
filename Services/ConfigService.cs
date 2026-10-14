using System.IO;
using Newtonsoft.Json;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services;

/// <summary>
/// 配置读写服务
/// </summary>
public static class ConfigService
{
    private static readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                return JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { /* 读取失败用默认 */ }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        try
        {
            string json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* 保存失败忽略 */ }
    }
}
