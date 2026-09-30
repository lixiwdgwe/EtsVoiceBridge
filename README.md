# ETS 参考答案离线语音输出（Windows / AMD）

双击 `dist\question-order\EtsVoiceBridge.exe` 打开图形界面。程序扫描 EtsViewer 使用的 ETS 作业目录，只提取参考答案，交给本机 Piper 英语语音模型，通过 AMD 显卡的 DirectML 后端生成语音，再播放到选定的 Windows 输出设备。文字和音频不发送给在线语音服务。

请保留整个 `EtsVoiceBridge` 文件夹。EXE 在 `dist\question-order` 中，本地 Python 环境位于 `.venv`，模型位于 `models`；单独复制 EXE 无法运行语音功能。发布的 EXE 自带 .NET 运行时。

## 源码分析

EtsViewer 的 `main.py` 从 `%APPDATA%\ETS`（或漫游目录下的长十六进制名称目录）找到作业，读取作业下的 `content*\content.json`。本程序按其字段结构提取：

| 作业类型 | 提取内容 |
| --- | --- |
| `collector.role` | 每道题的 `info.question[].std[0].value`，只取第一条参考答案 |
| `collector.picture` | `info.std[0].value`，只取第一条参考答案 |
| `collector.choose` | `info.xtlist[].answer` 所指向的选项文字 |

朗读、跟读和词汇原文不作为参考答案，不进入语音列表。语音模型只接收选中答案的正文，不接收题目提示或列表标签。

使用前可在界面右侧检查或修改提取出的内容。发布包不包含作业或答案数据。

`content_数字` 是资源 ID，不代表作业题号。后面的题目可能使用更小的资源 ID，替换后的题目也可能使用更大的资源 ID。时间是缓存文件更新时间，也不能作为正式题序。本程序缓存的 `question-order-cache.json` 仅保存模板和题目 ID；扫描目录时会从本机 ETS 日志更新绑定清单。

## 使用

1. 双击 `dist\question-order\EtsVoiceBridge.exe`。默认查找本机 ETS 作业目录；也可点“选择并扫描”指定目录。作业列表显示作业文件夹的最后修改时间，与 EtsViewer 的“更改时间”口径一致。
2. 选择作业和参考答案，必要时修改右侧文字。GPU 编号默认选第一个 AMD 适配器。
   答案默认使用“作业题序（自动识别）”：优先读取客户端作业结构中 `structs[].contents[]` 的 `code_id → real_id` 绑定，结合模板 `res.json` 的 `exam_type_order`、`exam_order`、`question_order` 排序。列表显示 `content2.json` 中的正式题号和章节名称。旧作业缺少绑定清单时，根据正式题号推断章节顺序；状态栏会显示题序依据。作业和答案仍可手动按时间或文件编号排序，切换排序会保留音频对应关系。
3. 在“播放到”选择输出设备，点“生成并输出”可即时生成当前答案。“重播”不会重新生成，“保存 WAV”可保存当前音频。
4. 点“生成整份作业音频”，程序会为每条参考答案生成单独音频并暂存在内存中。进度条显示已完成题数；生成后点选任意答案即可播放，点“重播”可再次播放当前答案。“清除音频”会停止播放并清空缓存，切换作业或修改答案也会清空缓存。批量生成不会导出文件。
5. 如需输入到其他软件的麦克风，请先安装 VB-CABLE。本程序播放设备选 `CABLE Input`，目标软件的麦克风选 `CABLE Output`，即可把答案语音送入目标软件。

## 从源码重建

需要 Windows、64 位 Python 3.12 和 .NET 10 SDK。首次安装依赖与模型需要联网；安装后生成语音可离线进行。PowerShell 中进入此目录执行：

```powershell
.\setup-local.ps1 -PythonExecutable "C:\path\to\python.exe"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\question-order
```

模型使用 [Piper 的 en_US-lessac-medium 语音](https://huggingface.co/rhasspy/piper-voices/tree/main/en/en_US/lessac/medium)。这台电脑上的 Kokoro ONNX 在 DirectML 的 `ConvTranspose` 算子上失败，因此交付版本采用已经在 AMD Radeon RX 6750 GRE 10GB 上完成推理的 Piper 模型。


## 第三方组件

运行包包含 Python、ONNX Runtime DirectML、NumPy、phonemizer、eSpeak NG 等组件，许可证随 Python 目录和各依赖的 dist-info 文件夹保留。Piper 模型来源及模型卡见 models/MODEL_CARD.txt。各组件和模型适用其各自条款。

