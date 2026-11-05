"""Read-only APK inspection. Does not execute any supplied code."""
import hashlib
import json
import re
import struct
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parent.parent
apk = root / "ValBuddy-2.3.0.apk"
with apk.open("rb") as stream:
    digest = hashlib.file_digest(stream, "sha256").hexdigest()
with zipfile.ZipFile(apk) as archive:
    config = json.loads(archive.read("assets/app.config"))
    bundle = archive.read("assets/index.android.bundle")
    strings = "\n".join(s.decode("ascii") for s in re.findall(rb"[ -~]{5,}", bundle))
    (root / "analysis" / "bundle-strings.txt").write_text(strings, encoding="utf-8")
    (root / "analysis" / "app.config.json").write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "sha256": digest,
        "bytes": apk.stat().st_size,
        "name": config["name"],
        "version": config["version"],
        "android": config["android"]["package"],
        "expo_sdk": config["sdkVersion"],
        "dex_count": sum(bool(re.fullmatch(r"classes\d*\.dex", n)) for n in archive.namelist()),
        "bundle_bytes": len(bundle),
        "hermes_magic": hex(struct.unpack_from("<Q", bundle)[0]),
        "hermes_version": struct.unpack_from("<I", bundle, 8)[0],
    }, indent=2))
