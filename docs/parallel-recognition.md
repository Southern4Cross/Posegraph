# 并行识别（实验）/ Parallel recognition (experimental)

共享模型实现来自 [PR #2](https://github.com/extrant/Posegraph/pull/2)（uiharuayako），合并时改成了可以随时关掉、出问题不影响识别的形式：

- **只用于图库批量识别**：插件设置「多图识别并行数」大于 1 时，批量队列的请求带 `parallel=true`；单张识别、辅助视角、预加载都走原来的串行路径。
- **出问题交回串行**：共享模型加载失败、上游源码指纹不符、运行时校验不通过时，后端本进程内停用并行，这一张改用串行重试，
  `/health` 的 `model.parallel.disabled` 写明原因，插件提示一次；单张请求出错也交回串行重试，不让识别失败。
- **按余量定路数**：共享模型加载后，GPU 按剩余显存（留 1GB，每路按 250MB 算），CPU 按可用内存（留 3GB，每路按 450MB 算）
  和核数（每路至少 2 线程）削减路数；CPU 模式下每路 PyTorch 线程数 = 核数 ÷ 路数，并行结束后恢复。
- **内存里只有一份模型**：换用共享模型前卸掉串行模型；批量并行进行中夹进来的单张也交给共享模型。
- **开关**：插件里设回 1；或后端环境变量 `POSEGRAPH_DISABLE_PARALLEL=1` 完全关掉。

代码隔离：后端 `parallel_batch.py`（调度、降级、预算）+ `shared_inference.py` / `estimators/shared_sam3d.py`（PR 原实现）；
插件 `Library/RecognitionService.Parallel.cs`、`Backend/BackendClient.Parallel.cs`、`UI/MainWindow.RecognitionSettings.cs`。
要彻底去掉：删掉这些文件，以及 `RecognitionService.WorkAsync` 里调用 `DrainParallelAsync` 的一行、`app.py` 里 `_parallel` 的几处调用。

Batch recognition only (requests flagged `parallel=true`); single images stay serial. Any shared-model failure disables parallel
recognition for the backend process and retries the image serially. Slots are limited by free VRAM / RAM and cores; CPU threads are
split across slots. Turn it off with concurrency 1 or `POSEGRAPH_DISABLE_PARALLEL=1`.

## 合并后实测 / Measurements after integration

RTX 4060 Laptop 8GB / i7 20 线程，图库实拍图，含模型加载时间：

| 场景 | 串行 | 并行 | 结果差异 |
|---|---:|---:|---|
| GPU，8 张，并行 3 | 28.8 秒，显存 +1924MB | 18.5 秒，显存 +2255MB | 逐元素一致 |
| GPU，指纹不符（降级） | 25.1 秒（6 张） | 19.3 秒，全部成功，显存 +1977MB | 逐元素一致 |
| CPU，4 张，并行 4 | 70.7 秒 | 53.4 秒 | ≤0.34mm（与串行两次加载之间的浮点差同量级） |

## PR #2 作者实测 / Measurements by the PR author

CPU i7-13700KF / Torch 2.11.0+cpu / fp32；GPU RTX 4070 Ti 12 GB / Torch 2.11.0+cu128 / bf16，MHR 在 CPU。
N1 / 3～8 来自文末固定实验快照 `6524422`；N2 在精简集成版按相同协议补测。
两版的 `shared_sam3d.py` / `sam3d_body.py` 未改；N2 不代表其余档位已在精简版重复测量。
三种原图 / flip / crop 各 8 次，共 24 请求；warmup2、intraop4 / interop1，每档一次独立进程样本，排除加载与预热。
增幅统一相对同设备本轮共享 N1，单位 GiB；不混入旧 6 / 18 请求数据，也不混用原串行封装作为共享 N1 基准。

### CPU

| N | 24 请求秒 | images/s | 吞吐增加 | 耗时减少 | 末 USS GiB（增加） |
|---|---:|---:|---:|---:|---:|
| 1 | 294.805 | 0.081410 | 基准 | 基准 | 4.488 |
| 2 | 219.438 | 0.109370 | 34.35% | 25.57% | 4.653（3.67%） |
| 3 | 177.197 | 0.135443 | 66.37% | 39.89% | 4.800（6.94%） |
| 4（数值未过） | 154.145 | 0.155697 | 91.25% | 47.71% | 4.875（8.60%） |
| 5 | 146.263 | 0.164088 | 101.56% | 50.39% | 4.948（10.24%） |
| 6 | 142.676 | 0.168214 | 106.63% | 51.60% | 5.052（12.56%） |
| 7 | 167.604 | 0.143194 | 75.89% | 43.15% | 5.134（14.38%） |
| 8 | 164.471 | 0.145922 | 79.24% | 44.21% | 5.225（16.42%） |

### GPU（独立 CUDA streams）

| N | 24 请求秒 | images/s | 吞吐增加 | 耗时减少 | 末 USS GiB（增加） |
|---|---:|---:|---:|---:|---:|
| 1 | 34.941 | 0.686868 | 基准 | 基准 | 2.932 |
| 2 | 30.764 | 0.780132 | 13.58% | 11.95% | 2.933（0.04%） |
| 3 | 25.303 | 0.948507 | 38.09% | 27.58% | 2.949（0.58%） |
| 4 | 24.665 | 0.973037 | 41.66% | 29.41% | 2.972（1.39%） |
| 5 | 24.576 | 0.976545 | 42.17% | 29.66% | 2.993（2.07%） |
| 6 | 26.199 | 0.916069 | 33.37% | 25.02% | 2.996（2.20%） |
| 7 | 26.713 | 0.898428 | 30.80% | 23.55% | 3.007（2.58%） |
| 8 | 25.774 | 0.931172 | 35.57% | 26.24% | 3.001（2.38%） |

| N | GPU peak allocated GiB（增加） | peak reserved GiB（增加） |
|---|---:|---:|
| 1 | 1.692 | 1.848 |
| 2 | 1.778（5.03%） | 1.959（6.03%） |
| 3 | 1.845（9.02%） | 2.070（12.05%） |
| 4 | 1.895（11.96%） | 2.182（18.08%） |
| 5 | 1.962（15.90%） | 2.293（24.10%） |
| 6 | 2.024（19.58%） | 2.404（30.13%） |
| 7 | 2.097（23.89%） | 2.512（35.94%） |
| 8 | 2.179（28.73%） | 2.617（41.65%） |

16 档实际调用并发峰均达到 N，加载次数 / 模型副本均为 1；每 view 的注册 Tensor 全共享，唯一权重 storage 不随 N 增长。
每档比较 24×9=216 数组；GPU 八档与 CPU N1/2/3/5/6/7/8 逐元素一致。
**CPU N4 未满足本轮 atol=rtol=1e-4 判据：208/216 通过，8 个旋转矩阵数组最大元素差 7.338e-4，待定位。**
CPU N4 failed the strict numerical comparison; the results do not establish equivalence for every configuration.
当前精简集成版真实 HTTP 已通过：24 请求 / 8 并发 / 1 模型，`hold=true` 时 `release_after=0` 保留模型，归档八项字段与 GPU 基准逐元素一致。
辅助视角 `set_views` 溢出重载持久化与清空、会话删除、显式卸载及切回并行 1 的原串行 `hold=false` 即时卸载均通过，清理错误为零。

单次样本中 CPU N6 吞吐最高，GPU N4 / N5 接近，不作为普适推荐；更多并发没有持续改善速度，且单次延迟会增加。
N5 / N7 有尾任务；warmup2 在 N1/2/3/4/5/6/7/8 分别调用 6/6/6/8/10/12/14/16 次，并非每线程每输入两遍。
峰在途 N 不代表持续满载或 kernel 全程重叠。USS 为结束阶段快照；allocator 是 warm 单进程峰，均不是整卡 / 整机容量承诺。
每档仅一次、顺序未随机化；原图变体范围有限，未做游戏内 / Brio / 与游戏争用 GPU 的验证。
Single-run batch measurements are not steady-state capacity or a universal concurrency recommendation; in-game behavior remains untested.

## 可复现实验快照 / Reproducible experiment snapshot

多进程对照、完整基准和长报告保留在固定提交 `6524422`，不作为主功能依赖：
[完整基准脚本](https://github.com/uiharuayako/Posegraph/blob/652442252d769ecbb123dc3f22bb4c6cbed72765/backend/scripts/bench_shared_model.py) ·
[完整实验报告](https://github.com/uiharuayako/Posegraph/blob/652442252d769ecbb123dc3f22bb4c6cbed72765/docs/shared-model-experiment.md)。
本机原始输入、JSON / NPZ / HTTP 日志保留在忽略的 `backend/outputs/`，不随代码提交。
