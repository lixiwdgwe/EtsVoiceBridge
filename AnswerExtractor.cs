using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EtsVoiceBridge;

internal sealed record SpeechItem(string Assignment, string Source, string Label, string Text)
{
    public DateTime ModifiedAt { get; init; }
    public int OriginalOrder { get; init; }
    public int AssignmentOrder { get; init; }
    public int LocalQuestionOrder { get; init; }
    public int? OfficialQuestionNumber { get; init; }
    public int FallbackStage { get; init; }
    public string Section { get; init; } = "";
    public string OrderBasis { get; init; } = "";
    public override string ToString() => $"{Source} · {(Section.Length > 0 ? Section + " · " : "")}{Label}";
}

internal static partial class AnswerExtractor
{
    public static IReadOnlyList<SpeechItem> LoadAssignment(string assignmentDir)
    {
        var result = new List<SpeechItem>();
        var positions = AssignmentOrderReader.GetContentOrder(assignmentDir);
        foreach (var contentDir in Directory.EnumerateDirectories(assignmentDir)
                     .Where(path => Path.GetFileName(path).StartsWith("content", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(contentDir, "content.json");
            if (!File.Exists(file)) continue;
            var resolved = Path.Combine(contentDir, "content2.json");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            using var resolvedDocument = ReadResolved(resolved);
            var modifiedAt = File.GetLastWriteTime(file);
            var source = Path.GetFileName(contentDir);
            positions.TryGetValue(source, out var position);
            result.AddRange(Extract(document.RootElement, Path.GetFileName(assignmentDir), source, resolvedDocument?.RootElement ?? default)
                .Select(item => item with
                {
                    ModifiedAt = modifiedAt,
                    AssignmentOrder = position?.Position ?? int.MaxValue,
                    LocalQuestionOrder = position is not null && position.Questions.Contains(item.LocalQuestionOrder)
                        ? position.Questions.ToList().IndexOf(item.LocalQuestionOrder) + 1 : item.LocalQuestionOrder,
                    Section = position?.Section ?? "",
                    OrderBasis = position is not null ? "客户端作业结构 + 模板" :
                        resolvedDocument is not null && item.OfficialQuestionNumber is not null ? "content2 正式题号" : "文件编号（缺少正式题序）"
                }));
        }
        result = result.Select((item, index) => item with { OriginalOrder = index }).ToList();
        if (result.Any(item => item.OfficialQuestionNumber is not null))
            result = result.Select(item => item.AssignmentOrder == int.MaxValue
                ? item with { OrderBasis = "content2 正式题号（章节推断）" } : item).ToList();
        // Resolved content contains the displayed question numbers, including IDs that were reused later.
        var fallback = result.Where(item => item.AssignmentOrder == int.MaxValue).GroupBy(item => item.Source)
            .OrderBy(group => group.Min(item => item.FallbackStage))
            .ThenBy(group => group.Min(item => item.OfficialQuestionNumber ?? int.MaxValue))
            .ThenBy(group => FileNumber(group.Key)).Select((group, index) => (group.Key, Position: positions.Count + index))
            .ToDictionary(group => group.Key, group => group.Position);
        return result.Select(item => item.AssignmentOrder == int.MaxValue
                ? item with { AssignmentOrder = fallback[item.Source] } : item)
            .OrderBy(item => item.AssignmentOrder).ThenBy(item => item.LocalQuestionOrder).ToArray();
    }

    private static JsonDocument? ReadResolved(string resolved)
    {
        if (File.Exists(resolved))
        {
            try { return JsonDocument.Parse(File.ReadAllText(resolved)); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static IReadOnlyList<SpeechItem> Extract(JsonElement root, string assignment, string source, JsonElement resolvedRoot = default)
    {
        var result = new List<SpeechItem>();
        var type = Str(root, "structure_type");
        var info = Get(root, "info");
        var resolvedInfo = Str(resolvedRoot, "structure_type") == type ? Get(resolvedRoot, "info") : default;
        void Add(string label, string? text, int localOrder = 1, int? officialNumber = null, int stage = 0)
        {
            var clean = Clean(text);
            if (clean.Length > 0) result.Add(new SpeechItem(assignment, source, label, clean)
            {
                LocalQuestionOrder = localOrder, OfficialQuestionNumber = officialNumber, FallbackStage = stage
            });
        }

        switch (type)
        {
            case "collector.role":
                var questionNumber = 0;
                var roleQuestions = Array(info, "question").ToArray();
                var inquiry = Str(info, "value").Length == 0 && roleQuestions.Length > 0 &&
                    roleQuestions.All(question => Str(question, "askaudio").Length == 0);
                foreach (var question in roleQuestions)
                {
                    questionNumber++;
                    var localOrder = Int(question, "xh", questionNumber);
                    var resolvedQuestion = MatchingQuestion(resolvedInfo, "question", "xh", localOrder);
                    var resolvedPrompt = Str(resolvedQuestion, "ask");
                    var prompt = Clean(resolvedPrompt.Length > 0 ? resolvedPrompt : Str(question, "ask"));
                    var officialNumber = PromptNumber(prompt);
                    prompt = Regex.Replace(prompt, @"ets_th\d+\s*", "").Trim();
                    if (officialNumber is not null) prompt = Regex.Replace(prompt, @"^\s*\d+\s*[.．、)]\s*", "");
                    var answer = Array(question, "std").FirstOrDefault();
                    Add($"题目 {officialNumber ?? localOrder} · 参考答案: {prompt}", Str(answer, "value"),
                        localOrder, officialNumber, inquiry ? 2 : 0);
                }
                break;

            case "collector.picture":
                var pictureAnswer = Array(info, "std").FirstOrDefault();
                Add("图片描述 · 参考答案", Str(pictureAnswer, "value"), stage: 1);
                break;

            case "collector.choose":
                var choiceNumber = 0;
                foreach (var question in Array(info, "xtlist"))
                {
                    choiceNumber++;
                    var letters = Str(question, "answer").Trim();
                    if (letters.Length == 0) continue;
                    var optionTexts = Array(question, "xxlist")
                        .Where(option => Str(option, "xx_mc").Trim() is { Length: > 0 } name &&
                                         letters.Contains(name, StringComparison.OrdinalIgnoreCase))
                        .Select(option => Clean(Str(option, "xx_nr")))
                        .Where(text => text.Length > 0);
                    var localOrder = Int(question, "xt_xh", choiceNumber);
                    var resolvedQuestion = MatchingQuestion(resolvedInfo, "xtlist", "xt_xh", localOrder);
                    var resolvedPrompt = Str(resolvedQuestion, "xt_nr");
                    var officialNumber = PromptNumber(Clean(resolvedPrompt.Length > 0 ? resolvedPrompt : Str(question, "xt_nr")));
                    Add($"选择题 {officialNumber ?? localOrder} · {letters}", string.Join("; ", optionTexts), localOrder, officialNumber);
                }
                break;

        }
        return result;
    }

    private static JsonElement MatchingQuestion(JsonElement info, string list, string numberField, int order) =>
        Array(info, list).Select((question, index) => (Question: question, Order: Int(question, numberField, index + 1)))
            .FirstOrDefault(item => item.Order == order).Question;

    private static int? PromptNumber(string prompt)
    {
        var match = Regex.Match(prompt, @"(?:^|\n)\s*(\d+)\s*[.．、)]");
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : null;
    }

    private static int Int(JsonElement value, string key, int fallback)
    {
        var field = Get(value, key);
        return field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var numeric) ? numeric :
            int.TryParse(Str(value, key), out var number) ? number : fallback;
    }

    private static long FileNumber(string source) =>
        long.TryParse(Regex.Match(source, @"\d+").Value, out var number) ? number : long.MaxValue;

    private static JsonElement Get(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value : default;

    private static string Str(JsonElement element, string key)
    {
        var value = Get(element, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string key)
    {
        var value = Get(element, key);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    }

    private static string Clean(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var text = Regex.Replace(input, @"<!--.*?-->", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*(p|div|li)\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]*>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\s*\n\s*", "\n");
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }
}
