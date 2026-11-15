# 回天 AI 第三方开源组件说明

本文件记录回天 AI 第一版使用或分发的开源组件。普通产品界面不展示内部技术名称，但依法保留许可证、版权和来源说明。

## OpenAI Codex

- 用途：本机任务调度骨架与 App Server 协议。
- 来源：https://github.com/openai/codex
- 固定版本：`343074d4207d572809bd8cea15f4be1d09d98e0b`
- 许可证：Apache License 2.0。
- 完整许可证：源码中的 `vendor/openai-codex/LICENSE`；发布目录中的
  `licenses/Apache-2.0.txt`。

## llama.cpp

- 用途：Windows 本地推理与视觉运行组件。
- 来源：https://github.com/ggml-org/llama.cpp
- 固定版本：`bb4caa7540188872173c44d161602d9271386413`（build b10566）。
- 许可证：MIT License。
- 完整许可证：源码中的 `vendor/llama.cpp-b10566/LICENSE`；发布目录中的
  `licenses/MIT-llama.cpp.txt`。

## Qwen3.8-27B 与社区衍生量化文件

- 用途：回天本地智能核心与视觉理解。
- 官方模型：https://www.modelscope.cn/models/Qwen/Qwen3.8-27B
- 本版本衍生量化来源：https://huggingface.co/JonathanColetti/Qwen3.8-27B-Uncensored-GGUF
- 本版本使用与文字权重同仓库、同版本的
  `Qwen3.8-27B-Uncensored-vision-f16.gguf` 作为视觉投影文件。文件名只需要一个
  `.gguf` 后缀；重复后缀不是运行要求。
- 上游模型与衍生量化仓库均声明采用 Apache License 2.0。本衍生版本的主要变化是
  降低拒答倾向，不应宣传为参数更多、速度提高 300% 或能力必然更强的官方升级版。
- 完整许可证正文随发布目录的 `licenses/Apache-2.0.txt` 提供。
- 下载文件按目录清单中的文件大小和 SHA256 双重校验，校验通过前不会覆盖已安装核心。

### 免费镜像核验

- `hf-mirror.com` 是免费社区镜像，本机在不使用代理的连接测试和实际分块下载中可用；
  但大文件最终会跳转到境外对象存储，不能把它表述成“国内自建存储”或承诺所有地区
  永久可用。
- GitCode 的公开镜像为
  https://ai.gitcode.com/hf_mirrors/JonathanColetti/Qwen3.8-27B-Uncensored-GGUF 。
  核验时仓库提交与上游一致，视觉文件以及 Q4_K_M、Q6_K、Q8_0、IQ4_XS 的 LFS
  对象可取得临时签名下载地址；当前黄阶使用的 IQ2_M 对象未同步并返回 404，因此
  GitCode 不能作为本机当前配置的唯一下载源。
- 当前下载清单使用免费 `hf-mirror.com` 为首选、上游仓库为备用；不使用需要付费的
  第三方下载站。镜像可用性仍须由下载引擎测速、断点续传、自动重试和完整性校验保障。

## sherpa-onnx、Kokoro 与 MeloTTS

- 用途：完全本地的实时语音识别、唤醒词识别与自然语音合成。
- sherpa-onnx 来源：https://github.com/k2-fsa/sherpa-onnx
- sherpa-onnx 许可证：Apache License 2.0；模型目录内同时保留其许可证文件。
- 0.3.5 的“清和”中英双语女声来自 MyShell.ai 的 MeloTTS：
  https://github.com/myshell-ai/MeloTTS ，许可证为 MIT License。
- 回天使用的是 sherpa-onnx 提供的 `vits-melo-tts-zh_en` ONNX 转换版本。转换说明、
  转换脚本和原始模型来源分别为：
  - https://k2-fsa.github.io/sherpa/onnx/tts/pretrained_models/vits.html#vits-melo-tts-zh-en-chinese-english-1-speaker
  - https://github.com/k2-fsa/sherpa-onnx/tree/master/scripts/melo-tts
  - https://huggingface.co/myshell-ai/MeloTTS-Chinese
- ONNX 模型发布包来源：
  https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-melo-tts-zh_en.tar.bz2 。
  包内保留 MyShell.ai 的 MIT `LICENSE`，完整许可证随产品语音资源提供。
- 可从合法稳定国内镜像取得的语音与识别资源优先使用国内下载源；本轮 MeloTTS ONNX
  包按上方列明的 sherpa-onnx 官方发布地址取得。所有资源在启用前进行完整性校验。

