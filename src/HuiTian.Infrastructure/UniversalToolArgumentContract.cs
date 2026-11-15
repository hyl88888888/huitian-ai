using System.Text.Json;
using System.Text.Json.Nodes;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

// Protocol compatibility only. Never infer user intent or invent a target.
internal static class UniversalToolArgumentContract
{
    internal static JsonElement Validate(string capability, JsonElement input, JsonElement schema)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("参数必须是对象。");
        var value = JsonNode.Parse(input.GetRawText())!.AsObject();
        var properties = schema.GetProperty("properties");
        Rename("window", "windowTitle");
        if (capability == "respond") Rename("message", "text");
        if (capability is "click_control" or "type_text")
        {
            Rename("control", "elementName");
            Rename("target", "elementName");
        }
        if (capability is "send_chat_message" or "send_chat_files" &&
            value["platform"] is JsonValue platform && platform.TryGetValue<string>(out var identifier) &&
            CommunicationPlatformIdentity.Canonicalize(identifier) is { } canonical)
            value["platform"] = canonical;
        var normalized = JsonSerializer.SerializeToElement(value);
        try { ValidateNode(normalized, schema, "a"); }
        catch (ArgumentException exception)
        {
            var repair = capability is "send_chat_message" or "send_chat_files"
                ? "保留原联系人和内容，按允许值修正参数后继续使用专用发送能力；参数错误不代表发送失败，不要降级为盲目输入或按 Enter。"
                : capability is "click_control" or "type_text" or "type_active" or "click_point"
                    ? "先读取具名控件；已确认焦点的输入才用 type_active{text,fieldDescription,windowTitle?}，不能猜测目标。"
                    : "按签名修正参数后重试；不要因参数错误请求用户接管。";
            throw new ArgumentException($"参数无效，尚未执行：{exception.Message} 正确签名：{Signature(capability, schema)}。{repair}");
        }
        return normalized;

        void Rename(string alias, string canonical)
        {
            if (!properties.TryGetProperty(canonical, out _) || !value.TryGetPropertyValue(alias, out var old)) return;
            if (value.TryGetPropertyValue(canonical, out var existing) && !JsonNode.DeepEquals(existing, old))
                throw new ArgumentException($"参数 {alias} 与 {canonical} 冲突，尚未执行。");
            value[canonical] = old?.DeepClone();
            value.Remove(alias);
        }
    }

    internal static string Signature(string capability, JsonElement schema)
    {
        var required = schema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(v => v.GetString()).ToHashSet() : [];
        return capability + "{" + string.Join(",", schema.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name + (required.Contains(p.Name) ? "" : "?") +
                (p.Value.TryGetProperty("enum", out var choices) ? "=" + string.Join("|", choices.EnumerateArray().Select(ChoiceText)) : ""))) + "}";
    }

    private static string ChoiceText(JsonElement choice) => choice.ValueKind == JsonValueKind.String
        ? choice.GetString()! : choice.GetRawText();

    private static void ValidateNode(JsonElement value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("type", out var type))
        {
            var matches = type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                _ => true,
            };
            if (!matches) throw new ArgumentException($"{path} 应为 {type.GetString()}");
        }
        if (schema.TryGetProperty("enum", out var choices) &&
            !choices.EnumerateArray().Any(c => JsonElement.DeepEquals(c, value)))
            throw new ArgumentException($"{path} 不在允许值中；允许值：{string.Join("、", choices.EnumerateArray().Select(ChoiceText))}");
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var props))
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                    if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null ||
                        (field.ValueKind == JsonValueKind.String && name != "text" && name != "content" &&
                         string.IsNullOrWhiteSpace(field.GetString())))
                        throw new ArgumentException($"{path}.{name} 缺失或为空");
            foreach (var property in value.EnumerateObject())
            {
                if (props.TryGetProperty(property.Name, out var child)) ValidateNode(property.Value, child, path + "." + property.Name);
                else if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False)
                    throw new ArgumentException($"{path}.{property.Name} 不是有效参数");
            }
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble() ||
                schema.TryGetProperty("maximum", out var max) && number > max.GetDouble())
                throw new ArgumentException($"{path} 超出允许范围");
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var min) && value.GetArrayLength() < min.GetInt32() ||
                schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32())
                throw new ArgumentException($"{path} 数量超出范围");
            if (schema.TryGetProperty("items", out var itemSchema))
                foreach (var item in value.EnumerateArray()) ValidateNode(item, itemSchema, path + "[]");
        }
    }
}
