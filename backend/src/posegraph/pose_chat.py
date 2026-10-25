"""OpenAI-compatible chat transport. Credentials are used for one request only.

The provider proposes a small JSON edit plan; local prompt_pose code validates
and executes it. Neither Python code nor provider-supplied Brio JSON is run.
"""

from __future__ import annotations

import base64
import io
import json
from typing import Literal
from urllib.parse import urlsplit, urlunsplit

import httpx
from fastapi import HTTPException
from pydantic import BaseModel, ConfigDict, Field, field_validator
from starlette.concurrency import run_in_threadpool

from .i18n import tr

MAX_HISTORY = 20
MAX_PROVIDER_BYTES = 1_048_576
REFERENCE_MAX_SIDE = 1024
REFERENCE_MAX_BYTES = 256 * 1024


def compress_reference_image(preview_png: bytes) -> bytes:
    """Create a bounded JPEG for the provider; leave the local preview intact."""
    from PIL import Image

    try:
        with Image.open(io.BytesIO(preview_png)) as source:
            if source.mode in ("RGBA", "LA", "P"):
                rgba = source.convert("RGBA")
                image = Image.new("RGB", rgba.size, "white")
                image.paste(rgba, mask=rgba.getchannel("A"))
            else:
                image = source.convert("RGB")
        image.thumbnail((REFERENCE_MAX_SIDE, REFERENCE_MAX_SIDE), Image.Resampling.LANCZOS)
        while True:
            for quality in (82, 72, 62):
                output = io.BytesIO()
                image.save(output, format="JPEG", quality=quality, optimize=True)
                encoded = output.getvalue()
                if len(encoded) <= REFERENCE_MAX_BYTES:
                    return encoded
            # Detailed/noisy images may still exceed the budget at lower quality.
            # Keep the aspect ratio and reduce resolution until the budget fits.
            image = image.resize((max(1, round(image.width * 0.8)), max(1, round(image.height * 0.8))),
                                 Image.Resampling.LANCZOS)
    except (OSError, ValueError, Image.DecompressionBombError):
        raise HTTPException(400, tr("无法处理参考图，请重新导入图像")) from None


class ChatMessage(BaseModel):
    model_config = ConfigDict(extra="forbid")
    role: Literal["user", "assistant"]
    content: str = Field(min_length=1, max_length=12000)


class ChatProvider(BaseModel):
    model_config = ConfigDict(extra="forbid")
    endpoint: str = Field(min_length=1, max_length=2048)
    api_key: str = Field(default="", max_length=8192, repr=False)
    model: str = Field(min_length=1, max_length=256)

    @field_validator("endpoint", "model")
    @classmethod
    def nonblank(cls, value: str) -> str:
        value = value.strip()
        if not value or any(ord(c) < 32 for c in value):
            raise ValueError("A non-empty endpoint/model without control characters is required")
        return value

    @field_validator("api_key")
    @classmethod
    def header_safe(cls, value: str) -> str:
        if any(ord(c) < 32 or ord(c) > 126 for c in value):
            raise ValueError("API key must contain printable ASCII characters only")
        return value.strip()


