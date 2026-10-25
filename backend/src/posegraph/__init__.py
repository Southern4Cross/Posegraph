"""posegraph：图片 → 3D 人体姿态 → FFXIV Brio 姿势。

version.json：version 为后端版本（与插件版本一致，同一个 git 标签发布）；
runtime 为所需的最低"运行环境版本"（一键包里 Python 与依赖的版本，见一键包根目录 runtime.json）。
依赖有变化（新增包、升级 PyTorch 等）时把 runtime 加一，插件会提示用户重新下载完整一键包，而不是只替换源码。
"""

import json as _json
from pathlib import Path as _Path

_info = _json.loads((_Path(__file__).with_name("version.json")).read_text(encoding="utf-8"))
__version__: str = _info["version"]
RUNTIME_REQUIRED: int = _info["runtime"]
