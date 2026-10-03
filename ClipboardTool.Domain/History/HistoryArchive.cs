using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipboardTool.Domain.History;

/// <summary>
/// 旧档 JSON 的解析与投影唯一归属（F08；02-spec/02 §1 存档契约）：
/// 历史是 JSON 数组，不是包一层 { entries: ... }；camelCase 键；
/// text/imagePath 按类型互斥投影；单条宽松解析——缺省字段归一化默认值，
/// 出现但类型坏的字段让整条作废（缺省与类型错是两回事，与 serde 语义一致）；
/// 单条损坏只丢该条，不整体失败。
/// </summary>
public static class HistoryArchive
{
    /// <summary>与 legacy serde_json 同形的写出：中文与 + 等字符原样保留，只转义引号/控制字符——存档是给人看的。</summary>
    private static readonly JsonSerializerOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>解析存档文本为条目数组；不是合法 JSON 数组（整体坏档/对象包裹/空文本）返回 null。</summary>
    public static IReadOnlyList<JsonNode?>? TryParseArray(string? json)
    {
        if (json is null)
        {
            return null;
        }
        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
            {
                return null;
            }
            return array.ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>单条宽松解析：id 非空字符串、type 精确 text/image、对应内容字段必需。
    /// 返回 null 表示该条损坏，由调用方只丢这一条。</summary>
    public static HistoryEntry? TryReadEntry(JsonNode? element)
    {
        if (element is not JsonObject obj)
        {
            return null;
        }
        if (!TryReadId(obj, out var id))
        {
            return null;
        }
        if (obj["type"] is not JsonValue typeValue || !typeValue.TryGetValue(out string? typeName))
        {
            return null;
        }
        var kind = typeName switch
        {
            "text" => EntryKind.Text,
            "image" => EntryKind.Image,
            _ => (EntryKind?)null, // 未知类型跳过该条
        };
        if (kind is null)
        {
            return null;
        }

        // text/imagePath 是互斥可选字段（Option）：缺省或显式 null → 无；出现就必须是字符串
        var hasText = TryReadOptionalString(obj, "text", out var text, out var textInvalid);
        var hasPath = TryReadOptionalString(obj, "imagePath", out var imagePath, out var pathInvalid);
        if (textInvalid || pathInvalid)
        {
            return null;
        }
        if (kind == EntryKind.Text && !hasText)
        {
            return null;
        }
        if (kind == EntryKind.Image && !hasPath)
        {
            return null;
        }

        if (!TryReadMs(obj, "createdAt", out var createdAt) || !TryReadMs(obj, "pinnedAt", out var pinnedAt))
        {
            return null;
        }
        if (!TryReadBool(obj, "pinned", out var pinned))
        {
            return null;
        }
        if (!TryReadStringWithDefault(obj, "note", out var note))
        {
            return null;
        }
        if (!TryReadSourceApp(obj, out var sourceApp))
        {
            return null;
        }

        return new HistoryEntry(id, kind.Value, text, imagePath, createdAt, sourceApp, pinned, pinnedAt, note);
    }

    private static bool TryReadId(JsonObject obj, out string id)
    {
        id = string.Empty;
        if (obj["id"] is not JsonValue value || !value.TryGetValue(out string? parsed) || parsed.Length == 0)
        {
            return false; // 缺失/类型坏/空 id 一律作废
        }
        id = parsed;
        return true;
    }

    /// <summary>可选字符串（text/imagePath/iconDataUrl，对应 serde Option）：缺省或显式 null → 无值；
    /// 出现但类型坏 → invalid（整条作废）。返回值表示「有值」。</summary>
    private static bool TryReadOptionalString(JsonObject obj, string key, out string? value, out bool invalid)
    {
        value = null;
        invalid = false;
        if (obj[key] is not JsonValue node)
        {
            return false;
        }
        if (!node.TryGetValue(out string? parsed))
        {
            invalid = true;
            return false;
        }
        value = parsed;
        return true;
    }

    /// <summary>缺省型字符串（note 与来源对象内三键，对应 serde String + default）：
    /// 缺省 → 默认值；显式 null 或类型坏 → 整条作废。</summary>
    private static bool TryReadStringWithDefault(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (!obj.ContainsKey(key))
        {
            return true;
        }
        if (obj[key] is not JsonValue node || !node.TryGetValue(out string? parsed))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    /// <summary>epoch 毫秒整数：缺省 0；出现则必须是非负整数（字符串/浮点/负数/null → 整条作废）。</summary>
    private static bool TryReadMs(JsonObject obj, string key, out long value)
    {
        value = 0;
        if (!obj.ContainsKey(key))
        {
            return true;
        }
        if (obj[key] is not JsonValue node || !node.TryGetValue(out long ms) || ms < 0)
        {
            return false;
        }
        value = ms;
        return true;
    }

    /// <summary>布尔：缺省 false；出现则必须是布尔（字符串 "yes"/null → 整条作废）。</summary>
    private static bool TryReadBool(JsonObject obj, string key, out bool value)
    {
        value = false;
        if (!obj.ContainsKey(key))
        {
            return true;
        }
        if (obj[key] is not JsonValue node || !node.TryGetValue(out value))
        {
            return false;
        }
        return true;
    }

    /// <summary>来源应用（serde Option&lt;SourceApp&gt;）：缺省或显式 null → 无；
    /// 出现则必须是对象，四键内字符串缺省空串、iconDataUrl 可空，字段类型坏 → 整条作废。</summary>
    private static bool TryReadSourceApp(JsonObject obj, out SourceApp? sourceApp)
    {
        sourceApp = null;
        var node = obj["sourceApp"];
        if (node is null)
        {
            return true;
        }
        if (node is not JsonObject sa)
        {
            return false;
        }
        if (!TryReadStringWithDefault(sa, "exePath", out var exePath)
            || !TryReadStringWithDefault(sa, "appName", out var appName)
            || !TryReadStringWithDefault(sa, "windowTitle", out var windowTitle))
        {
            return false;
        }
        var hasIcon = TryReadOptionalString(sa, "iconDataUrl", out var iconDataUrl, out var iconInvalid);
        if (iconInvalid)
        {
            return false;
        }
        sourceApp = new SourceApp(exePath, appName, windowTitle, hasIcon ? iconDataUrl : null);
        return true;
    }

    /// <summary>序列化投影（存档契约）：键名 camelCase，时间毫秒整数；
    /// 文本条目不写 imagePath、图片条目不写 text（互斥可选字段不随意填 null）；
    /// sourceApp 恒写（null 或对象，与旧档样例同形）。</summary>
    public static string Serialize(IReadOnlyList<HistoryEntry> entries)
    {
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            array.Add(WriteEntry(entry));
        }
        return array.ToJsonString(WriterOptions);
    }

    private static JsonObject WriteEntry(HistoryEntry entry)
    {
        var obj = new JsonObject
        {
            ["id"] = entry.Id,
            ["type"] = entry.Type == EntryKind.Text ? "text" : "image",
        };
        if (entry.Type == EntryKind.Text)
        {
            obj["text"] = entry.Text;
        }
        else
        {
            obj["imagePath"] = entry.ImagePath;
        }
        obj["createdAt"] = entry.CreatedAtMs;
        obj["sourceApp"] = entry.SourceApp is null ? null : WriteSourceApp(entry.SourceApp);
        obj["pinned"] = entry.Pinned;
        obj["pinnedAt"] = entry.PinnedAtMs;
        obj["note"] = entry.Note;
        return obj;
    }

    private static JsonObject WriteSourceApp(SourceApp sourceApp) => new()
    {
        ["exePath"] = sourceApp.ExePath,
        ["appName"] = sourceApp.AppName,
        ["windowTitle"] = sourceApp.WindowTitle,
        ["iconDataUrl"] = sourceApp.IconDataUrl,
    };
}
