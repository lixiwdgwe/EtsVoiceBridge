using System.Text;
using System.Text.Json;

namespace EtsVoiceBridge;

internal static class SelfTest
{
    public static int Run()
    {
        try
        {
            Check("""{"structure_type":"collector.role","info":{"question":[{"ask":"<p>Q?</p>","std":[{"value":"<b>Hello</b> &amp; bye."},{"value":"Ignore this variant."}]}]}}""",
                "Hello & bye.");
            Check("""{"structure_type":"collector.picture","info":{"std":[{"value":"<p>A cat.</p>"},{"value":"Ignore this variant."}]}}""", "A cat.");
            Check("""{"structure_type":"collector.choose","info":{"xtlist":[{"answer":"B","xxlist":[{"xx_mc":"A","xx_nr":"No"},{"xx_mc":"B","xx_nr":"Yes"}]}]}}""", "Yes");
            CheckEmpty("""{"structure_type":"collector.repeat_essay","info":{"sublist":[{"text":"First."}]}}""");
            CheckEmpty("""{"structure_type":"collector.read","info":{"value":"Read me."}}""");
            CheckEmpty("""{"structure_type":"collector.word","info":{"value":"Word practice."}}""");
            CheckQuestionOrder();
            var wave = MakeWav();
            var (format, audio) = WaveOutput.ParseWav(wave);
            if (format.SamplesPerSec != 24000 || audio.Length != 4)
                throw new Exception("WAV 解析失败。");
            Console.WriteLine("Self-test passed: answer extraction, official question order, and WAV parsing.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckQuestionOrder()
    {
        var bindings = new Dictionary<string, IReadOnlyList<TemplateBinding>>();
        AssignmentOrderReader.ReadLogLine("""[INFO] [{"body":{"data":[{"set_id":"sample","structs":[{"template":{"id":"1002"},"contents":[{"real_id":"900","code_id":"st1"},{"real_id":"100","code_id":"st2"}]}]}]}}]""", bindings);
        if (!bindings.TryGetValue("sample", out var blocks) || blocks[0].Contents[0].RealId != "900")
            throw new Exception("作业结构的题目绑定读取失败。");

        var temp = Path.Combine(Path.GetTempPath(), "EtsVoiceBridge-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Content(string source, string original, string resolved)
            {
                var folder = Path.Combine(temp, source);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "content.json"), original);
                File.WriteAllText(Path.Combine(folder, "content2.json"), resolved);
            }
            var role = """{"structure_type":"collector.role","info":{"value":"passage","question":[{"xh":"1","askaudio":"question.mp3","ask":"ets_th1 Test?","std":[{"value":"First answer."},{"value":"Ignore."}]}]}}""";
            Content("content_100", role, role.Replace("ets_th1", "7."));
            Content("content_900", role, role.Replace("ets_th1", "1."));
            var picture = """{"structure_type":"collector.picture","info":{"std":[{"value":"Retelling."}]}}""";
            Content("content_950", picture, picture);
            var inquiry = """{"structure_type":"collector.role","info":{"value":"","question":[{"xh":"1","ask":"请你准备第一个问题。","std":[{"value":"Will it rain?"}]}]}}""";
            Content("content_50", inquiry, inquiry);
            var actual = AnswerExtractor.LoadAssignment(temp);
            if (!actual.Select(item => item.Source).SequenceEqual(new[] { "content_900", "content_100", "content_950", "content_50" }) ||
                actual[0].OfficialQuestionNumber != 1 || actual[1].OfficialQuestionNumber != 7 || actual[0].Text != "First answer.")
                throw new Exception("正式题号应优先于文件编号，转述和询问应保留章节顺序。");
        }
        finally
        {
            var full = Path.GetFullPath(temp);
            if (string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("EtsVoiceBridge-order-", StringComparison.Ordinal)
                && Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static void Check(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        var actual = AnswerExtractor.Extract(document.RootElement, "assignment", "content1");
        if (actual.Count != 1 || actual[0].Text != expected)
            throw new Exception($"提取失败：预期 '{expected}'，实际 '{string.Join("|", actual.Select(x => x.Text))}'。");
    }

    private static void CheckEmpty(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (AnswerExtractor.Extract(document.RootElement, "assignment", "content1").Count != 0)
            throw new Exception("非答案材料不应进入语音列表。");
    }

    private static byte[] MakeWav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)40);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write((uint)16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)24000);
        writer.Write((uint)48000);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)4);
        writer.Write(new byte[4]);
        return stream.ToArray();
    }
}
