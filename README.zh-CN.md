# Posegraph

[English](README.md) | **简体中文**

把照片、插画或对话描述的姿势，摆到你的 FF14 角色身上。

Posegraph 是一个 Dalamud 插件，配合本地运行的 AI 后端。在游戏里选一张图，后端用 Meta 的
[SAM 3D Body](https://github.com/facebookresearch/sam-3d-body) 识别 3D 人体姿态，重定向到你所在种族的 FF14 骨架上，
插件再通过 [Brio](https://github.com/Etheirys/Brio) 应用到 GPose 角色。
也可以接入自己的模型 API，通过聊天生成姿势，或在导入图片后用多轮对话继续调整。

```
Posegraph 插件（游戏内 ImGui）  ── HTTP（127.0.0.1）──►  Posegraph 后端（本地 Python）
  状态栏、选图、叠加层、                                  SAM 3D Body 识别（每张图一次）
  3D 预览、调整滑杆、                                     MHR → FF14 重定向（每次调整约 50ms）
  应用 / 镜头 / 导出             ◄── Brio 姿势 JSON ──   种族骨架、合理性检查
        │
        └── Brio IPC：冻结角色、导入姿势
```

## 功能

- **照片和插画都支持**，全身或半身，包括手指。「插画」预设会收敛插画里夸张的头颈比例。
- **全部可玩种族的男性**：按角色外貌自动识别种族（也可手动指定）。中原系与精灵男已在游戏内实测，拉拉菲尔与鲁加系男换种族后体型比例错误的问题已在 0.3.2 修复。
  女性骨架**尚未适配**（骨架数据已包含，但重定向规则是按男性骨架调校的）。
- **调参不用重新识别**：分部位幅度（脊柱、颈、头、锁骨、手腕）、手部末端补偿、重力校正、自动贴地、辅助骨强度，
  以及 3D 火柴人里的逐关节手动偏移。拖动滑杆只刷新预览，松手才应用（Brio 撤销历史保持干净）。
- **合理性提示**：阈值取自游戏动画采样，带一键修正建议。
- **手动 AI 姿势建议**：在「提示」页主动分析当前预览或角色姿势，结合骨骼与身体部位的对应、关节关系和规则提示给出建议；分析不会修改姿势。
- **手部末端补偿**：手贴着头、胸、髋时，即使种族比例和照片里的人不同，也能保持接触；肘部保持纯铰链。
- **复刻镜头**：在 GPose 里还原照片的机位、距离和视角。
- **导出** Brio `.pose` 文件。
- **对话调姿**：直接用文字生成姿势，或基于当前识别结果多轮调整。设置中填写兼容 OpenAI 的 API 端点、密钥和模型名。
  普通文本模型即可处理骨架；发送参考图是可选项，开启时需要支持图像输入的模型。
- **对低显存友好**：自动 / GPU / CPU 三档，默认识别后立即释放模型；CPU 模式完全不占显存。
- **中英文界面**，默认跟随游戏客户端语言。

## 运行要求

- 装好 Dalamud（API 15）并启用 **Brio** 的 FF14
- Windows 10/11 64 位
- 用显卡识别图像：NVIDIA 显卡，可用显存约 **3GB**（识别时约占 2GB）。没有的话用 CPU：每张约 10~20 秒，内存约 4GB。
- 后端一键包约 7GB 磁盘空间
- 对话调姿需要兼容 OpenAI Chat Completions 的云端或本地服务，以及模型名和访问凭据。
  纯文字对话仍需本地后端，但不会加载 SAM 3D Body，也不要求其权重、PyTorch 或显卡；下方提供轻量源码安装方式。

## 安装

### 1. 后端一键包

1. 下载 **[Posegraph-Backend-win64.zip](https://drive.google.com/file/d/1PnbqdyH0hOTMSPhBaSXQ-Dpjp3fil6k2/view?usp=sharing)**（谷歌硬盘，约 4GB），解压到空间充足的位置，路径建议只用英文。
2. 解压后有 `python`、`backend`、`LICENSES`、`Start-Backend.bat`、`README.txt`，不需要安装任何东西。

### 2. 插件

Posegraph 不在 Dalamud 官方仓库里，从自定义仓库安装：

1. 游戏里 `/xlsettings` → 实验性 → 自定义插件仓库，添加
   `https://raw.githubusercontent.com/extrant/DalamudPlugins/main/pluginmaster.json`，勾选并保存。
2. 在插件管理器里搜索 **Posegraph** 并安装。

也可以从[发布页](https://github.com/extrant/Posegraph/releases)下载 `Posegraph.zip` 解压，把 `Posegraph.dll` 添加到 Dev Plugin Locations。

### 3. 首次使用

1. `/posegraph` 打开主界面。
2. 设置 → **选择一键包目录…** → 选解压出来的文件夹。
3. 点状态栏的**插头**按钮启动后端。可以勾选「插件加载时自动启动后端」。

### 更新

- **插件**：通过插件仓库自动更新。
- **后端（一键包用户）**：插件更新后，设置页会显示「后端版本 …，插件 …」。后端比插件旧时，点旁边的下载按钮。
  插件会从本仓库对应标签下载同版本的源码（`archive/refs/tags/vX.Y.Z.zip`，不到 1MB），只替换 `backend\src`，Python、依赖和模型都不动。
  旧版保留为 `backend\src.bak`，新版启动失败会自动回滚。连不上 GitHub 时可以填加速前缀，例如 `https://gh-proxy.org/`。
- 只有依赖变化时才需要重新下载完整一键包，插件会提示（一键包的 `runtime.json` 低于新版后端的要求）。
- 源码安装：用 `git pull` 或切换到同版本标签更新。

## 使用

1. 进入 GPose，选中要摆姿势的角色（或在角色列表里选；列表只列玩家、Brio 新建的角色和雇员等人形 NPC）。
2. 点文件夹按钮，在菜单中选择「选择图片（多选）」或「选择文件夹（包含子文件夹）」。
   也可以把多张图片或文件夹拖到「图像」页（jpg、png、webp、bmp、gif 首帧、tiff、avif）。
   文件夹按钮右侧的剪切板按钮，说明为「识别剪切板中的图像」，可直接导入已复制的图片。
3. 首次识别会加载模型（约 10~20 秒），之后每张几秒。
   导入多张图片时，在后台预识别整批图片。每张识别完成后即可点击左侧列表中的成功项，加载对应预览和姿势，
   其余图片继续识别。只加载用户选中的图片，并按原有「识别完成后立即应用到角色」设置决定是否自动应用。
   单张图片识别完成后自动加载。
4. 看「图像」页叠加层（绿 = 识别骨架，橙 = FF14 骨架）和「3D」页。
5. 在「调整」页微调，看「提示」页，然后点 **应用**（▶）。相机按钮复刻照片镜头，↶ 还原第一次应用前的姿势，磁盘按钮导出 `.pose`。

导入超过 8 张图片时，需同时更新后端。后端最多在内存中保留 8 份识别会话，其余结果暂存到临时磁盘文件，
方便稍后选中时继续加载。更换导入批次或卸载插件时，会清理对应的缓存会话和临时文件。

多图识别默认按顺序执行（并行数 **1**）。在「设置 → 识别后端 → 多图识别并行数」的下拉框中可选择 **1～8**，
修改后从下一批图片生效。设置大于 **1** 时，需要更新并重启后端；运行中的后端无法确认支持时，插件会报错，
不会开始该批次识别。默认 **1** 仍兼容旧后端。
并行识别使用隔离的工作进程，每个进程加载完整模型，内存和显存需求随并行数增加。
下拉框右侧显示模型资源预算：每个 GPU 工作进程约需 **3 GB 显存**（MHR 也放在 GPU 时再加约 **0.75 GB**），
每个工作进程约需 **4 GB 内存**。CPU 识别不计推理显存；「自动」档显示 GPU 方案，实际可能回退到 CPU。
实际额外占用取决于模式和图片；估算不含游戏、图片与会话缓存及残留 GPU 上下文。
更高并行数不保证提速；当前并行模式的性能和资源占用尚未完成实机验证。
「停止预识别」会让已经开始的图片识别完成，并跳过尚未开始的图片。

状态栏图标从左到右：Brio、GPose、IVCS（读到游戏姿势后显示）、后端、显存、内存，鼠标悬停看详情。

### 对话调姿

1. 在「设置 → 提示词动作」填写 API 端点和密钥，点击「拉取模型」，再从模型名下拉框选择；选择后自动保存。
   端点可填兼容 OpenAI 的基础地址（如 `https://your-provider.example/v1`）或完整的 `/chat/completions` 地址。
   无需认证的本地服务可留空密钥。拉取模型由插件直接访问服务，不需要启动本地后端。
2. 启动本地后端，打开「对话」页。已经导入图像时，基于当前识别姿势调整；没有图像时，可从当前角色姿势开始。
   要从零生成，点击「从默认站姿开始」。
3. 输入「左手抬到肩膀高度，头看向右边」，再接着说「再高一点，另一只手放松」。每轮都会结合当前骨架和最近的对话。
4. 点「生成预览」，在「3D」和「提示」页检查结果，再点 **应用** 更新角色，或导出 `.pose` 文件。
   聊天回复只更新预览，应用到游戏需要手动操作。

在 GPose 中选中角色后，对话会读取该角色的完整骨架、父子层级和默认局部变换，包括实际加载的面部、头发和附属骨骼。
例如「保持其他部分不变，让眼睛睁开」会仅恢复眼睑，不改动身体、视线和其他表情。图像预览缺失的面部数据会从当前角色补齐。
没有加载角色骨架时，模型会收到明确的可用能力范围，不会把缺失的控制数据当成骨骼不存在。

**不强制使用多模态模型**：本地后端向模型提供结构化骨架信息，并校验返回的骨骼操作。
需要模型查看参考图时，在「对话」页开启「发送参考图」并使用支持图像输入的模型。
发送前会压缩为 JPEG，最长边不超过 1024 像素、文件不超过 256 KB；本地原图和预览不变。
关闭时原图留在本机；提示词、最近对话和骨架数据
会发送给你配置的服务。原有的图像姿态识别仍由本地 SAM 3D Body 完成。

密钥在设置中以掩码显示，并通过 Windows 当前用户加密后保存在插件配置中；后端按次使用凭据，不保存密钥。

「新对话」清空聊天记录并保留当前姿势；「从默认站姿开始」同时重置姿势和历史。
从站姿开始后，后续轮次也不会再附带旧参考图；重新导入图像或「返回图像姿势」后才能再次使用。
导入其他图片或切换角色也会清理上下文，避免沿用另一套姿势的指令。
图像调参和复刻照片镜头针对识别姿势，使用时先点「返回图像姿势」。
「取消生成」会丢弃等待中的回复，保留当前姿势；服务商可能仍会完成该次请求。

### AI 姿势建议

1. 按「对话调姿」中的步骤配置模型并启动本地后端，然后打开「提示」页。
2. 可填写关注点，例如「保持左手贴近头部，重点检查肩颈是否自然」。点击 **AI 分析当前姿势** 后，才会向配置的模型服务发送分析请求。
   打开选项卡、拖动滑杆和刷新规则提示均不会自动调用模型；没有规则警告时也可以分析。
3. 模型会收到当前预览姿势（没有预览时读取选中的角色）、种族、实际加载的骨架层级、骨骼名称与身体部位的对应、
   坐标方向、关节变换，以及手脚与头、胸、髋等部位的测量关系。已有规则提示和适用的图像调参值也会一并提供。
4. 阅读问题、依据和调整建议。建议关联到可定位的关节时，可在「3D」页查看；也可以把建议转入「对话」输入框，
   再手动点击「生成预览」并检查结果。分析本身不会改变预览、角色姿势或对话记录。

AI 建议的「发送参考图」需单独勾选，不沿用对话页的发送开关；默认关闭。开启时需要支持图像输入的模型，
发送的是压缩后的导入参考图，**不是游戏角色截图**。骨架与参考图不足以确认衣服穿模、蒙皮变形或实际接触。

建议只对应发起分析时的姿势。切换角色、图片、种族或更改姿势后，旧建议会失效，需要重新点击分析。
可以取消等待中的分析；模型服务商可能仍会完成已经收到的请求。

### 设置

| 设置 | 默认 | 说明 |
|---|---|---|
| 语言 | 自动 | 游戏客户端是中文就用中文，否则看 Dalamud 界面语言，都不是中文则用英文 |
| 识别设备 | 自动 | 可用显存约 3GB 时用 GPU，否则用 CPU。「GPU」强制用显卡，「CPU」完全不占显存 |
| 模型释放 | 识别后立即释放 | 显存/内存归还给游戏，下次识别重新加载（约 5~10 秒） |
| GPU 模式下 MHR 放 CPU | 开 | 显存约省 750MB，每张约慢 0.5 秒 |
| 松开滑杆时自动应用 | 开 | 关掉后需要手动点应用 |
| 聊天 API 端点 / 密钥 / 模型名 | 空 | 填写端点和密钥后拉取模型，从下拉框选择并自动保存；无需认证可留空密钥 |
| 发送参考图 | 关 | 需要已导入图像和支持图像输入的模型；开启后向配置的服务发送预览图 |

### 种族

| 骨架组 | 种族 | 状态 |
|---|---|---|
| 中原系男 | 人族中原、高地、猫魅、敖龙、维埃拉 | 已在游戏内实测 |
| 精灵男 | 精灵 | 已在游戏内实测 |
| 鲁加系男 | 鲁加、硌狮 | 0.3.2 已修复，待游戏内复测 |
| 拉拉菲尔 | 拉拉菲尔男 / 女 | 0.3.2 已修复，待游戏内复测 |
| 女性骨架 | 全部 | **尚未适配** |

识别出的种族不对时（换种族 MOD、Customize+ 改体型），在角色列表旁的种族下拉框里手动指定。
手动指定按角色分别记住（本次 GPose 内）；在角色之间切换不会清除它们在 Brio 里的姿势。

骨架数据来自 IVCS。角色没有加载 IVCS 骨骼时也能用：手指第三节的弯曲会合并到第二节，状态栏 IVCS 图标显示黄色。

## 已知限制

- 单张图片的前后深度有歧义（比如手在头前还是头后）。
- 还没有碰撞避让：拉拉菲尔举起的手臂可能穿过头，鲁加 / 硌狮高抬腿时大腿可能插进肚子。
- 肘部极度弯曲时褶皱比游戏动画明显（约 145°，游戏动画约 130°）。
- 跳跃、腾空的图会被按到地面上，需要关掉「自动贴地」。
- 多人和视频在界面上预留了入口，尚未实现；目前整张图按一个人处理。
- CPU 模式速度很依赖 CPU（20 线程笔记本 CPU 实测约 10 秒/张）。
- 对话调姿的准确度取决于所配置的模型。骨骼操作校验会拒绝格式错误的结果，但不保证人体动作、接触和穿模都正确，应用前请检查预览。
- 对话生成的是静态姿势，不是连续动画；纯文字生成的姿势无法复刻照片镜头。

## 从源码构建

### 插件

```powershell
cd Posegraph
dotnet build -c Release      # 产物：Posegraph\bin\Release\Posegraph\latest.zip
```

项目使用 `Dalamud.NET.SDK` 15 与 `Brio.API`。国服 XIVLauncherCN 的 Dalamud 路径会自动识别。

### 后端（开发安装）

```powershell
cd backend
powershell -ExecutionPolicy Bypass -File scripts\setup_sam3d.ps1     # 虚拟环境、CUDA 版 PyTorch、上游源码、权重（SHA256 校验）
.venv\Scripts\python -m posegraph.app                               # http://127.0.0.1:42430
```

权重默认从魔搭 ModelScope 下载（免登录）；`-Source hf` 走 Hugging Face 官方仓库（需申请访问）。
环境变量（前缀 `POSEGRAPH_`）：`DEVICE`（auto|cuda|cpu）、`PRECISION`（auto|bf16|fp16|fp32）、`MHR_ON_CPU`、
`MIN_FREE_VRAM_MB`、`RELEASE_AFTER`（秒，0 立即、-1 常驻）、`RECOGNITION_PARALLELISM`（1～8，默认 1）、`PRELOAD`、`SAVE_SESSIONS`（每次识别另存 npz 到 `outputs/ui`）、`PORT`、`BRIDGE_URL`、`SAM3D_REPO`、`SAM3D_CKPT`。

只使用**纯文字对话调姿**时，可用 Python 3.11 仅安装后端基础依赖：

```powershell
cd backend
py -3.11 -m venv .venv
.venv\Scripts\python -m pip install -e .
.venv\Scripts\python -m posegraph.app
```

在插件的后端高级设置中，将 Python 指向该环境的 `python.exe`，backend 目录指向本仓库的 `backend` 文件夹，关闭模型预加载。
需要图像识别时，再按上面的完整流程安装。

### 回归检查

安装后端基础依赖和 .NET SDK 后，在仓库根目录运行：

```powershell
backend\.venv\Scripts\python -m unittest discover -s backend\tests -v
Get-ChildItem tests -Recurse -Filter *.csproj | ForEach-Object {
    dotnet run --project $_.FullName -c Release
    if ($LASTEXITCODE -ne 0) { throw "Check failed: $($_.Name)" }
}
dotnet build Posegraph\Posegraph.csproj -c Release
```

插件构建还需本机的 Dalamud 开发库。检查通过合成姿势、本地 HTTP 服务和隔离测试进程覆盖生产源码，
无需付费模型 API 或启动游戏。SAM 3D Body 的真实并行推理、显存峰值、原生角色骨架读取和游戏内剪切板 / Brio 行为仍需另做实机验证。

### 后端一键包

```powershell
powershell -ExecutionPolicy Bypass -File packaging\build_backend_package.ps1 `
    -Python C:\Path\To\Python311 -Venv backend\.venv `
    -Sam3d backend\third_party\sam-3d-body -Weights backend\checkpoints\sam-3d-body-vith `
    -Out D:\dist -Zip
```

脚本把一份普通的 CPython 3.11 安装复制成便携解释器，再加上虚拟环境的 site-packages、后端源码、SAM 3D Body 源码与权重、许可证文件。

### 开发工具（`backend/scripts`，输出为中文）

| 脚本 | 用途 |
|---|---|
| `photo_to_game.py`、`image_to_pose.py`、`push_pose.py`、`preview_overlay.py` | 不用插件界面的命令行流程 |
| `verify_devices.py`、`bench_sam3d.py` | 比对 CPU/GPU 输出、测显存与速度（见 `docs/benchmark.md`） |
| `build_retarget_data.py` | 从 IVCS `.sklb` 重新生成骨架数据 |
| `sample_frames.py` | 采样游戏内动画帧（用于拟合辅助骨规则） |
| `export_race_compare.py`、`blender_race_compare.py`、`build_body_extents.py` | Blender 多种族对比场景与身体尺寸表 |

## HTTP 接口

插件自带一个本地服务（127.0.0.1:42429），供后端复刻镜头和命令行工具使用：`GET /status`、`GET /actors`、
`GET|POST /pose?actor=N`、`GET /rig?actor=N`、`GET|POST /camera`。后端（127.0.0.1:42430）提供 `GET /health`、`POST /sessions`、
`GET /sessions/{id}/image`、`DELETE /sessions/{id}`、`POST /sessions/{id}/retarget`、`POST /sessions/{id}/camera`、`POST /model/load|unload|config`、
`GET /races`、`GET /presets`、`POST /chat/pose`、`POST /chat/advice`、`POST /shutdown`。两者都只监听本机。

`POST /model/config` 接收 `recognition_parallelism`（1～8 的整数），在 `model.config.recognitionParallelism` 中返回实际配置。
修改时等待正在运行的推理结束，释放旧 worker 后生效。

`POST /chat/pose` 接收 `prompt`、`history`（`user` / `assistant` 消息）、当前 Brio `pose`（可选）、`race`、
`ground`、可选的 `session_id` / `include_image`、已加载骨架 `rig` 和 `actor_pose`，以及 `provider`（`endpoint`、`api_key`、`model`）。
返回 `reply`、`history` 和与重定向接口相同格式的姿势 / 预览 `result`。调用方保存历史，在下一轮连同当前姿势一起传回；
纯文字请求无需图像会话，也不会加载识别模型。

`POST /chat/advice` 接收当前 `pose`、`race`、可选的 `rig`、`actor_pose`、`params`、`warnings`、`focus`、
可选的 `session_id` / `include_image` 和相同的 `provider`，返回结构化观察与建议，不编辑或应用姿势。
两个聊天接口都不会在服务端持久化访问凭据和对话历史。

## 许可证

Posegraph 自身的代码（插件、后端、脚本）以 [MIT 许可证](LICENSE) 授权（0.3.8 及之前的版本为 PolyForm Noncommercial License 1.0.0）。
第三方组件仍按各自的许可证，其中后端一键包附带的 SAM 3D Body 代码与权重为 Meta 的 SAM License。

## 致谢

见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。SAM 3D Body（代码与权重）© Meta，以 SAM License 发布；
FF14 骨架数据提取自 Illusio Vitae Custom Skeletons。

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. Posegraph 为玩家自制工具，与 Square Enix 无关，也未获其认可。
