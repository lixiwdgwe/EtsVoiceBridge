using System.Diagnostics;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace EtsVoiceBridge;

internal static class LocalSpeechClient
{
    public static async Task<IReadOnlyList<byte[]>> GenerateAssignmentWavsAsync(
        IReadOnlyList<string> answers,
        string pythonPath, string modelPath, string voicesPath, string voice, int gpuIndex,
        IProgress<int>? progress, CancellationToken token)
    {
        if (answers.Count == 0) throw new ArgumentException("当前作业没有参考答案。");
        foreach (var (path, label) in new[]
                 { (pythonPath, "Python"), (modelPath, "Piper 模型"), (voicesPath, "语音配置") })
            if (!File.Exists(path)) throw new FileNotFoundException($"找不到{label}：{path}");
        var worker = Path.Combine(AppContext.BaseDirectory, "local_tts.py");
        if (!File.Exists(worker)) throw new FileNotFoundException($"找不到本地推理程序：{worker}");

        var start = new ProcessStartInfo(pythonPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8:replace";
        foreach (var arg in new[]
                 { worker, "--model", modelPath, "--voices", voicesPath, "--voice", voice,
                   "--device", gpuIndex.ToString(), "--batch" })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动本地语音模型。");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var diagnostics = new StringBuilder();
        async Task ReadDiagnosticsAsync()
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(token)) is not null)
            {
                if (line.StartsWith("PROGRESS ", StringComparison.Ordinal) &&
                    int.TryParse(line.AsSpan(9), out var completed))
                    progress?.Report(completed);
                else diagnostics.AppendLine(line);
            }
        }
        var diagnosticsTask = ReadDiagnosticsAsync();
        using var output = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, token);
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(answers).AsMemory(), token);
        process.StandardInput.Close();
        await Task.WhenAll(diagnosticsTask, outputTask, process.WaitForExitAsync(token));
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"整份作业生成失败：{diagnostics.ToString().Trim()}");

        output.Position = 0;
        var wavs = new List<byte[]>(answers.Count);
        Span<byte> header = stackalloc byte[8];
        for (var index = 1; index <= answers.Count; index++)
        {
            if (output.Read(header) != header.Length)
                throw new InvalidDataException($"缺少第 {index} 条答案的音频长度。");
            var length = BinaryPrimitives.ReadUInt64LittleEndian(header);
            if (length > int.MaxValue || length > (ulong)(output.Length - output.Position))
                throw new InvalidDataException($"第 {index} 条答案的音频数据不完整。");
            var data = new byte[(int)length];
            output.ReadExactly(data);
            if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF")
                throw new InvalidDataException($"第 {index} 条答案不是有效 WAV。");
            wavs.Add(data);
        }
        if (output.Position != output.Length)
            throw new InvalidDataException("模型返回了多余的音频数据。");
        return wavs;
    }

    public static async Task<byte[]> GenerateWavAsync(
        string text, string pythonPath, string modelPath, string voicesPath,
        string voice, int gpuIndex, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("朗读文字不能为空。");
        foreach (var (path, label) in new[]
                 { (pythonPath, "Python"), (modelPath, "Piper 模型"), (voicesPath, "语音配置") })
            if (!File.Exists(path)) throw new FileNotFoundException($"找不到{label}：{path}");

        var worker = Path.Combine(AppContext.BaseDirectory, "local_tts.py");
        if (!File.Exists(worker)) throw new FileNotFoundException($"找不到本地推理程序：{worker}");
        var start = new ProcessStartInfo(pythonPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        start.Environment["PYTHONIOENCODING"] = "utf-8:replace";
        foreach (var arg in new[]
                 { worker, "--model", modelPath, "--voices", voicesPath, "--voice", voice,
                   "--device", gpuIndex.ToString() })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动本地语音模型。");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        using var output = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, token);
        await process.StandardInput.WriteAsync(text.AsMemory(), token);
        process.StandardInput.Close();
        await Task.WhenAll(outputTask, process.WaitForExitAsync(token));
        var diagnostic = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"本地语音生成失败：{diagnostic.Trim()}");
        var data = output.ToArray();
        if (data.Length < 12 || System.Text.Encoding.ASCII.GetString(data, 0, 4) != "RIFF")
            throw new InvalidDataException("本地模型没有输出 WAV 音频。" + diagnostic);
        return data;
    }
}
