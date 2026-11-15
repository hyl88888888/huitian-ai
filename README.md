# 回天 AI · HuiTian AI

Windows 桌面 AI 助手的历史源码，包含聊天、语音、桌面任务和本地模型适配模块。

**作者：侯禹良（[hyl88888888](https://github.com/hyl88888888)）**  
**联系电话：15234732550**

欢迎交流、反馈问题或参与改进。其他作品：[回天设计](https://github.com/hyl88888888/huitian-design)、[回天创作台](https://github.com/hyl88888888/huitian-creator-studio)。

## 项目结构

- `src/HuiTian.App`：WPF 桌面界面与语音集成。
- `src/HuiTian.Core`：核心模型、协议和任务逻辑。
- `src/HuiTian.Infrastructure`：服务与工具适配。
- `tests`：历史测试和诊断程序。
- `scripts`：开发辅助脚本。

## 构建准备

工程面向 Windows 和 .NET 10。需要 .NET 10 SDK，并按 [外部组件说明](vendor/README.md) 配置原生语音库、模型和本机路径后构建：

```powershell
dotnet build src/HuiTian.App/HuiTian.App.csproj -c Release
```

这是 v0.4.13 历史源码整理版。原工程保留了一些开发机绝对路径；直接克隆尚不能保证构建或运行。此次发布没有重新安装模型、SDK 或语音环境，也没有重新执行完整构建和联网/桌面自动化测试。历史测试说明位于 `docs`，不代表当前验证结果。

## 公开范围

仓库提供应用源码、测试和必要说明。客户资料、个人文件、会话记录、访问密钥、私有激活资料、模型权重、运行环境及历史安装包保存在仓库之外。第三方实现和数据的权利声明见 `THIRD-PARTY-NOTICES.md`；其中模型与运行环境说明为历史记录。

## 许可证

本项目原创源码按 MIT 许可证发布，见 [LICENSE](LICENSE)。第三方代码和资源使用各自原许可证。
