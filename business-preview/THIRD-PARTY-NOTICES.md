# 第三方组件与来源

商业版独立分发，不继承旧版所有依赖，也不使用 OpenAI/Codex 付费服务或账号。

| 组件 | 固定版本/来源 | 许可 | 用途 |
| --- | --- | --- | --- |
| llama.cpp | b10566 / bb4caa7540188872173c44d161602d9271386413；https://github.com/ggml-org/llama.cpp | MIT | CPU 本地推理 |
| LLVM OpenMP | 随上述 Windows 构建分发 | 包内 LICENSE-LLVM-OpenMP | CPU 线程运行时 |
| Microsoft.Playwright | 1.62.0；https://github.com/microsoft/playwright-dotnet | .NET 库 MIT；捆绑驱动按包内许可 | 浏览器结构化工具 |
| DocumentFormat.OpenXml | 3.5.1；https://github.com/dotnet/Open-XML-SDK | MIT | Office 文件直接处理 |
| Qwen3.5-2B / Unsloth GGUF | https://huggingface.co/unsloth/Qwen3.5-2B-GGUF；revision f6d5376be1edb4d416d56da11e5397a961aca8ae | Apache-2.0（仓库声明） | 文字模式轻量语义候选 |

完整正文位于产品根目录 `Licenses`、`Runtime/llama` 与 NuGet/发布包随带第三方说明。模型以文件大小和 SHA256 校验后启用；不修改模型权重。本应用不宣称模型作者对产品作任何背书。

自包含 .NET Windows 发布包须保留 SDK 自动携带的 `ThirdPartyNotices.txt` 与 `LICENSE.txt`。Playwright 的 `.playwright` 目录须保留包内所有 LICENSE/NOTICE，不只复制驱动 exe。

## 分发前检查

预览版不等于法律、隐私或商用稳定性验收完成。正式安装器、代码签名、升级系统、模型和音色权利、国内网络下载可达性需独立核验。
