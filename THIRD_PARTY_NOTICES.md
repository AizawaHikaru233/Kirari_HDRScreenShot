# Third-Party Notices

Kirari 依赖以下第三方组件。所有组件均为开源许可，且与 GPL-3.0 兼容（Apache-2.0 与 MIT
代码可以合并进 GPL-3.0 作品；反向不可）。分发二进制时请连同本文件一并分发，以满足各许可
的署名/许可证文本要求。

## NuGet 依赖

| 组件 | 许可 | 用途 |
| --- | --- | --- |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)（Vortice.Direct3D11 / Vortice.DXGI / Vortice.Mathematics） | MIT | Direct3D 11 / DXGI 绑定：屏幕捕获回读、HDR 背景窗交换链、显示器元数据 |
| [RapidOcrNet](https://github.com/BobLd/RapidOcrNet) | Apache-2.0 | OCR 推理管线（RapidOCR 的 .NET 移植） |
| [Microsoft.ML.OnnxRuntime](https://github.com/microsoft/onnxruntime) | MIT | ONNX 模型推理运行时（RapidOcrNet 依赖） |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | OCR 输入图像处理（RapidOcrNet 依赖） |
| [PContourNet](https://github.com/BobLd/PContourNet) | MIT | 二值图轮廓提取（RapidOcrNet 依赖） |

## 模型文件（`models/v5/`）

| 文件 | 许可 | 来源 |
| --- | --- | --- |
| `ch_PP-OCRv5_mobile_det.onnx`、`ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx`、`latin_PP-OCRv5_rec_mobile_infer.onnx`、`ppocrv5_latin_dict.txt` | Apache-2.0 | 随 RapidOcrNet 分发，源自 [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) / [RapidAI/RapidOCR](https://github.com/RapidAI/RapidOCR) |
| `ch_PP-OCRv5_rec_mobile.onnx`、`ppocrv5_dict.txt` | Apache-2.0 | 从 RapidAI/RapidOCR（modelscope 镜像 v3.9.2）下载，SHA256 已校验 |

## 行为参考（未复制代码，无许可义务，仅致谢）

- HDR PNG 的 cICP/cLLI/mDCV/sBIT 信令布局参考了 [ReShade](https://github.com/crosire/reshade)（BSD-3-Clause）与
  [Special K](https://github.com/SpecialKO/SpecialK)（GPL-3.0）的行为，以及
  [ledoge/jxr_to_png](https://github.com/ledoge/jxr_to_png) 开创的"iCCP 兜底 SDR 显示"思路。
  本项目的 PNG 写入器、块封装、ICC 配置文件生成器均为独立实现。
- W3C [PNG 第三版规范](https://www.w3.org/TR/png-3/) 与 ITU/SMPTE 公开标准文档。

## 系统组件（不随本软件分发）

Windows Graphics Capture、DWM、WIC（JPEG 编码）、Direct3D 运行时等由操作系统提供，
属 GPL-3.0 的"系统库"例外范畴。
