using System.Text;
using System.Text.Json;
using PCActivityTracker.Domain;

namespace PCActivityTracker.Services;

// The model supplies section text, not Markdown structure. Prompt, schema,
// local parsing and quality validation share this one section definition.
internal static class AiReportFormat
{
    internal static IReadOnlyList<(string Key, string Heading)> Sections(AiReportType type) =>
        type == AiReportType.Daily
            ? [("overview", "概要"), ("time_allocation", "主な時間配分"),
               ("focus_interruptions", "集中と中断"), ("suggestions", "次回への提案"), ("summary", "要約")]
            : [("overview", "週の概要"), ("daily_changes", "日別の変化"),
               ("time_allocation", "主な時間配分"), ("focus_interruptions", "集中と中断"),
               ("suggestions", "来週への提案"), ("summary", "要約")];

    internal static JsonElement Schema(AiReportType type) => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = Sections(type).ToDictionary(section => section.Key, section => new
        {
            type = "string", minLength = 1, maxLength = section.Key == "summary" ? 140 : 400,
            description = section.Heading + "。集計値に基づく短い日本語。"
        }),
        required = Sections(type).Select(section => section.Key).ToArray(),
        additionalProperties = false
    });

    internal static string Render(string json, AiReportType type)
    {
        // Parse strictly: truncated output, unknown fields and empty sections
        // must never become a seemingly valid saved report.
        if (json.Length > 20000) throw Invalid("生成本文が長すぎます");
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { throw Invalid("生成結果が指定JSON形式ではありません（途中終了の可能性があります）"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("生成結果がJSONオブジェクトではありません");
            var sections = Sections(type);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!keys.Add(property.Name) || !sections.Any(section => section.Key == property.Name))
                    throw Invalid("生成結果に重複または未定義の項目があります");
            var builder = new StringBuilder();
            foreach (var (key, heading) in sections)
            {
                if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                    throw Invalid($"項目「{heading}」がありません、または文字列ではありません");
                var text = value.GetString()!.Trim();
                if (string.IsNullOrWhiteSpace(text) || text.Length > (key == "summary" ? 140 : 400))
                    throw Invalid($"項目「{heading}」の長さが不正です");
                if (text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
                    throw Invalid($"項目「{heading}」に制御文字があります");
                builder.AppendLine("## " + heading);
                // Flatten model-supplied line breaks so they cannot inject sections.
                builder.AppendLine(text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '));
                builder.AppendLine();
            }
            return builder.ToString().Trim();
        }
    }

    private static AiReportQualityException Invalid(string issue) => new([issue]);
}
