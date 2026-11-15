# 外部运行组件

本仓库保留应用源码与已有第三方声明。历史开发机使用的模型、.NET SDK、语音运行环境和原生 DLL 需要另行安装。

- 语音接口：[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)。原工程引用 `vendor/sherpa-onnx/sherpa-onnx.dll`、`onnxruntime.dll`、`sherpa-onnx-c-api.dll`，须使用互相兼容的同一发行版本并遵守上游许可证。
- 本地推理：[llama.cpp](https://github.com/ggml-org/llama.cpp)，历史目录名 `llama.cpp-b10566`。
- 外部代理：[OpenAI Codex](https://github.com/openai/codex)。相关许可证随仓库保留。
- 微信辅助脚本的来源和许可证保留在 `weixin-fast-send` 目录。

模型和云服务名称是历史记录；本次开源没有验证它们的当前可用性。原工程中 `D:\HuiTian` 路径需按自己的目录配置。