class RigBone(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: str = Field(min_length=1, max_length=128)
    parent: str | None = Field(default=None, max_length=128)
    reference_position: list[float] = Field(min_length=3, max_length=3)
    reference_rotation: list[float] = Field(min_length=4, max_length=4)
    reference_scale: list[float] = Field(min_length=3, max_length=3)


class ActorRig(BaseModel):
    model_config = ConfigDict(extra="forbid")
    bones: list[RigBone] = Field(min_length=1, max_length=2048)


class PoseChatRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    prompt: str = Field(min_length=1, max_length=4000)
    history: list[ChatMessage] = Field(default_factory=list, max_length=MAX_HISTORY)
    pose: str | None = Field(default=None, max_length=2_000_000)
    actor_pose: str | None = Field(default=None, max_length=2_000_000)
    rig: ActorRig | None = None
    race: str | None = Field(default=None, max_length=16)
    session_id: str | None = Field(default=None, max_length=64)
    include_image: bool = False
    ground: bool = True
    provider: ChatProvider

    @field_validator("prompt")
    @classmethod
    def nonblank(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("Prompt cannot be blank")
        return value.strip()


def completion_url(endpoint: str) -> str:
    """Accept a service root, a versioned base URL, or a full completion URL."""
    try:
        if any(c.isspace() or ord(c) < 32 for c in endpoint.strip()):
            raise ValueError()
        parts = urlsplit(endpoint.strip())
        if (parts.scheme not in ("https", "http") or not parts.hostname or
                parts.username is not None or parts.password is not None or
                parts.query or parts.fragment):
            raise ValueError()
        # Validate the port, too (urlsplit defers that check).
        _ = parts.port
    except ValueError:
        raise HTTPException(400, tr("API 端点须为 HTTP(S) 地址，且不能包含凭据、查询参数或片段")) from None
    path = parts.path.rstrip("/")
    if path.endswith("/responses") or path.endswith("/messages"):
        raise HTTPException(400, tr("请配置兼容 Chat Completions 的端点（基础地址或 /chat/completions）"))
    if not path:
        path = "/v1"
    if not path.endswith("/chat/completions"):
        path += "/chat/completions"
    return urlunsplit((parts.scheme, parts.netloc, path, "", ""))


def parse_plan(content: str) -> tuple[str, list[dict]]:
    content = content.strip()
    if content.startswith("```"):
        lines = content.splitlines()
        if len(lines) >= 3 and lines[-1].strip() == "```":
            content = "\n".join(lines[1:-1])
    try:
        plan = json.loads(content)
        if not isinstance(plan, dict) or set(plan) != {"reply", "edits"}:
            raise ValueError()
        reply, edits = plan["reply"], plan["edits"]
        if (not isinstance(reply, str) or not reply.strip() or len(reply) > 8000 or
                not isinstance(edits, list) or len(edits) > 64 or
                any(not isinstance(edit, dict) for edit in edits)):
            raise ValueError()
        return reply.strip(), edits
    except (ValueError, TypeError, RecursionError):
        raise HTTPException(502, tr("模型未返回有效的动作 JSON；请重试或更换支持指令的模型")) from None


async def request_plan(req: PoseChatRequest, context: dict, system_prompt: str,
                       preview_png: bytes | None = None) -> tuple[str, list[dict]]:
    current = ("Current pose context (authoritative; prior edits are already applied):\n" +
               json.dumps(context, ensure_ascii=False, allow_nan=False, separators=(",", ":")) +
               "\nUser instruction:\n" + req.prompt)
    content = await reference_content(current, req.include_image, preview_png)
    messages = [{"role": "system", "content": system_prompt}]
    messages.extend(m.model_dump() for m in req.history)
    messages.append({"role": "user", "content": content})
    return parse_plan(await request_completion(req.provider, messages))


async def reference_content(current: str, include_image: bool, preview_png: bytes | None = None) -> str | list[dict]:
    """Attach only the explicitly selected original reference, within chat's budget."""
    content: str | list[dict] = current
    if include_image:
        if not preview_png:
            raise HTTPException(400, tr("发送参考图需要先导入并识别一张图片"))
        compressed = await run_in_threadpool(compress_reference_image, preview_png)
        content = [{"type": "text", "text": current},
                   {"type": "image_url", "image_url": {
                       "url": "data:image/jpeg;base64," + base64.b64encode(compressed).decode("ascii")}}]
    return content


async def request_completion(provider: ChatProvider, messages: list[dict],
                             invalid_response: str = "模型未返回有效的动作 JSON；请重试或更换支持指令的模型") -> str:
    """Shared bounded transport; callers separately validate their response contract."""
    url = completion_url(provider.endpoint)
    # Do not require tool calling, JSON mode, temperature, or vendor-specific
    # token parameters: many local/compatible models implement only basic chat.
    body = {"model": provider.model, "messages": messages, "stream": False}
    headers = {"Accept": "application/json"}
    if provider.api_key:
        headers["Authorization"] = "Bearer " + provider.api_key
    try:
        # Never follow a redirect with the user's credentials or image attached.
        async with httpx.AsyncClient(timeout=httpx.Timeout(90, connect=10), follow_redirects=False) as client:
            async with client.stream("POST", url, json=body, headers=headers) as response:
                if response.status_code in (401, 403):
                    raise HTTPException(502, tr("模型服务认证失败；请检查 API 密钥和模型访问权限"))
                if response.status_code == 429:
                    raise HTTPException(502, tr("模型服务限流或额度不足；请稍后重试并检查额度"))
                if not response.is_success:
                    # Upstream error bodies can echo keys, prompts or images.
                    raise HTTPException(502, tr("模型服务返回 HTTP {status}；请检查端点、模型名及图像支持",
                                                status=response.status_code))
                data = bytearray()
                async for chunk in response.aiter_bytes():
                    data.extend(chunk)
                    if len(data) > MAX_PROVIDER_BYTES:
                        raise HTTPException(502, tr("模型响应过大，请缩小本次动作调整范围"))
    except httpx.TimeoutException:
        raise HTTPException(504, tr("模型请求超时；请稍后重试或使用响应更快的模型")) from None
    except httpx.InvalidURL:
        raise HTTPException(400, tr("API 端点须为 HTTP(S) 地址，且不能包含凭据、查询参数或片段")) from None
    except httpx.HTTPError:
        raise HTTPException(502, tr("无法连接模型服务；请检查 API 端点和网络")) from None
    try:
        response_body = json.loads(data)
        choice = response_body["choices"][0]
        if not isinstance(choice, dict):
            raise ValueError()
        if choice.get("finish_reason") == "length":
            raise HTTPException(502, tr("模型响应被截断，请缩小本次动作调整范围"))
        answer = choice["message"]["content"]
        if not isinstance(answer, str):
            raise ValueError()
    except (ValueError, KeyError, IndexError, TypeError, RecursionError):
        raise HTTPException(502, tr(invalid_response)) from None
    return answer
