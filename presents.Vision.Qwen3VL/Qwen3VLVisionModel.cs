using System;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.AIModelUtility;
using Microsoft.Extensions.Logging;
// 本模块命名空间嵌在 Alife.Function.Vision 之下，而该命名空间内有一个过时的
// IVisionModel（见 Alife.Function.Vision/Obsolete.cs）。C# 解析裸名 IVisionModel 时，
// "外层命名空间成员"优先于 using 导入，会绑到那个过时接口，导致本模块被注册在过时接口名下，
// 而视频理解按 AIModelUtility.IVisionModel 查找，于是永远找不到它。
// 这里用别名强制指向正确接口（与 VisionRouter 中同样的处理方式）。
using VisionModel = Alife.Function.AIModelUtility.IVisionModel;

namespace presents.Vision.Qwen3VL;

[Module("Qwen3-VL视觉分析",
    "基于 Qwen3-VL-4B 的本地视觉分析引擎，支持思考模式（可开关）。",
    defaultCategory: "presents/视觉模型",
    EditorUI = typeof(Qwen3VLVisionModelUI))]
public class Qwen3VLVisionModel(
    IAlifeApplication alifeApplication,
    ILogger<Qwen3VLVisionModel> logger) :
    ChatBehaviour,
    VisionModel,
    IConfigurable<Qwen3VLVisionModelConfig>
{
    public Qwen3VLVisionModelConfig Configuration { get; set; } = null!;

    public async Task<string> QueryAsync(string imagePath, string question, int maxResponseTokens,
        CancellationToken cancellationToken = default)
    {
        if (initError != null)
            return $"Qwen3-VL 模型未就绪：{initError}";
        if (pythonPipe == null)
            return "Qwen3-VL 的 Python 进程未启动。";
        try
        {
            return await pythonPipe.InvokeAsync<string>("query",
                new { image_path = imagePath, question, max_new_tokens = maxResponseTokens, enable_thinking = Configuration.EnableThinking });
        }
        catch (Exception ex)
        {
            return $"调用失败：{ex}";
        }
    }

    /// <summary>模型初始化失败的原因；非 null 表示模块已注册但暂不可用（下拉里仍可见）。</summary>
    string? initError;

    PythonPipeProcess pythonPipe = null!;
    readonly string pythonCode =
        """
        import os
        # ★ 关掉 Xet 的分块缓存：hf_hub 1.x 默认走 Xet，会在 HF 缓存之外再存一份 chunk 副本，
        #   同一个模型可能占两份磁盘（"下了一次，C 盘掉两倍"多半是它）。这里固定关掉。
        os.environ.setdefault("HF_HUB_DISABLE_XET", "1")
        os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")
        import sys, json, torch
        from PIL import Image
        from transformers import Qwen3VLForConditionalGeneration, Qwen3VLProcessor, BitsAndBytesConfig
        from qwen_vl_utils import process_vision_info

        device = torch.device('cuda')
        model = None
        processor = None

        def _resolve_model_dir(model_id, local_path):
            # 决定从哪加载模型（顺序固定）：
            #   ① 配置里填了 ModelPath 且目录存在 → 直接用它（不联网、不查缓存）；
            #   ② 缓存里已经完整 → local_files_only **只读本地**（不联网、不重下）——
            #      这是"每次启动都重新加载一次缓存"的根治：完整时一次 HTTP 都不会发；
            #   ③ 只有缺文件时才联网下载（并发压到 2，中断后残留的 .incomplete 分片更少）。
            # 注意：这里只能用 # 注释 —— 本段是 C# 原始字符串（三引号），不能出现三引号。
            if local_path and os.path.isdir(local_path):
                print(f"[qwen3-vl] 使用本地模型目录: {local_path}", flush=True)
                return local_path
            if local_path:
                print(f"[qwen3-vl] ModelPath 不存在，回退 HF 缓存: {local_path}", flush=True)
            from huggingface_hub import snapshot_download
            try:
                p = snapshot_download(model_id, local_files_only=True)
                print(f"[qwen3-vl] 命中 HF 缓存（未联网）: {p}", flush=True)
                return p
            except Exception as e:
                print(f"[qwen3-vl] HF 缓存不完整（{type(e).__name__}）", flush=True)

            # ② ModelScope 缓存里已有同一模型 → 直接用（国内镜像，且没有 hf-mirror 的 Xet 302 问题）
            ms_home = os.environ.get("MODELSCOPE_CACHE") or os.path.join(os.path.expanduser("~"), ".cache", "modelscope", "hub")
            ms_dir = os.path.join(ms_home, "models", *model_id.split("/"))
            if os.path.isdir(ms_dir) and os.path.isfile(os.path.join(ms_dir, "config.json")):
                print(f"[qwen3-vl] 使用 ModelScope 缓存: {ms_dir}", flush=True)
                return ms_dir

            # ③ 两条下载路线：先 HF（官方/镜像），失败再 ModelScope（国内更快）。都支持断点续传。
            try:
                p = snapshot_download(model_id, max_workers=2)
                print(f"[qwen3-vl] HF 下载完成: {p}", flush=True)
                return p
            except Exception as e:
                print(f"[qwen3-vl] HF 下载失败（{type(e).__name__}: {e}），改用 ModelScope…", flush=True)
            from modelscope import snapshot_download as ms_snapshot_download
            p = ms_snapshot_download(model_id)
            print(f"[qwen3-vl] ModelScope 下载完成: {p}", flush=True)
            return p

        def init(model_id, local_path=""):
            global model, processor
            model_path = _resolve_model_dir(model_id, local_path)
            quantization_config = BitsAndBytesConfig(
                load_in_4bit=True,
                bnb_4bit_compute_dtype=torch.bfloat16,
                bnb_4bit_use_double_quant=True,
                bnb_4bit_quant_type="nf4"
            )
            model = Qwen3VLForConditionalGeneration.from_pretrained(
                model_path,
                dtype="auto",
                quantization_config=quantization_config,
                device_map="auto",
                attn_implementation="sdpa"
            )
            processor = Qwen3VLProcessor.from_pretrained(
                model_path,
                min_pixels=256 * 28 * 28,
                max_pixels=480 * 28 * 28
            )
            return "ready: " + model_path

        def query(image_path, question, max_new_tokens, enable_thinking):
            image = Image.open(image_path).convert("RGB")
            messages = [
                {
                    "role": "user",
                    "content": [
                        {"type": "image", "image": image},
                        {"type": "text", "text": question},
                    ],
                }
            ]
            try:
                text = processor.apply_chat_template(
                    messages, tokenize=False, add_generation_prompt=True, enable_thinking=enable_thinking
                )
            except TypeError:
                text = processor.apply_chat_template(
                    messages, tokenize=False, add_generation_prompt=True
                )
            image_inputs, video_inputs = process_vision_info(messages)
            inputs = processor(
                text=[text],
                images=image_inputs,
                videos=video_inputs,
                padding=True,
                return_tensors="pt",
            )
            inputs = inputs.to(device)
            with torch.inference_mode():
                generated_ids = model.generate(**inputs, max_new_tokens=max_new_tokens)
                generated_ids_trimmed = [
                    out_ids[len(in_ids):] for in_ids, out_ids in zip(inputs.input_ids, generated_ids)
                ]
                res = processor.batch_decode(
                    generated_ids_trimmed, skip_special_tokens=True, clean_up_tokenization_spaces=False
                )
            del inputs, generated_ids
            torch.cuda.empty_cache()
            return res[0].strip()
        """;

    protected override async Task OnAwake()
    {
        const string ModelId = "Qwen/Qwen3-VL-4B-Instruct";
        pythonPipe = new(alifeApplication, "qwen3_vl", pythonCode);
        await pythonPipe.StartAsync();
        try
        {
            // 第二个参数 = 本地模型目录（配置里填了就直连目录，不联网、不查缓存）
            await pythonPipe.InvokeAsync<string>("init", ModelId, Configuration?.ModelPath ?? "");
        }
        catch (Exception ex)
        {
            // 模型加载失败时保持模块处于已注册状态，
            // 避免模块从视觉模型列表里消失导致下拉为空、无从选择。
            initError = ex.Message;
            logger.LogError(ex, "[Qwen3-VL] 模型初始化失败，模块保持注册，调用时将返回错误");
        }
    }
    protected override async Task OnDestroy()
    {
        await pythonPipe.DisposeAsync();
    }
}