### 仅隔离测试、未随产品启用的语音候选

- ZipVoice 来源：https://github.com/k2-fsa/ZipVoice ，其源码仓库声明 Apache License 2.0。
- 本轮只把 sherpa-onnx 的
  `sherpa-onnx-zipvoice-distill-int8-zh-en-emilia` 与 `vocos_24khz.onnx` 放在
  `D:\HuiTian\Cache\VoiceCandidates\ZipVoice\` 隔离测试；它们没有复制到产品运行目录，
  没有作为可选音色启用，也不随 0.3.5 发布包分发。
- 未启用原因是当前 Windows 动态库下首个回调约 7.760 秒、实测 RTF 6.583，并出现
  中文兼容警告，无法满足实时语音目标。若将来重新评估，仍须单独复核模型权重、声码器
  和参考音频各自的许可证及商用授权，不能只用源码仓库许可证代替模型授权。

## NAudio

- 用途：在 Windows 上以低延迟采集麦克风 PCM 音频，交给本地语音识别器处理。
- 来源：https://github.com/naudio/NAudio
- 许可证：MIT License。

## GeoNames 城市坐标数据

- 用途：把用户明确给出的中国城市名离线解析为天气接口所需的经纬度和海拔；不调用
  需要注册账号的 GeoNames Web Service。
- 来源：https://download.geonames.org/export/dump/cities15000.zip
- 取数说明：2026-08-27 下载前由官方目录与 HTTP `Content-Length` 核实压缩包为
  3,309,859 字节（低于 15 MB 上限），原始包与处理缓存均位于 `D:` 盘。
- 产品内仅嵌入原始表中 `country code = CN` 的城市记录，并删减为名称、别名、坐标、
  人口、海拔和时区等必要字段；这是经过筛选和删列的衍生数据。
- 许可证：Creative Commons Attribution 4.0 International：
  https://creativecommons.org/licenses/by/4.0/ 。
- 数据由 GeoNames 提供，按“原样”提供，不保证准确性、及时性或完整性。

## MET Norway Locationforecast 2.0

- 用途：运行时按 GeoNames 坐标读取未来天气预报，并由回天按中国标准时间汇总未来
  7 天的每日高低温、代表天气和降水量。
- 来源：https://api.met.no/weatherapi/locationforecast/2.0/
- 数据提供方：The Norwegian Meteorological Institute（MET Norway）。
- 许可证：Norwegian Licence for Open Government Data (NLOD) 2.0 与 Creative
  Commons Attribution 4.0 International：
  https://data.norge.no/nlod/en/2.0 ，
  https://creativecommons.org/licenses/by/4.0/ 。
- 改动说明：回天对逐时/逐区间预报进行了时区转换、逐日聚合、数值取整和中文天气
  描述映射；这些改动不表示 MET Norway 或 GeoNames 对回天产品的认可。
- 客户端按服务条款使用最多 4 位小数的坐标，遵守 `Expires` / `Last-Modified` 缓存
  指示，并以真实应用身份 `HuiTian/1.0` 访问；不虚构联系邮箱或网站。

## weixin-auto-send（局部改写）

- 用途：在微信 4.x 不向 Windows UI Automation 开放内部控件时，使用系统自带的
  `Windows.Media.Ocr` 离线识别联系人搜索结果和当前聊天标题，核对一致后才允许
  回天发送普通消息或文件。
- 来源：https://github.com/raphael2025/weixin-auto-send
- 原作者版权：Copyright (c) 2026 raphael。
- 许可证：MIT License。
- 回天保留并改写了窗口发现、DPI 适配、局部截图 OCR、联系人分区过滤和标题二次
  核对思路；改为一次 PowerShell 进程完成搜索结果与标题两次 OCR，歧义或标题不符
  时不发送。实际文字与文件仍由回天进程在核验成功后发送，并完整恢复用户剪贴板。
- 完整许可证及改写说明：源码中的 `vendor/weixin-fast-send/LICENSE` 与
  `vendor/weixin-fast-send/NOTICE.md`；发布目录中的同名 `vendor/weixin-fast-send/`
  文件。

## 说明

开源许可证允许在遵守其条款的前提下进行商业使用。回天 AI 的品牌、界面、产品逻辑、授权体系和专有代码不因引用开源组件而自动变成第三方品牌产品。
