using System.ComponentModel;

namespace presents.Vision.Qwen3VL;

public class Qwen3VLVisionModelConfig
{
    /// <summary>
    /// 思考模式：开启后模型先推理再作答，质量更高但更慢（会消耗更多输出 token）。
    /// 长时间多帧视频分析建议关闭以获得速度；单张图片深度理解可开启。
    /// </summary>
    public bool EnableThinking { get; set; } = false;

    /// <summary>
    /// 本地模型目录（可选）。填了就**直接从这个目录加载**，完全不联网、不查 cache、不会重复下载；
    /// 留空则走 huggingface_hub 的 snapshot_download（下到 HF 缓存，通常是
    /// %USERPROFILE%\.cache\huggingface\hub，可用环境变量 HF_HOME 改到别的盘）。
    /// 目录里应有 config.json / *.safetensors / preprocessor_config.json / tokenizer* 等文件。
    /// 适合：权重已经手工下好、或想把它放在固定位置（清缓存也不会坏）。
    /// </summary>
    [Description("本地模型目录（可选）：填了就直接从该目录加载（不联网、不查缓存、不会重复下载）；"
        + "留空则自动从 HuggingFace 下载到缓存。例如 D:\\models\\Qwen3-VL-4B-Instruct。")]
    public string ModelPath { get; set; } = "";
}
