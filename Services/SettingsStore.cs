using System.IO;
using System.Text.Json;
using System.Windows;
using WoWSBattleAssistant.Models;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 配置持久化。存储到 %AppData%\WoWSBattleAssistant\settings.json
/// </summary>
public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WoWSBattleAssistant");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new RectJsonConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s != null) return s;
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 保存失败不阻断主流程
        }
    }

    /// <summary>把逻辑坐标 Rect 序列化为 "x,y,w,h" 之外的友好形式（已由 JsonSerializer 处理）</summary>
    public static string GetSettingsPath() => FilePath;
}

/// <summary>Rect 的 JSON 转换器（System.Text.Json 默认对 Rect 支持有限，这里手动处理）</summary>
public sealed class RectJsonConverter : System.Text.Json.Serialization.JsonConverter<Rect>
{
    public override Rect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return Rect.Empty;

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.Equals(s, "Empty", StringComparison.OrdinalIgnoreCase)) return Rect.Empty;

            var parts = s?.Split(',');
            if (parts != null && parts.Length == 4 &&
                double.TryParse(parts[0], out var x) &&
                double.TryParse(parts[1], out var y) &&
                double.TryParse(parts[2], out var w) &&
                double.TryParse(parts[3], out var h))
            {
                return new Rect(x, y, w, h);
            }
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            double x = 0, y = 0, w = 0, h = 0;
            bool isEmpty = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var prop = reader.GetString();
                reader.Read();
                if (string.Equals(prop, "IsEmpty", StringComparison.OrdinalIgnoreCase))
                {
                    isEmpty = reader.GetBoolean();
                    continue;
                }
                var val = reader.GetDouble();
                switch (prop)
                {
                    case "X": case "x": x = val; break;
                    case "Y": case "y": y = val; break;
                    case "Width": case "width": w = val; break;
                    case "Height": case "height": h = val; break;
                }
            }
            return isEmpty ? Rect.Empty : new Rect(x, y, w, h);
        }
        return Rect.Empty;
    }

    public override void Write(Utf8JsonWriter writer, Rect value, JsonSerializerOptions options)
    {
        // Rect.Empty 的 X/Y=+∞, Width/Height=-∞, 无法直接写成 JSON 数字,单独标记
        if (value.IsEmpty)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("IsEmpty", true);
            writer.WriteEndObject();
            return;
        }
        writer.WriteStartObject();
        writer.WriteNumber("X", value.X);
        writer.WriteNumber("Y", value.Y);
        writer.WriteNumber("Width", value.Width);
        writer.WriteNumber("Height", value.Height);
        writer.WriteEndObject();
    }
}
