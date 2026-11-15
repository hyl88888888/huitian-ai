# Weixin fast target verification

The PowerShell helper in this directory is adapted from
[`raphael2025/weixin-auto-send`](https://github.com/raphael2025/weixin-auto-send),
copyright (c) 2026 raphael and distributed under the MIT License in `LICENSE`.

The adaptation combines window discovery, one contact search, result-list OCR,
recipient selection and chat-title OCR into one process. It uses the offline
`Windows.Media.Ocr` capability included with supported Chinese Windows
installations. It deliberately does not send message text or files: after it
has verified the recipient, the HuiTian process performs the payload paste and
send while preserving the user's clipboard.
