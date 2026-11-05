"""Fetch original VALORANT agent art from Valorant-API and prepare local UI assets."""
from concurrent.futures import ThreadPoolExecutor, as_completed
from io import BytesIO
import json
import re
from pathlib import Path
from urllib.parse import urlparse
from urllib.request import Request, urlopen
from PIL import ImageEnhance

from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "src" / "ValoAutoPick" / "Assets" / "Agents"
DATA_URL = "https://valorant-api.com/v1/agents?isPlayableCharacter=true&language=ja-JP"
USER_AGENT = "ValoAutoPick/1.0 (Windows agent-picker desktop application)"


def get(url: str) -> bytes:
    parsed = urlparse(url)
    if parsed.scheme != "https" or parsed.hostname not in {"valorant-api.com", "media.valorant-api.com"}:
        raise ValueError(f"Unexpected art host: {parsed.hostname}")
    request = Request(url, headers={"User-Agent": USER_AGENT})
    with urlopen(request, timeout=30) as response:
        if response.status != 200:
            raise ValueError(f"Art download failed: HTTP {response.status}")
        return response.read()


def fit(image: Image.Image, size: tuple[int, int], mode: str) -> Image.Image:
    image = ImageOps.exif_transpose(image)
    if mode == "cover":
        return ImageOps.fit(image, size, method=Image.Resampling.LANCZOS, centering=(0.5, 0.45))
    image.thumbnail(size, Image.Resampling.LANCZOS)
    return image


def render(blob: bytes, size: tuple[int, int], mode: str, image_format: str, agent: dict) -> bytes:
    with Image.open(BytesIO(blob)) as source:
        if image_format == "JPEG":
            rgba = ImageOps.exif_transpose(source).convert("RGBA")
            base = Image.new("RGBA", rgba.size, (18, 24, 34, 255))
            base.alpha_composite(rgba)
            dark = ImageEnhance.Brightness(base.convert("RGB")).enhance(.27)
            colors = agent.get("backgroundGradientColors") or ["1a2433"]
            color = "#" + re.sub("[^0-9a-fA-F]", "", colors[0])[:6]
            image = fit(Image.blend(dark, Image.new("RGB", dark.size, color), .15), size, mode)
        else:
            image = fit(source.convert("RGBA"), size, mode)
        output = BytesIO()
        if image_format == "JPEG":
            image.save(output, "JPEG", quality=88, optimize=True, progressive=True, subsampling=0)
        else:
            image.save(output, "PNG", optimize=True, compress_level=9)
        return output.getvalue()


def main() -> None:
    data = json.loads(get(DATA_URL))
    agents = [agent for agent in data["data"] if agent.get("isPlayableCharacter") and agent.get("fullPortrait") and agent.get("displayIcon")]
    if len(agents) < 10:
        raise ValueError("Agent catalog looks incomplete; no files were changed.")
    if len({agent["uuid"] for agent in agents}) != len(agents):
        raise ValueError("Duplicate agent ID; no files were changed.")
    pending: dict[Path, bytes] = {}
    with ThreadPoolExecutor(max_workers=6) as pool:
        tasks = {}
        for agent in agents:
            agent_id = agent["uuid"]
            if not re.fullmatch(r"[0-9a-fA-F-]{36}", agent_id):
                raise ValueError("Invalid agent ID; no files were changed.")
            tasks[pool.submit(get, agent["displayIcon"])] = (agent_id, "icon", agent)
            tasks[pool.submit(get, agent["fullPortrait"])] = (agent_id, "portrait", agent)
            if agent.get("background"):
                tasks[pool.submit(get, agent["background"])] = (agent_id, "background", agent)
        for future in as_completed(tasks):
            agent_id, kind, agent = tasks[future]
            blob = future.result()
            if kind == "icon": result = render(blob, (144, 144), "cover", "PNG", agent)
            elif kind == "portrait": result = render(blob, (640, 780), "contain", "PNG", agent)
            else: result = render(blob, (1280, 720), "cover", "JPEG", agent)
            extension = ".jpg" if kind == "background" else ".png"
            pending[ASSET_DIR / f"{agent_id}-{kind}{extension}"] = result

    catalog = []
    for agent in agents:
        role = agent.get("role") or {}
        catalog.append({"Id": agent["uuid"], "Name": agent["displayName"], "Role": role.get("displayName") or "",
                        "RoleIconId": role.get("uuid") or ""})
    catalog_bytes = json.dumps(catalog, ensure_ascii=False, indent=2).encode("utf-8")
    # Stage every validated image before updating the catalog, so interrupted downloads never create a partial catalog.
    ASSET_DIR.mkdir(parents=True, exist_ok=True)
    staged = []
    try:
        for path, content in pending.items():
            temporary = path.with_suffix(path.suffix + ".tmp")
            temporary.write_bytes(content)
            staged.append((temporary, path))
        for temporary, path in staged:
            temporary.replace(path)
        catalog_path = ROOT / "src" / "ValoAutoPick" / "agents.json"
        catalog_tmp = catalog_path.with_suffix(".json.tmp")
        catalog_tmp.write_bytes(catalog_bytes)
        catalog_tmp.replace(catalog_path)
    except BaseException:
        for temporary, _ in staged:
            temporary.unlink(missing_ok=True)
        raise
    print(f"Saved {len(agents)} agents and {len(pending)} image assets ({sum(map(len, pending.values())) / 1_000_000:.1f} MB).")


if __name__ == "__main__":
    main()
