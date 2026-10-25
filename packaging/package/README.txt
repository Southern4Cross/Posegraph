Posegraph Backend (one-click package) / Posegraph 后端一键包
===========================================================

English
-------
This folder contains everything the Posegraph plugin needs to recognize poses:
a portable Python 3.11 with PyTorch (CUDA build, also runs on CPU), the Posegraph backend
source, the SAM 3D Body source and its model weights.

1. Extract this folder somewhere with enough space (about 7 GB). An ASCII-only path is recommended.
2. In game, open Posegraph (/posegraph) -> Settings -> "Choose backend package folder…"
   and pick this folder (the one containing "python" and "backend").
3. Click the plug icon in the status bar to start the backend. The model loads on the first recognition.

You can also run Start-Backend.bat to start it manually.
Requirements: Windows 10/11 x64. An NVIDIA GPU with ~3 GB free VRAM is used when available;
otherwise it runs on the CPU (about 10-20 s per image, ~4 GB RAM).

中文
----
本文件夹包含 Posegraph 插件识别姿势所需的全部内容：便携版 Python 3.11 与 PyTorch（CUDA 版，也能用 CPU 跑）、
Posegraph 后端源码、SAM 3D Body 源码与模型权重。

1. 解压到空间充足的位置（约 7GB），路径建议只用英文。
2. 游戏里打开 Posegraph（/posegraph）→ 设置 →「选择一键包目录…」，选这个文件夹（里面有 python 和 backend）。
3. 点状态栏的插头按钮启动后端，首次识别时加载模型。

也可以双击 Start-Backend.bat 手动启动。
要求：Windows 10/11 64 位。有 NVIDIA 显卡且可用显存约 3GB 时用显卡，否则用 CPU（每张约 10~20 秒，内存约 4GB）。

Licenses / 许可证: see the LICENSES folder.
Posegraph itself: PolyForm Noncommercial License 1.0.0 (noncommercial use only / 仅限非商业用途).
SAM 3D Body (code and weights) is released under the SAM License by Meta; other components keep their own licenses.
