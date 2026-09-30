namespace EtsVoiceBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
            return SelfTest.Run();
        if (args.Length == 2 && args[0] == "--order-probe")
        {
            try
            {
                AssignmentOrderReader.Refresh();
                var items = AnswerExtractor.LoadAssignment(args[1]);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(items.Select(item => new
                {
                    item.Source, item.Section, item.Label, item.AssignmentOrder,
                    item.LocalQuestionOrder, item.OfficialQuestionNumber, item.OrderBasis
                })));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Contains("--gpus"))
        {
            try
            {
                foreach (var gpu in DxgiAdapters.Enumerate())
                    Console.WriteLine($"{gpu.Index}: {gpu.Name} (vendor 0x{gpu.VendorId:X4})");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Contains("--devices"))
        {
            try
            {
                foreach (var device in WaveOutput.Devices())
                    Console.WriteLine($"{device.Id}: {device.Name}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Length == 3 && args[0] == "--synthesize")
        {
            try
            {
                var projectDir = new DirectoryInfo(AppContext.BaseDirectory);
                while (projectDir is not null && !File.Exists(Path.Combine(projectDir.FullName, "EtsVoiceBridge.csproj")))
                    projectDir = projectDir.Parent;
                if (projectDir is null) throw new DirectoryNotFoundException("Project folder not found.");
                var files = projectDir.FullName;
                var amd = DxgiAdapters.Enumerate().FirstOrDefault(gpu => gpu.VendorId == 0x1002)
                    ?? throw new InvalidOperationException("AMD GPU not found.");
                var wav = LocalSpeechClient.GenerateWavAsync(args[1],
                    Path.Combine(files, ".venv", "Scripts", "python.exe"),
                    Path.Combine(files, "models", "en_US-lessac-medium.onnx"),
                    Path.Combine(files, "models", "en_US-lessac-medium.onnx.json"),
                    "en_US-lessac-medium", (int)amd.Index, CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllBytes(args[2], wav);
                Console.WriteLine($"Generated {wav.Length} bytes on {amd.Name}.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Length == 2 && args[0] == "--probe")
        {
            try
            {
                var assignments = Directory.EnumerateDirectories(args[1])
                    .Where(path => Directory.EnumerateDirectories(path, "content*")
                        .Any(content => File.Exists(Path.Combine(content, "content.json")))).ToArray();
                var itemCount = 0;
                foreach (var assignment in assignments)
                    itemCount += AnswerExtractor.LoadAssignment(assignment).Count;
                Console.WriteLine($"Parsed {assignments.Length} assignments and {itemCount} speech items.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Probe failed: {ex.Message}");
                return 1;
            }
        }
        if (args.Length == 2 && args[0] == "--batch-test")
        {
            try
            {
                var projectDir = new DirectoryInfo(AppContext.BaseDirectory);
                while (projectDir is not null && !File.Exists(Path.Combine(projectDir.FullName, "EtsVoiceBridge.csproj")))
                    projectDir = projectDir.Parent;
                if (projectDir is null) throw new DirectoryNotFoundException("Project folder not found.");
                var amd = DxgiAdapters.Enumerate().FirstOrDefault(gpu => gpu.VendorId == 0x1002)
                    ?? throw new InvalidOperationException("AMD GPU not found.");
                var answers = new[] { "Hello.", "It’s time to speak." };
                var wavs = LocalSpeechClient.GenerateAssignmentWavsAsync(answers,
                    Path.Combine(projectDir.FullName, ".venv", "Scripts", "python.exe"),
                    Path.Combine(projectDir.FullName, "models", "en_US-lessac-medium.onnx"),
                    Path.Combine(projectDir.FullName, "models", "en_US-lessac-medium.onnx.json"),
                    "en_US-lessac-medium", (int)amd.Index, null, CancellationToken.None).GetAwaiter().GetResult();
                if (wavs.Count != answers.Length) throw new InvalidDataException("答案与音频数量不匹配。");
                Directory.CreateDirectory(args[1]);
                for (var index = 0; index < wavs.Count; index++)
                {
                    WaveOutput.ParseWav(wavs[index]);
                    File.WriteAllBytes(Path.Combine(args[1], $"{index + 1:000}.wav"), wavs[index]);
                }
                Console.WriteLine($"Generated {wavs.Count} separate in-memory WAVs on {amd.Name}.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
