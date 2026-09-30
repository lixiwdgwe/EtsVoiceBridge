using System.Text.RegularExpressions;

namespace EtsVoiceBridge;

internal sealed class MainForm : Form
{
    private readonly TextBox _root = new() { Dock = DockStyle.Fill };
    private readonly ListBox _assignments = new() { Dock = DockStyle.Fill };
    private readonly ListBox _items = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _assignmentSort = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _answerSort = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _text = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _python = new() { Dock = DockStyle.Fill };
    private readonly TextBox _model = new() { Dock = DockStyle.Fill };
    private readonly TextBox _voices = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _gpuIndex = new() { Minimum = 0, Maximum = 16, Width = 48 };
    private readonly ComboBox _voice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox _device = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 285 };
    private readonly Label _status = new() { AutoSize = true, Text = "请选择作业和参考答案。" };
    private readonly Button _generate = new() { Text = "生成并输出", AutoSize = true };
    private readonly Button _generateAssignment = new() { Text = "生成整份作业音频", AutoSize = true, Enabled = false };
    private readonly Button _play = new() { Text = "重播", AutoSize = true, Enabled = false };
    private readonly Button _stop = new() { Text = "停止", AutoSize = true };
    private readonly Button _clear = new() { Text = "清除音频", AutoSize = true, Enabled = false };
    private readonly Button _save = new() { Text = "保存 WAV", AutoSize = true, Enabled = false };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Visible = false };
    private byte[]? _wav;
    private IReadOnlyList<byte[]>? _assignmentWavs;
    private IReadOnlyList<string>? _cachedAnswers;
    private string? _cachedAssignmentPath;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource<bool>? _operationDone;
    private int _audioVersion;
    private int _selectionRequest;
    private int _mouseDownSelectionIndex = -1;
    private bool _settingText;
    private bool _reorderingAssignments;
    private bool _reorderingAnswers;

    public MainForm()
    {
        Text = "ETS 参考答案语音输出";
        MinimumSize = new Size(900, 650);
        Size = new Size(1100, 740);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9);
        _root.Text = FindDefaultRoot() ?? "";
        var projectDir = FindProjectDirectory();
        _python.Text = Path.Combine(projectDir, ".venv", "Scripts", "python.exe");
        _model.Text = Path.Combine(projectDir, "models", "en_US-lessac-medium.onnx");
        _voices.Text = Path.Combine(projectDir, "models", "en_US-lessac-medium.onnx.json");
        _voice.Items.Add("en_US-lessac-medium");
        _voice.SelectedIndex = 0;
        _assignmentSort.Items.AddRange(["时间：最新在前", "时间：最旧在前", "数字：从小到大", "数字：从大到小"]);
        _answerSort.Items.AddRange(["作业题序（自动识别）", "时间：最新在前", "时间：最旧在前", "文件编号：从小到大", "文件编号：从大到小"]);
        _assignmentSort.SelectedIndex = 0;
        _answerSort.SelectedIndex = 0;
        try
        {
            var amd = DxgiAdapters.Enumerate().FirstOrDefault(gpu => gpu.VendorId == 0x1002);
            if (amd is not null) _gpuIndex.Value = amd.Index;
        }
        catch { /* The user can enter the DirectML adapter index manually. */ }

        var rootRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Height = 35 };
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rootRow.Controls.Add(new Label { Text = "作业根目录", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        rootRow.Controls.Add(_root, 1, 0);
        var browse = new Button { Text = "选择并扫描", AutoSize = true };
        rootRow.Controls.Add(browse, 2, 0);
        browse.Click += (_, _) => BrowseRoot();

        var scan = new Button { Text = "扫描目录", AutoSize = true };
        scan.Click += (_, _) => ScanRoot();
        var refreshDevices = new Button { Text = "刷新设备", AutoSize = true };
        refreshDevices.Click += (_, _) => RefreshDevices();

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        controls.Controls.AddRange([
            scan, new Label { Text = "语音", AutoSize = true, Padding = new Padding(8, 7, 0, 0) }, _voice,
            new Label { Text = "GPU 编号", AutoSize = true, Padding = new Padding(8, 7, 0, 0) }, _gpuIndex,
            new Label { Text = "播放到", AutoSize = true, Padding = new Padding(8, 7, 0, 0) }, _device,
            refreshDevices
        ]);

        var modelRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        modelRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        modelRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modelRows.Controls.Add(new Label { Text = "本地 Python", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        modelRows.Controls.Add(_python, 1, 0);
        modelRows.Controls.Add(new Label { Text = "Piper ONNX", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        modelRows.Controls.Add(_model, 1, 1);
        modelRows.Controls.Add(new Label { Text = "语音配置", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        modelRows.Controls.Add(_voices, 1, 2);

        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        lists.Controls.Add(GroupWithSort("作业（目录更改时间）", _assignments, _assignmentSort), 0, 0);
        lists.Controls.Add(GroupWithSort("参考答案（题号与章节）", _items, _answerSort), 1, 0);
        lists.Controls.Add(Group("交给本地模型的文字（可修改）", _text), 2, 0);

        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actionRow.Controls.AddRange([_generate, _generateAssignment, _play, _stop, _clear, _save]);
        _generate.Click += async (_, _) => await GenerateAndPlayAsync();
        _generateAssignment.Click += async (_, _) => await GenerateAssignmentAsync();
        _play.Click += async (_, _) => await PlayAsync();
        _stop.Click += (_, _) => _operation?.Cancel();
        _clear.Click += (_, _) => ClearGeneratedAudio();
        _save.Click += (_, _) => SaveWav();
        _assignments.SelectedIndexChanged += (_, _) =>
        {
            if (!_reorderingAssignments) LoadSelectedAssignment();
        };
        _assignmentSort.SelectedIndexChanged += (_, _) => SortAssignments();
        _answerSort.SelectedIndexChanged += (_, _) => SortAnswers();
        _items.SelectedIndexChanged += async (_, _) =>
        {
            if (_reorderingAnswers) return;
            if (_items.SelectedItem is not SpeechItem item) return;
            var index = _items.SelectedIndex;
            SetAnswerText(_cachedAnswers is not null && index < _cachedAnswers.Count
                ? _cachedAnswers[index] : item.Text);
            if (_assignmentWavs is not null) await PlayCachedSelectionAsync();
        };
        _items.MouseDown += (_, _) => _mouseDownSelectionIndex = _items.SelectedIndex;
        _items.MouseClick += async (_, args) =>
        {
            var index = _items.IndexFromPoint(args.Location);
            if (_assignmentWavs is not null && index >= 0 && index == _items.SelectedIndex &&
                index == _mouseDownSelectionIndex)
                await PlayCachedSelectionAsync();
        };
        _text.TextChanged += (_, _) =>
        {
            if (_settingText || _assignmentWavs is null) return;
            InvalidateAudio();
            _status.Text = "答案已修改，请重新生成整份作业音频。";
        };

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4 };
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.Controls.Add(actionRow, 0, 0);
        bottom.Controls.Add(_status, 0, 1);
        bottom.Controls.Add(_progress, 0, 2);
        bottom.Controls.Add(new Label
        {
            Text = "本地 AI 生成语音，不上传文字。虚拟麦克风：播放到 CABLE Input，录音软件选择 CABLE Output。",
            AutoSize = true, ForeColor = Color.DimGray
        }, 0, 3);

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, Padding = new Padding(10) };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        main.Controls.Add(rootRow, 0, 0);
        main.Controls.Add(controls, 0, 1);
        main.Controls.Add(modelRows, 0, 2);
        main.Controls.Add(lists, 0, 3);
        main.Controls.Add(bottom, 0, 4);
        Controls.Add(main);

        RefreshDevices();
        if (_root.Text.Length > 0) ScanRoot(interactive: false);
    }

    private static GroupBox Group(string title, Control content)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(7) };
        box.Controls.Add(content);
        return box;
    }

    private static GroupBox GroupWithSort(string title, Control content, ComboBox sort)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(sort, 0, 0);
        layout.Controls.Add(content, 0, 1);
        return Group(title, layout);
    }

    private static string? FindDefaultRoot()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var ets = Path.Combine(roaming, "ETS");
        if (Directory.Exists(ets)) return ets;
        try { return Directory.EnumerateDirectories(roaming)
            .FirstOrDefault(path => Regex.IsMatch(Path.GetFileName(path), @"^[0-9A-F]{20,}$")); }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string FindProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "EtsVoiceBridge.csproj"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }

    private void BrowseRoot()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择 ETS 作业根目录" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _root.Text = dialog.SelectedPath;
        ScanRoot();
    }

    private void ScanRoot(bool interactive = true)
    {
        try
        {
            InvalidateAudio();
            _reorderingAssignments = true;
            try { _assignments.Items.Clear(); }
            finally { _reorderingAssignments = false; }
            _items.Items.Clear();
            _generateAssignment.Enabled = false;
            var root = _root.Text.Trim();
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("目录不存在。");
            AssignmentOrderReader.Refresh();
            var assignments = Directory.EnumerateDirectories(root)
                .Where(HasContent)
                .Select(path => new Assignment(path, Directory.GetLastWriteTime(path))).ToArray();
            if (HasContent(root))
                assignments = [new Assignment(root, Directory.GetLastWriteTime(root)), .. assignments];
            foreach (var assignment in assignments) _assignments.Items.Add(assignment);
            SortAssignments();
            _status.Text = $"找到 {assignments.Length} 个含 content.json 的作业目录。";
            if (_assignments.Items.Count > 0) _assignments.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            if (interactive) ShowError(ex);
            else _status.Text = ex.Message;
        }
    }

    private static bool HasContent(string dir) => Directory.EnumerateDirectories(dir, "content*")
        .Any(content => File.Exists(Path.Combine(content, "content.json")));

    private void LoadSelectedAssignment()
    {
        InvalidateAudio();
        _items.Items.Clear();
        SetAnswerText("");
        _generateAssignment.Enabled = false;
        if (_assignments.SelectedItem is not Assignment assignment) return;
        try
        {
            foreach (var item in AnswerExtractor.LoadAssignment(assignment.Path)) _items.Items.Add(item);
            SortAnswers();
            _generateAssignment.Enabled = _items.Items.Count > 0;
            var basis = string.Join("；", _items.Items.OfType<SpeechItem>().Select(item => item.OrderBasis).Distinct());
            _status.Text = $"{assignment}: {_items.Items.Count} 条参考答案。题序依据：{basis}";
            if (_items.Items.Count > 0) _items.SelectedIndex = 0;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SortAssignments()
    {
        if (_assignments.Items.Count == 0) return;
        var selected = _assignments.SelectedItem as Assignment;
        var assignments = _assignments.Items.OfType<Assignment>().ToArray();
        IEnumerable<Assignment> sorted = _assignmentSort.SelectedIndex switch
        {
            1 => assignments.OrderBy(item => item.ModifiedAt).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            2 => assignments.OrderBy(item => NumericKey(Path.GetFileName(item.Path)))
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            3 => assignments.OrderByDescending(item => NumericKey(Path.GetFileName(item.Path)))
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            _ => assignments.OrderByDescending(item => item.ModifiedAt)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
        };
        _reorderingAssignments = true;
        try
        {
            _assignments.Items.Clear();
            foreach (var item in sorted) _assignments.Items.Add(item);
            if (selected is not null) _assignments.SelectedItem = selected;
        }
        finally { _reorderingAssignments = false; }
    }

    private void SortAnswers()
    {
        if (_items.Items.Count == 0) return;
        var selected = _items.SelectedItem as SpeechItem;
        var rows = _items.Items.OfType<SpeechItem>()
            .Select((item, index) => new AnswerRow(item,
                _assignmentWavs is not null ? _assignmentWavs[index] : null,
                _cachedAnswers is not null ? _cachedAnswers[index] : null)).ToArray();
        IEnumerable<AnswerRow> sorted = _answerSort.SelectedIndex switch
        {
            1 => rows.OrderByDescending(row => row.Item.ModifiedAt).ThenBy(row => row.Item.OriginalOrder),
            2 => rows.OrderBy(row => row.Item.ModifiedAt).ThenBy(row => row.Item.OriginalOrder),
            3 => rows.OrderBy(row => NumericKey(row.Item.Source)).ThenBy(row => row.Item.OriginalOrder),
            4 => rows.OrderByDescending(row => NumericKey(row.Item.Source)).ThenBy(row => row.Item.OriginalOrder),
            _ => rows.OrderBy(row => row.Item.AssignmentOrder).ThenBy(row => row.Item.LocalQuestionOrder)
        };
        var ordered = sorted.ToArray();
        _selectionRequest++;
        _reorderingAnswers = true;
        try
        {
            _items.Items.Clear();
            foreach (var row in ordered) _items.Items.Add(row.Item);
            if (selected is not null) _items.SelectedItem = selected;
            if (_assignmentWavs is not null) _assignmentWavs = ordered.Select(row => row.Wav!).ToArray();
            if (_cachedAnswers is not null) _cachedAnswers = ordered.Select(row => row.CachedText!).ToArray();
        }
        finally { _reorderingAnswers = false; }
    }

    private static long NumericKey(string name)
    {
        var match = Regex.Match(name, @"\d+");
        return match.Success && long.TryParse(match.Value, out var number) ? number : long.MaxValue;
    }

    private void RefreshDevices()
    {
        try
        {
            _device.Items.Clear();
            foreach (var device in WaveOutput.Devices()) _device.Items.Add(device);
            var cable = _device.Items.OfType<AudioDevice>().FirstOrDefault(d =>
                d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
            _device.SelectedItem = cable ?? _device.Items.OfType<AudioDevice>().FirstOrDefault();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task GenerateAndPlayAsync()
    {
        if (_operation is not null) return;
        var audioVersion = _audioVersion;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        var done = _operationDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _generate.Enabled = false;
        _generateAssignment.Enabled = false;
        _clear.Enabled = true;
        _progress.Visible = false;
        try
        {
            _status.Text = "正在生成语音…";
            var wav = await LocalSpeechClient.GenerateWavAsync(_text.Text.Trim(), _python.Text.Trim(),
                _model.Text.Trim(), _voices.Text.Trim(), _voice.SelectedItem?.ToString() ?? "en_US-lessac-medium",
                (int)_gpuIndex.Value, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _wav = wav;
            _play.Enabled = _save.Enabled = true;
            await PlayCoreAsync(operation.Token);
        }
        catch (OperationCanceledException)
        {
            if (audioVersion == _audioVersion) _status.Text = "已停止。";
        }
        catch (Exception ex)
        {
            if (audioVersion == _audioVersion) ShowError(ex);
        }
        finally
        {
            _generate.Enabled = true;
            _generateAssignment.Enabled = _items.Items.Count > 0;
            _clear.Enabled = _wav is not null || _assignmentWavs is not null;
            _operation = null;
            _operationDone = null;
            done.TrySetResult(true);
        }
    }

    private async Task GenerateAssignmentAsync()
    {
        if (_operation is not null || _assignments.SelectedItem is not Assignment assignment) return;
        var selected = _items.SelectedItem;
        var answers = _items.Items.OfType<SpeechItem>()
            .Select(item => ReferenceEquals(item, selected) ? item with { Text = _text.Text.Trim() } : item)
            .ToArray();
        if (answers.Length == 0 || answers.Any(item => string.IsNullOrWhiteSpace(item.Text)))
        {
            ShowError(new InvalidOperationException("当前作业有空白答案，请先填写后再生成。"));
            return;
        }

        var audioVersion = _audioVersion;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        var done = _operationDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _assignmentWavs = null;
        _cachedAnswers = null;
        _cachedAssignmentPath = null;
        _wav = null;
        _play.Enabled = _save.Enabled = _clear.Enabled = false;
        _generate.Enabled = _generateAssignment.Enabled = false;
        _assignments.Enabled = _items.Enabled = _text.Enabled = false;
        _assignmentSort.Enabled = _answerSort.Enabled = false;
        _progress.Maximum = answers.Length;
        _progress.Value = 0;
        _progress.Visible = true;
        var finished = false;
        var progress = new Progress<int>(completed =>
        {
            if (!finished && audioVersion == _audioVersion)
            {
                _progress.Value = Math.Clamp(completed, 0, _progress.Maximum);
                _status.Text = $"正在生成整份作业：{completed}/{answers.Length}";
            }
        });
        try
        {
            _status.Text = $"正在生成整份作业：0/{answers.Length}";
            var wavs = await LocalSpeechClient.GenerateAssignmentWavsAsync(answers.Select(item => item.Text).ToArray(),
                _python.Text.Trim(), _model.Text.Trim(), _voices.Text.Trim(),
                _voice.SelectedItem?.ToString() ?? "en_US-lessac-medium", (int)_gpuIndex.Value,
                progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            finished = true;
            _assignmentWavs = wavs;
            _cachedAnswers = answers.Select(item => item.Text).ToArray();
            _cachedAssignmentPath = assignment.Path;
            _items.SelectedIndex = -1;
            SetAnswerText("");
            _progress.Value = _progress.Maximum;
            _status.Text = $"已生成 {answers.Length} 条答案；选中答案即可播放。";
        }
        catch (OperationCanceledException)
        {
            finished = true;
            if (audioVersion == _audioVersion) _status.Text = "已停止生成。";
        }
        catch (Exception ex)
        {
            finished = true;
            if (audioVersion == _audioVersion) ShowError(ex);
        }
        finally
        {
            finished = true;
            _generate.Enabled = true;
            _generateAssignment.Enabled = _items.Items.Count > 0;
            _clear.Enabled = _wav is not null || _assignmentWavs is not null;
            _assignments.Enabled = _items.Enabled = _text.Enabled = true;
            _assignmentSort.Enabled = _answerSort.Enabled = true;
            _operation = null;
            _operationDone = null;
            done.TrySetResult(true);
        }
    }

    private async Task PlayCachedSelectionAsync()
    {
        var request = ++_selectionRequest;
        var index = _items.SelectedIndex;
        var assignmentPath = (_assignments.SelectedItem as Assignment)?.Path;
        var pending = _operationDone?.Task;
        if (pending is not null)
        {
            _audioVersion++;
            _operation?.Cancel();
            await pending;
        }
        if (request != _selectionRequest || index != _items.SelectedIndex ||
            assignmentPath != _cachedAssignmentPath || _assignmentWavs is null ||
            index < 0 || index >= _assignmentWavs.Count) return;
        _wav = _assignmentWavs[index];
        _play.Enabled = _save.Enabled = _clear.Enabled = true;
        await PlayAsync();
    }

    private async Task PlayAsync()
    {
        if (_operation is not null || _wav is null) return;
        var audioVersion = _audioVersion;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        var done = _operationDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await PlayCoreAsync(operation.Token); }
        catch (OperationCanceledException)
        {
            if (audioVersion == _audioVersion) _status.Text = "已停止。";
        }
        catch (Exception ex)
        {
            if (audioVersion == _audioVersion) ShowError(ex);
        }
        finally
        {
            _operation = null;
            _operationDone = null;
            done.TrySetResult(true);
        }
    }

    private async Task PlayCoreAsync(CancellationToken token)
    {
        if (_wav is null || _device.SelectedItem is not AudioDevice device) return;
        _status.Text = $"正在输出到 {device.Name}…";
        await WaveOutput.PlayAsync(_wav, device.Id, token);
        token.ThrowIfCancellationRequested();
        _status.Text = "输出完成。";
    }

    private void ClearGeneratedAudio()
    {
        InvalidateAudio();
        _status.Text = "已清除生成音频。";
    }

    private void InvalidateAudio()
    {
        _audioVersion++;
        _selectionRequest++;
        _operation?.Cancel();
        _assignmentWavs = null;
        _cachedAnswers = null;
        _cachedAssignmentPath = null;
        _wav = null;
        _play.Enabled = _save.Enabled = _clear.Enabled = false;
        _progress.Visible = false;
    }

    private void SetAnswerText(string value)
    {
        _settingText = true;
        try { _text.Text = value; }
        finally { _settingText = false; }
    }

    private void SaveWav()
    {
        if (_wav is null) return;
        using var dialog = new SaveFileDialog { Filter = "WAV 音频|*.wav", FileName = "ets-speech.wav" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllBytes(dialog.FileName, _wav); _status.Text = $"已保存：{dialog.FileName}"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        _status.Text = ex.Message;
        MessageBox.Show(this, ex.Message, "ETS 语音输出", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private sealed record Assignment(string Path, DateTime ModifiedAt)
    {
        public override string ToString() => $"{ModifiedAt:yyyy-MM-dd HH:mm:ss} · {System.IO.Path.GetFileName(Path)}";
    }

    private sealed record AnswerRow(SpeechItem Item, byte[]? Wav, string? CachedText);
}
