using System.Text;
using System.Text.Json;

namespace EtsVoiceBridge;

internal sealed record ContentBinding(string RealId, string CodeId);
internal sealed record TemplateBinding(string TemplateId, IReadOnlyList<ContentBinding> Contents);
internal sealed record ContentPosition(int Position, string Section, IReadOnlyList<int> Questions);

internal static class AssignmentOrderReader
{
    private static Dictionary<string, IReadOnlyList<TemplateBinding>> _bindings = new();

    public static void Refresh()
    {
        var bindings = new Dictionary<string, IReadOnlyList<TemplateBinding>>();
        var cache = Path.Combine(AppContext.BaseDirectory, "question-order-cache.json");
        try
        {
            if (File.Exists(cache))
                bindings = JsonSerializer.Deserialize<Dictionary<string, IReadOnlyList<TemplateBinding>>>(
                    File.ReadAllText(cache)) ?? bindings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ETS", "logs");
        try
        {
            foreach (var file in Directory.EnumerateFiles(logs, "*.log").OrderBy(File.GetLastWriteTime))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    while (reader.ReadLine() is { } line)
                        if (line.Contains("\"contents\"", StringComparison.Ordinal) &&
                            (line.Contains("\"structs\"", StringComparison.Ordinal) || line.Contains("\"struct\"", StringComparison.Ordinal)))
                            ReadLogLine(line, bindings);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _bindings = bindings;
        // Store only template/question IDs. No request, account, or authentication data is retained.
        try { File.WriteAllText(cache, JsonSerializer.Serialize(bindings)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static void ReadLogLine(string line, Dictionary<string, IReadOnlyList<TemplateBinding>> result)
    {
        for (var start = 0; start < line.Length; start++)
        {
            if (line[start] is not ('{' or '[')) continue;
            var end = FindJsonEnd(line, start);
            if (end < 0) continue;
            try
            {
                using var document = JsonDocument.Parse(line.AsMemory(start, end - start + 1));
                Visit(document.RootElement, result);
                start = end;
            }
            catch (JsonException) { }
        }
    }

    private static int FindJsonEnd(string text, int start)
    {
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var ch = text[index];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') quoted = false;
                continue;
            }
            if (ch == '"') quoted = true;
            else if (ch is '{' or '[') depth++;
            else if (ch is '}' or ']')
            {
                if (--depth == 0) return index;
            }
        }
        return -1;
    }

    private static void Visit(JsonElement value, Dictionary<string, IReadOnlyList<TemplateBinding>> result, int depth = 0)
    {
        if (depth > 64) return;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var setId = Str(value, "set_id");
            var blocks = Get(value, "structs");
            if (blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() == 0)
            {
                var block = Get(value, "struct");
                if (block.ValueKind == JsonValueKind.Object) blocks = default;
                if (setId.Length > 0 && block.ValueKind == JsonValueKind.Object)
                    Save(setId, [block], result);
            }
            if (setId.Length > 0 && blocks.ValueKind == JsonValueKind.Array)
                Save(setId, blocks.EnumerateArray(), result);
            foreach (var property in value.EnumerateObject()) Visit(property.Value, result, depth + 1);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Visit(item, result, depth + 1);
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
                 (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                Visit(document.RootElement, result, depth + 1);
            }
            catch (JsonException) { }
        }
    }

    private static void Save(string setId, IEnumerable<JsonElement> blocks,
        Dictionary<string, IReadOnlyList<TemplateBinding>> result)
    {
        var templates = new List<TemplateBinding>();
        foreach (var block in blocks)
        {
            var contents = Array(block, "contents")
                .Select(content => new ContentBinding(Str(content, "real_id"), Str(content, "code_id")))
                .Where(content => content.RealId.Length > 0).ToArray();
            if (contents.Length == 0) continue;
            templates.Add(new TemplateBinding(Str(Get(block, "template"), "id"), contents));
        }
        if (templates.Count > 0) result[setId] = templates;
    }

    public static IReadOnlyDictionary<string, ContentPosition> GetContentOrder(string assignmentDir)
    {
        var positions = new Dictionary<string, ContentPosition>(StringComparer.OrdinalIgnoreCase);
        if (!_bindings.TryGetValue(Path.GetFileName(assignmentDir.TrimEnd(Path.DirectorySeparatorChar)), out var blocks) || blocks is null)
            return positions;
        foreach (var block in blocks)
        {
            if (block is null || block.Contents is null) continue;
            var file = Path.Combine(assignmentDir, "template_" + block.TemplateId, "res.json");
            try
            {
                if (File.Exists(file))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (var section in Array(document.RootElement, "exam_type_list").OrderBy(item => Number(item, "exam_type_order")))
                    foreach (var exam in Array(section, "exam_list").OrderBy(item => Number(item, "exam_order")))
                    {
                        var id = Str(exam, "exam_id").Trim('{', '}');
                        var content = block.Contents.FirstOrDefault(item => item.CodeId == id || item.RealId == id);
                        if (content is not null)
                            positions.TryAdd("content_" + content.RealId, new ContentPosition(positions.Count, Str(section, "exam_type_name"),
                                Array(exam, "question_list").OrderBy(item => Number(item, "question_order"))
                                    .Select(item => Number(item, "question_order")).ToArray()));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            foreach (var content in block.Contents)
                positions.TryAdd("content_" + content.RealId, new ContentPosition(positions.Count, "", []));
        }
        return positions;
    }

    private static JsonElement Get(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
    private static string Str(JsonElement value, string key)
    {
        var field = Get(value, key);
        return field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" :
            field.ValueKind == JsonValueKind.Number ? field.GetRawText() : "";
    }
    private static IEnumerable<JsonElement> Array(JsonElement value, string key) =>
        Get(value, key) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToArray() : [];
    private static int Number(JsonElement value, string key) => int.TryParse(Str(value, key), out var number) ? number : int.MaxValue;
}
