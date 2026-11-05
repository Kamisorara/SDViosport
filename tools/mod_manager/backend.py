"""
Backend module for Stardew Valley iOS Mod Manager.
Handles Apple USB AFC communication and mod/save/log operations.
"""

import os
import sys
import re
import json
import zipfile
import shutil
import tempfile
import asyncio
import subprocess
import urllib.request
from datetime import datetime
from typing import Optional, Callable, Any
from dataclasses import dataclass, field

try:
    from pymobiledevice3.usbmux import list_devices
    from pymobiledevice3.lockdown import create_using_usbmux
    from pymobiledevice3.services.house_arrest import HouseArrestService
    from pymobiledevice3.services.installation_proxy import InstallationProxyService
    PYMD3_AVAILABLE = True
except ImportError:
    PYMD3_AVAILABLE = False


@dataclass
class ModInfo:
    folder_name: str
    name: str
    author: str = "Unknown"
    version: str = "1.0.0"
    description: str = ""
    unique_id: str = ""
    is_enabled: bool = True
    minimum_api_version: str = ""
    dependencies: list[str] = field(default_factory=list)
    content_pack_for: Optional[str] = None
    is_content_pack: bool = False
    is_code_mod: bool = False
    nexus_id: Optional[int] = None
    update_keys: list[str] = field(default_factory=list)


KNOWN_FRAMEWORK_NEXUS_IDS = {
    "pathoschild.contentpatcher": 1915,
    "platonymous.farmtypemanager": 3231,
    "esca.farmtypemanager": 3231,
    "spacechase0.spacecore": 1348,
    "spacechase0.jsonassets": 1720,
    "spacechase0.genericmodconfigmenu": 5098,
    "cherry.expandedpreconditionsutility": 6529,
    "flashshifter.stardewvalleyexpandedcp": 3753,
    "spacechase0.dynamicgameassets": 9350,
    "pathoschild.lookupanything": 541,
    "pathoschild.automate": 1063,
}

PROFILES_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "profiles.json")


def load_profiles() -> dict[str, dict]:
    """Load saved mod profiles from profiles.json."""
    if os.path.exists(PROFILES_FILE):
        try:
            with open(PROFILES_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
                if isinstance(data, dict):
                    return data
        except Exception as e:
            print(f"[Backend] Failed to load profiles.json: {e}")
    return {}


def save_profiles(profiles: dict[str, dict]) -> bool:
    """Persist mod profiles to profiles.json."""
    try:
        with open(PROFILES_FILE, "w", encoding="utf-8") as f:
            json.dump(profiles, f, indent=4, ensure_ascii=False)
        return True
    except Exception as e:
        print(f"[Backend] Failed to save profiles.json: {e}")
        return False


def get_clean_folder_name(folder_name: str) -> str:
    """Strip .disabled_ prefix and any __v<version> parking suffix from a mod folder name."""
    name = folder_name.replace(".disabled_", "").replace(".disabled-", "")
    name = re.sub(r"__v[0-9a-zA-Z._-]+$", "", name)
    return name


def get_disabled_folder_name(clean_name: str, version: str = "") -> str:
    """Return a version-tagged disabled folder name so multiple versions of a mod can coexist."""
    if version:
        v_safe = re.sub(r"[^0-9a-zA-Z._-]", "_", str(version).strip())
        return f".disabled_{clean_name}__v{v_safe}"
    return f".disabled_{clean_name}"


def create_profile_snapshot(name: str, mods: list[ModInfo], description: str = "") -> dict:
    """Create a serializable mod profile dictionary from a list of ModInfo objects.
    When multiple versions of the same mod are installed on the device (e.g. one enabled
    and another parked as disabled), records the enabled version for this profile.
    """
    now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    merged: dict[str, dict] = {}
    for m in mods:
        clean_folder = get_clean_folder_name(m.folder_name)
        key = clean_folder.strip().lower() or (m.unique_id or m.name).strip().lower()
        entry = {
            "unique_id": m.unique_id,
            "name": m.name,
            "clean_folder": clean_folder,
            "version": m.version,
            "is_enabled": bool(m.is_enabled),
            "nexus_id": m.nexus_id,
            "is_content_pack": bool(m.is_content_pack),
            "is_code_mod": bool(m.is_code_mod),
        }
        if key not in merged:
            merged[key] = entry
        else:
            prev = merged[key]
            # Prefer whichever copy is currently ENABLED so this profile pins the active version!
            if entry["is_enabled"] and not prev["is_enabled"]:
                merged[key] = entry
            elif not entry["is_enabled"] and prev["is_enabled"]:
                pass
            elif is_version_newer(entry["version"], prev["version"]):
                merged[key] = entry

    return {
        "name": name,
        "description": description,
        "updated_at": now_str,
        "mods": list(merged.values()),
    }


def clean_json(text: str) -> str:
    """Strip C-style comments (/* ... */ and // ...) and trailing commas from JSON for SMAPI manifest compatibility."""
    # 1. Strip block comments /* ... */
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.DOTALL)
    # 2. Strip line comments // ... but preserve http:// and https://
    lines = []
    for line in text.splitlines():
        clean_line = re.sub(r'(?<!http:)(?<!https:)//.*$', '', line)
        lines.append(clean_line)
    text = '\n'.join(lines)
    # 3. Strip trailing commas before } or ] (repeat for nested structures)
    for _ in range(3):
        text = re.sub(r',\s*([}\]])', r'\1', text)
    return text


def is_requirement_installed(req_mod_id: Optional[int], req_name: str, installed_mods: list[ModInfo]) -> bool:
    """Check if a required mod is satisfied by any of the installed mods."""
    if req_mod_id == 2400 or "smapi" in req_name.lower():
        return True

    # Normalize req name (strip notes/framework indicators and spaces/punctuation)
    clean_req = re.sub(r"\([^)]*\)", "", req_name).strip().lower().replace(" ", "").replace("_", "").replace("-", "")

    for m in installed_mods:
        if req_mod_id and m.nexus_id and m.nexus_id == req_mod_id:
            return True
        if m.unique_id and req_mod_id and m.unique_id.lower() in KNOWN_FRAMEWORK_NEXUS_IDS:
            if KNOWN_FRAMEWORK_NEXUS_IDS[m.unique_id.lower()] == req_mod_id:
                return True
        m_uid_clean = m.unique_id.lower().replace(".", "").replace(" ", "").replace("_", "").replace("-", "")
        if clean_req and (clean_req == m_uid_clean or clean_req in m_uid_clean or m_uid_clean in clean_req):
            return True
        m_name_clean = m.name.lower().replace(" ", "").replace("_", "").replace("-", "")
        if clean_req and (clean_req == m_name_clean or clean_req in m_name_clean or m_name_clean in clean_req):
            return True

    return False


def parse_version_tuple(v: str) -> list:
    """Parse version string into comparable token list."""
    if not v:
        return []
    clean = re.sub(r'^[vV]', '', str(v).strip())
    tokens = re.findall(r'\d+|[a-zA-Z]+', clean)
    parsed = []
    for t in tokens:
        if t.isdigit():
            parsed.append(int(t))
        else:
            parsed.append(t.lower())
    return parsed


def is_version_newer(remote_v: str, local_v: str) -> bool:
    """Return True if remote_v is strictly newer than local_v."""
    if not remote_v or not local_v:
        return False
    r = parse_version_tuple(remote_v)
    l = parse_version_tuple(local_v)
    while len(r) > 0 and r[-1] == 0:
        r.pop()
    while len(l) > 0 and l[-1] == 0:
        l.pop()
    for ri, li in zip(r, l):
        if type(ri) is type(li):
            if ri > li:
                return True
            if ri < li:
                return False
        else:
            if isinstance(ri, int) and isinstance(li, str):
                return True
            elif isinstance(ri, str) and isinstance(li, int):
                return False
    return len(r) > len(l)


class IOSModBackend:
    """Manages iOS device connection and mod operations over AFC or Wi-Fi Debug Server."""

    def __init__(self):
        self.lockdown = None
        self.house_arrest: Optional[HouseArrestService] = None
        self.device_info: dict = {}
        self.bundle_id: str = ""
        self.is_connected: bool = False
        self.local_mode_dir: Optional[str] = None  # If user selects a mounted PC folder
        self.wifi_url: Optional[str] = None        # e.g. "http://192.168.1.123:8080"
        self._save_locations: dict[str, str] = {}

    async def list_usb_devices(self) -> list[dict]:
        """List all connected iOS devices over USB."""
        if not PYMD3_AVAILABLE:
            return []
        try:
            devices = await list_devices()
            results = []
            for d in devices:
                results.append({
                    "devid": d.devid,
                    "serial": d.serial,
                    "connection_type": d.connection_type
                })
            return results
        except Exception as e:
            print(f"[Backend] Error listing devices: {e}")
            return []

    async def connect_usb(self, serial: Optional[str] = None) -> tuple[bool, str]:
        """Connect to an iOS device and vend Stardew Valley Documents."""
        if not PYMD3_AVAILABLE:
            return False, "pymobiledevice3 library not installed."

        try:
            self.lockdown = await create_using_usbmux(serial=serial)
            self.device_info = self.lockdown.short_info
            dev_name = self.device_info.get("DeviceName", "iOS Device")
            model = self.device_info.get("ProductType", "iPhone")
            os_ver = self.device_info.get("ProductVersion", "iOS")

            # 1. Discover installed Stardew Valley app bundle ID
            bundle_id = None
            candidate_bids = [
                "com.deadakj.sdvios.KRS7366K7J",
                "com.deadakj.sdvios",
                "com.chucklefish.stardewvalley"
            ]
            for c_bid in candidate_bids:
                try:
                    ha = await HouseArrestService.create(self.lockdown, c_bid, documents_only=True)
                    bundle_id = c_bid
                    self.house_arrest = ha
                    break
                except Exception:
                    continue

            if not bundle_id:
                try:
                    async with InstallationProxyService(self.lockdown) as ip:
                        apps = await ip.get_apps()
                        for bid in apps.keys():
                            b_lower = bid.lower()
                            if "deadakj.sdvios" in b_lower or "stardew" in b_lower or "sdvios" in b_lower:
                                bundle_id = bid
                                break
                except Exception as e:
                    print(f"[Backend] Warning during app discovery: {e}")

            if not bundle_id:
                bundle_id = "com.deadakj.sdvios"

            self.bundle_id = bundle_id
            print(f"[Backend] Connecting to container: {self.bundle_id}")

            # 2. Connect via HouseArrest (VendDocuments) if not already created above
            if not self.house_arrest:
                self.house_arrest = await HouseArrestService.create(
                    self.lockdown, self.bundle_id, documents_only=True
                )

            # Ensure /Documents/Mods and /Documents/DisabledMods exist
            try:
                if not await self.house_arrest.exists("/Documents/Mods"):
                    await self.house_arrest.makedirs("/Documents/Mods")
                if not await self.house_arrest.exists("/Documents/DisabledMods"):
                    await self.house_arrest.makedirs("/Documents/DisabledMods")
            except Exception:
                pass

            self.is_connected = True
            self.local_mode_dir = None
            self.wifi_url = None
            return True, f"Connected to {dev_name} ({model}, iOS {os_ver})"

        except Exception as e:
            self.is_connected = False
            return False, f"Connection failed: {e}"

    def connect_wifi(self, ip_or_url: str = "192.168.1.123", port: int = 8080) -> tuple[bool, str]:
        """Connect wirelessly to the SDViOS RemoteDebugServer running on port 8080."""
        raw = ip_or_url.strip().rstrip("/")
        if not raw.startswith("http://") and not raw.startswith("https://"):
            if ":" in raw:
                base_url = f"http://{raw}"
            else:
                base_url = f"http://{raw}:{port}"
        else:
            base_url = raw

        try:
            req = urllib.request.Request(f"{base_url}/status", headers={"User-Agent": "SDViOS-ModManager"})
            with urllib.request.urlopen(req, timeout=4.0) as resp:
                data = json.loads(resp.read().decode("utf-8", errors="ignore"))
                self.wifi_url = base_url
                self.local_mode_dir = None
                self.house_arrest = None
                self.is_connected = True
                avail_mb = data.get("avail_mb", "?")
                loc = data.get("location", "Title")
                self.device_info = {
                    "DeviceName": f"Wi-Fi ({base_url})",
                    "ProductType": f"Headroom: {avail_mb} MB",
                    "ProductVersion": f"Loc: {loc}"
                }
                return True, f"Connected over Wi-Fi to {base_url} (Available RAM: {avail_mb} MB)"
        except Exception as e:
            return False, f"Could not reach SDViOS Wi-Fi server at {base_url}: {e}"

    def connect_local_folder(self, folder_path: str) -> tuple[bool, str]:
        """Fallback: connect to a local directory (e.g. if mounted by iTunes/3uTools/iMazing)."""
        if not os.path.isdir(folder_path):
            return False, f"Directory does not exist: {folder_path}"

        # If pointing to root of app documents, or directly to Mods folder:
        mods_path = folder_path if os.path.basename(folder_path).lower() == "mods" else os.path.join(folder_path, "Mods")
        os.makedirs(mods_path, exist_ok=True)
        self.local_mode_dir = folder_path
        self.wifi_url = None
        self.is_connected = True
        return True, f"Connected to local folder: {folder_path}"

    async def disconnect(self):
        """Close AFC session."""
        if self.house_arrest:
            try:
                await self.house_arrest.close()
            except Exception:
                pass
            self.house_arrest = None
        self.wifi_url = None
        self.is_connected = False

    def _wifi_get_json(self, path_and_query: str, timeout: float = 8.0) -> dict:
        req = urllib.request.Request(f"{self.wifi_url}{path_and_query}", headers={"User-Agent": "SDViOS-ModManager"})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8", errors="ignore"))

    def _wifi_get_bytes(self, path_and_query: str, timeout: float = 15.0) -> bytes:
        req = urllib.request.Request(f"{self.wifi_url}{path_and_query}", headers={"User-Agent": "SDViOS-ModManager"})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.read()

    def _wifi_post(self, path_and_query: str, body: bytes = b"", content_type: str = "application/octet-stream", timeout: float = 30.0) -> dict:
        req = urllib.request.Request(
            f"{self.wifi_url}{path_and_query}",
            data=body,
            method="POST",
            headers={
                "User-Agent": "SDViOS-ModManager",
                "Content-Type": content_type,
                "Content-Length": str(len(body)),
            },
        )
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8", errors="ignore"))

    async def list_mods(self) -> list[ModInfo]:
        """List all installed mods and parse their manifest.json."""
        if not self.is_connected:
            return []

        mods = []

        if self.local_mode_dir:
            # Local filesystem
            mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
            if not os.path.isdir(mods_dir):
                return []
            for entry in os.listdir(mods_dir):
                full_p = os.path.join(mods_dir, entry)
                if not os.path.isdir(full_p):
                    continue
                manifest_p = os.path.join(full_p, "manifest.json")
                mod = self._parse_manifest_local(entry, manifest_p)
                mods.append(mod)
            return sorted(mods, key=lambda m: m.name.lower())

        if self.wifi_url:
            def _fetch_wifi_mods():
                res = []
                try:
                    listing = self._wifi_get_json("/api/list?dir=Mods")
                    for item in listing.get("entries", []):
                        if not item.get("is_dir"):
                            continue
                        entry = item.get("name", "")
                        if entry in [".", "..", "SMAPI-config.json", ".DS_Store"]:
                            continue
                        rel_manifest = urllib.parse.quote(f"Mods/{entry}/manifest.json")
                        raw_bytes = None
                        try:
                            raw_bytes = self._wifi_get_bytes(f"/download?path={rel_manifest}", timeout=6.0)
                        except Exception:
                            pass
                        res.append(self._parse_manifest_bytes(entry, raw_bytes))
                except Exception as e:
                    print(f"[Backend] Error listing mods over Wi-Fi: {e}")
                return sorted(res, key=lambda m: m.name.lower())

            return await asyncio.to_thread(_fetch_wifi_mods)

        # AFC Remote
        if not self.house_arrest:
            return []

        try:
            try:
                if not await self.house_arrest.exists("/Documents/DisabledMods"):
                    await self.house_arrest.makedirs("/Documents/DisabledMods")
            except Exception:
                pass

            entries = await self.house_arrest.listdir("/Documents/Mods")
            for entry in entries:
                if entry in [".", "..", "SMAPI-config.json", ".DS_Store"]:
                    continue

                mod_path = f"/Documents/Mods/{entry}"
                try:
                    if not await self.house_arrest.isdir(mod_path):
                        continue
                except Exception:
                    continue

                # Auto-migrate any .disabled_* folders out of /Documents/Mods into /Documents/DisabledMods
                if entry.startswith(".disabled_") or entry.startswith(".disabled-"):
                    dest_disabled = f"/Documents/DisabledMods/{entry}"
                    try:
                        if await self.house_arrest.exists(dest_disabled):
                            await self.house_arrest.rm(dest_disabled)
                        await self.house_arrest.rename(mod_path, dest_disabled)
                        mod_path = dest_disabled
                    except Exception:
                        pass

                manifest_path = f"{mod_path}/manifest.json"
                mod = await self._parse_manifest_afc(entry, manifest_path)
                mods.append(mod)

            try:
                disabled_entries = await self.house_arrest.listdir("/Documents/DisabledMods")
                seen_folders = {m.folder_name for m in mods}
                for entry in disabled_entries:
                    if entry in [".", "..", "SMAPI-config.json", ".DS_Store"] or entry in seen_folders:
                        continue
                    mod_path = f"/Documents/DisabledMods/{entry}"
                    try:
                        if not await self.house_arrest.isdir(mod_path):
                            continue
                    except Exception:
                        continue
                    manifest_path = f"{mod_path}/manifest.json"
                    mod = await self._parse_manifest_afc(entry, manifest_path)
                    mod.is_enabled = False
                    mods.append(mod)
            except Exception:
                pass

            return sorted(mods, key=lambda m: m.name.lower())
        except Exception as e:
            print(f"[Backend] Error listing mods over AFC: {e}")
            return []

    async def _parse_manifest_afc(self, folder_name: str, manifest_path: str) -> ModInfo:
        """Parse manifest.json over AFC."""
        is_enabled = not folder_name.startswith(".disabled_") and not folder_name.startswith(".disabled-") and not folder_name.startswith(".")
        clean_name = get_clean_folder_name(folder_name)

        mod = ModInfo(
            folder_name=folder_name,
            name=clean_name,
            is_enabled=is_enabled
        )

        try:
            if await self.house_arrest.exists(manifest_path):
                raw = await self.house_arrest.get_file_contents(manifest_path)
                raw_str = clean_json(raw.decode("utf-8-sig", errors="ignore"))
                data = json.loads(raw_str)
                mod.name = data.get("Name", clean_name)
                mod.author = data.get("Author", "Unknown")
                mod.version = str(data.get("Version", "1.0.0"))
                mod.description = data.get("Description", "")
                mod.unique_id = data.get("UniqueID", "")
                mod.minimum_api_version = str(data.get("MinimumApiVersion", ""))

                if "ContentPackFor" in data and data["ContentPackFor"]:
                    mod.is_content_pack = True
                    mod.content_pack_for = data["ContentPackFor"].get("UniqueID", "Content Patcher")

                if "EntryDll" in data and data["EntryDll"]:
                    mod.is_code_mod = True

                deps = data.get("Dependencies", [])
                if isinstance(deps, list):
                    mod.dependencies = [
                        d.get("UniqueID", "") for d in deps 
                        if isinstance(d, dict) and "UniqueID" in d and d.get("IsRequired", True)
                    ]

                update_keys = data.get("UpdateKeys", [])
                if isinstance(update_keys, list):
                    mod.update_keys = [str(k) for k in update_keys if isinstance(k, str)]
                    for k in mod.update_keys:
                        m_match = re.search(r"nexus:(\d+)", k, re.IGNORECASE)
                        if m_match:
                            mod.nexus_id = int(m_match.group(1))
                            break

                if not mod.nexus_id and mod.unique_id and mod.unique_id.lower() in KNOWN_FRAMEWORK_NEXUS_IDS:
                    mod.nexus_id = KNOWN_FRAMEWORK_NEXUS_IDS[mod.unique_id.lower()]
        except Exception as e:
            print(f"[Backend] Manifest parse error for {folder_name}: {e}")

        return mod

    def _parse_manifest_bytes(self, folder_name: str, raw_bytes: Optional[bytes]) -> ModInfo:
        """Parse manifest.json from raw bytes."""
        is_enabled = not folder_name.startswith(".disabled_") and not folder_name.startswith(".disabled-") and not folder_name.startswith(".")
        clean_name = get_clean_folder_name(folder_name)

        mod = ModInfo(
            folder_name=folder_name,
            name=clean_name,
            is_enabled=is_enabled
        )

        if raw_bytes:
            try:
                raw_str = clean_json(raw_bytes.decode("utf-8-sig", errors="ignore"))
                data = json.loads(raw_str)
                mod.name = data.get("Name", clean_name)
                mod.author = data.get("Author", "Unknown")
                mod.version = str(data.get("Version", "1.0.0"))
                mod.description = data.get("Description", "")
                mod.unique_id = data.get("UniqueID", "")
                mod.minimum_api_version = str(data.get("MinimumApiVersion", ""))

                if "ContentPackFor" in data and data["ContentPackFor"]:
                    mod.is_content_pack = True
                    mod.content_pack_for = data["ContentPackFor"].get("UniqueID", "Content Patcher")

                if "EntryDll" in data and data["EntryDll"]:
                    mod.is_code_mod = True

                deps = data.get("Dependencies", [])
                if isinstance(deps, list):
                    mod.dependencies = [
                        d.get("UniqueID", "") for d in deps
                        if isinstance(d, dict) and "UniqueID" in d and d.get("IsRequired", True)
                    ]

                update_keys = data.get("UpdateKeys", [])
                if isinstance(update_keys, list):
                    mod.update_keys = [str(k) for k in update_keys if isinstance(k, str)]
                    for k in mod.update_keys:
                        m_match = re.search(r"nexus:(\d+)", k, re.IGNORECASE)
                        if m_match:
                            mod.nexus_id = int(m_match.group(1))
                            break

                if not mod.nexus_id and mod.unique_id and mod.unique_id.lower() in KNOWN_FRAMEWORK_NEXUS_IDS:
                    mod.nexus_id = KNOWN_FRAMEWORK_NEXUS_IDS[mod.unique_id.lower()]
            except Exception as e:
                print(f"[Backend] Manifest bytes parse error for {folder_name}: {e}")

        return mod

    def _parse_manifest_local(self, folder_name: str, manifest_path: str) -> ModInfo:
        """Parse manifest.json on local filesystem."""
        raw_bytes = None
        if os.path.isfile(manifest_path):
            try:
                with open(manifest_path, "rb") as f:
                    raw_bytes = f.read()
            except Exception:
                pass
        return self._parse_manifest_bytes(folder_name, raw_bytes)

    async def toggle_mod(self, mod: ModInfo) -> tuple[bool, str]:
        """Toggle a mod between Enabled and Disabled.
        Disabled folders use `.disabled_<clean_name>__v<version>` so different versions of the same mod
        (e.g. a downgraded version required by a collection vs a newer version for another profile)
        can coexist on the device without colliding.
        """
        if not self.is_connected:
            return False, "Not connected"

        target_enabled = not mod.is_enabled
        clean_name = get_clean_folder_name(mod.folder_name)
        new_folder = clean_name if target_enabled else get_disabled_folder_name(clean_name, mod.version)

        if self.local_mode_dir:
            mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
            src = os.path.join(mods_dir, mod.folder_name)
            dst = os.path.join(mods_dir, new_folder)
            try:
                if os.path.exists(dst) and os.path.abspath(src) != os.path.abspath(dst):
                    dst_mod = self._parse_manifest_local(new_folder, os.path.join(dst, "manifest.json"))
                    if target_enabled and dst_mod.version and dst_mod.version != mod.version:
                        # Park the currently active version so it is preserved for other profiles
                        parked = os.path.join(mods_dir, get_disabled_folder_name(clean_name, dst_mod.version))
                        if os.path.exists(parked):
                            shutil.rmtree(parked)
                        os.rename(dst, parked)
                    else:
                        shutil.rmtree(dst)
                if os.path.exists(src):
                    os.rename(src, dst)
                mod.folder_name = new_folder
                mod.is_enabled = target_enabled
                return True, f"Mod {'enabled' if target_enabled else 'disabled'}"
            except Exception as e:
                return False, f"Rename failed: {e}"

        if self.wifi_url:
            def _wifi_toggle():
                q_from = urllib.parse.quote(f"Mods/{mod.folder_name}")
                q_to = urllib.parse.quote(f"Mods/{new_folder}")
                try:
                    self._wifi_post(f"/api/delete?path={q_to}")
                except Exception:
                    pass
                self._wifi_post(f"/api/rename?from={q_from}&to={q_to}")
                mod.folder_name = new_folder
                mod.is_enabled = target_enabled
                return True, f"Mod {'enabled' if target_enabled else 'disabled'}"

            try:
                return await asyncio.to_thread(_wifi_toggle)
            except Exception as e:
                return False, f"Wi-Fi rename failed: {e}"

        if not self.house_arrest:
            return False, "AFC not available"

        try:
            try:
                if not await self.house_arrest.exists("/Documents/DisabledMods"):
                    await self.house_arrest.makedirs("/Documents/DisabledMods")
            except Exception:
                pass

            src = f"/Documents/Mods/{mod.folder_name}"
            if not await self.house_arrest.exists(src):
                alt_src = f"/Documents/DisabledMods/{mod.folder_name}"
                if await self.house_arrest.exists(alt_src):
                    src = alt_src

            dst = f"/Documents/Mods/{new_folder}" if target_enabled else f"/Documents/DisabledMods/{new_folder}"
            if src != dst and await self.house_arrest.exists(dst):
                dst_ver = ""
                try:
                    dst_manifest = f"{dst}/manifest.json"
                    if await self.house_arrest.exists(dst_manifest):
                        raw_bytes = await self.house_arrest.get_file_contents(dst_manifest)
                        dst_ver = self._parse_manifest_bytes(new_folder, raw_bytes).version
                except Exception:
                    pass

                if target_enabled and dst_ver and dst_ver != mod.version:
                    # Another version is currently enabled at dst; park it safely in DisabledMods instead of deleting it!
                    parked_name = get_disabled_folder_name(clean_name, dst_ver)
                    parked_path = f"/Documents/DisabledMods/{parked_name}"
                    if await self.house_arrest.exists(parked_path):
                        await self.house_arrest.rm(parked_path)
                    await self.house_arrest.rename(dst, parked_path)
                else:
                    # Exact same version already at dst; remove duplicate before renaming
                    await self.house_arrest.rm(dst)

            if await self.house_arrest.exists(src):
                await self.house_arrest.rename(src, dst)
            mod.folder_name = new_folder
            mod.is_enabled = target_enabled
            return True, f"Mod {'enabled' if target_enabled else 'disabled'}"
        except Exception as e:
            return False, f"AFC rename failed: {e}"

    async def apply_profile(
        self,
        profile_data: dict,
        strict: bool = True,
        progress_callback: Optional[Callable[[str], None]] = None,
    ) -> tuple[bool, str, dict]:
        """Apply a saved mod profile to the connected device or local folder.
        Supports multiple versions of the same mod coexisting across profiles (e.g. a downgraded version
        required by a collection vs a newer version in another profile) by parking inactive versions at
        `.disabled_<clean_folder>__v<version>` and enabling the profile's target version at `<clean_folder>`.
        """
        if not self.is_connected:
            return False, "Not connected to device or folder.", {}

        profile_name = profile_data.get("name", "Profile")
        raw_profile_mods = profile_data.get("mods", [])

        # Deduplicate profile entries, preferring the entry that was enabled when the profile was saved
        dedup_profile: dict[str, dict] = {}
        for entry in raw_profile_mods:
            cfolder = get_clean_folder_name(entry.get("clean_folder") or "").strip().lower()
            uid = (entry.get("unique_id") or "").strip().lower()
            mname = (entry.get("name") or "").strip().lower()
            key = cfolder or uid or mname
            if not key:
                continue
            if key not in dedup_profile:
                dedup_profile[key] = dict(entry)
            else:
                prev = dedup_profile[key]
                if entry.get("is_enabled") and not prev.get("is_enabled"):
                    dedup_profile[key] = dict(entry)
                elif not entry.get("is_enabled") and prev.get("is_enabled"):
                    pass
                elif is_version_newer(str(entry.get("version", "")), str(prev.get("version", ""))):
                    dedup_profile[key] = dict(entry)

        profile_mods = list(dedup_profile.values())

        by_uid: dict[str, dict] = {}
        by_folder: dict[str, dict] = {}
        by_name: dict[str, dict] = {}

        for entry in profile_mods:
            uid = (entry.get("unique_id") or "").strip().lower()
            cfolder = get_clean_folder_name(entry.get("clean_folder") or "").strip().lower()
            mname = (entry.get("name") or "").strip().lower()
            if uid:
                by_uid[uid] = entry
            if cfolder:
                by_folder[cfolder] = entry
            if mname:
                by_name[mname] = entry

        if progress_callback:
            progress_callback(f"Reading installed mods for profile '{profile_name}'...")

        installed_mods = await self.list_mods()

        # Group installed mods by clean_folder (case-insensitive)
        groups_by_clean: dict[str, list[ModInfo]] = {}
        for mod in installed_mods:
            clean_folder = get_clean_folder_name(mod.folder_name)
            key = clean_folder.strip().lower()
            groups_by_clean.setdefault(key, []).append(mod)

        matched_profile_keys = set()
        enabled_changed = 0
        disabled_changed = 0
        unchanged = 0
        errors = []

        for cfolder_lower, group in groups_by_clean.items():
            # Remove only true duplicates that have the EXACT same version string
            by_ver: dict[str, ModInfo] = {}
            for m in sorted(group, key=lambda x: 1 if x.is_enabled else 0, reverse=True):
                v_key = (m.version or "").strip().lower()
                if v_key in by_ver:
                    await self.delete_mod(m)
                else:
                    by_ver[v_key] = m
            unique_versions = list(by_ver.values())

            sample = unique_versions[0]
            uid_lower = (sample.unique_id or "").strip().lower()
            name_lower = (sample.name or "").strip().lower()

            entry = None
            if cfolder_lower and cfolder_lower in by_folder:
                entry = by_folder[cfolder_lower]
            elif uid_lower and uid_lower in by_uid:
                entry = by_uid[uid_lower]
            elif name_lower and name_lower in by_name:
                entry = by_name[name_lower]

            desired_version = None
            if entry is not None:
                entry_key = (entry.get("unique_id") or entry.get("clean_folder") or entry.get("name") or "").lower()
                matched_profile_keys.add(entry_key)
                target_enabled = bool(entry.get("is_enabled", True))
                desired_version = (entry.get("version") or "").strip()
            else:
                is_touch_controls = (
                    uid_lower == "com.deadakj.sdviostouchcontrols"
                    or cfolder_lower == "sdviostouchcontrols"
                )
                if is_touch_controls:
                    target_enabled = True
                elif strict:
                    target_enabled = False
                else:
                    target_enabled = any(m.is_enabled for m in unique_versions)

            if not target_enabled:
                # Disable all installed versions of this mod (parking each with its version suffix)
                any_toggled = False
                for m in unique_versions:
                    if m.is_enabled:
                        if progress_callback:
                            progress_callback(f"Disabling {m.name} (v{m.version})...")
                        ok, msg = await self.toggle_mod(m)
                        if ok:
                            any_toggled = True
                        else:
                            errors.append(f"{m.name}: {msg}")
                if any_toggled:
                    disabled_changed += 1
                else:
                    unchanged += 1
            else:
                # Pick the version matching desired_version (even if it's an older version required by a collection!)
                chosen = None
                if desired_version:
                    chosen = next(
                        (m for m in unique_versions if (m.version or "").strip().lower() == desired_version.lower()),
                        None
                    )
                if chosen is None:
                    # Fallback to highest version
                    unique_versions.sort(
                        key=lambda m: (parse_version_tuple(m.version), 1 if m.is_enabled else 0),
                        reverse=True
                    )
                    chosen = unique_versions[0]

                # 1. Disable any other version that is currently enabled so `<clean_folder>` is freed
                for other in unique_versions:
                    if other is not chosen and other.is_enabled:
                        if progress_callback:
                            progress_callback(f"Parking {other.name} (v{other.version})...")
                        ok, msg = await self.toggle_mod(other)
                        if not ok:
                            errors.append(f"{other.name} (v{other.version}): {msg}")

                # 2. Enable the chosen version if it isn't already enabled
                if not chosen.is_enabled:
                    if progress_callback:
                        progress_callback(f"Enabling {chosen.name} (v{chosen.version})...")
                    ok, msg = await self.toggle_mod(chosen)
                    if ok:
                        enabled_changed += 1
                    else:
                        errors.append(f"{chosen.name}: {msg}")
                else:
                    unchanged += 1

        missing_mods = []
        for entry in profile_mods:
            if not entry.get("is_enabled", True):
                continue
            entry_key = (entry.get("unique_id") or entry.get("clean_folder") or entry.get("name") or "").lower()
            if entry_key and entry_key not in matched_profile_keys:
                missing_mods.append(entry)

        stats = {
            "profile_name": profile_name,
            "enabled_changed": enabled_changed,
            "disabled_changed": disabled_changed,
            "unchanged": unchanged,
            "missing_mods": missing_mods,
            "errors": errors,
        }

        summary = (
            f"Applied profile '{profile_name}': "
            f"{enabled_changed} enabled, {disabled_changed} disabled, {unchanged} unchanged."
        )
        if missing_mods:
            summary += f" ({len(missing_mods)} enabled mod(s) in profile are not installed on device.)"
        if errors:
            return False, f"{summary} Errors: {'; '.join(errors)}", stats
        return True, summary, stats

    async def delete_mod(self, mod: ModInfo) -> tuple[bool, str]:
        """Permanently delete a mod."""
        if not self.is_connected:
            return False, "Not connected"

        if self.local_mode_dir:
            mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
            target = os.path.join(mods_dir, mod.folder_name)
            try:
                shutil.rmtree(target)
                return True, f"Deleted {mod.name}"
            except Exception as e:
                return False, f"Delete failed: {e}"

        if self.wifi_url:
            def _wifi_del():
                q_path = urllib.parse.quote(f"Mods/{mod.folder_name}")
                self._wifi_post(f"/api/delete?path={q_path}")
                return True, f"Deleted {mod.name}"

            try:
                return await asyncio.to_thread(_wifi_del)
            except Exception as e:
                return False, f"Wi-Fi delete failed: {e}"

        if not self.house_arrest:
            return False, "AFC not available"

        try:
            target = f"/Documents/Mods/{mod.folder_name}"
            if await self.house_arrest.exists(target):
                await self.house_arrest.rm(target)
            alt_target = f"/Documents/DisabledMods/{mod.folder_name}"
            if await self.house_arrest.exists(alt_target):
                await self.house_arrest.rm(alt_target)
            return True, f"Deleted {mod.name}"
        except Exception as e:
            return False, f"AFC delete failed: {e}"

    async def delete_all_mods(self) -> tuple[bool, str]:
        """Permanently delete all mods while keeping SMAPI config intact."""
        if not self.is_connected:
            return False, "Not connected"

        deleted_count = 0
        errors = []

        if self.local_mode_dir:
            mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
            if os.path.isdir(mods_dir):
                for entry in os.listdir(mods_dir):
                    if entry.lower() in ["smapi-config.json", ".ds_store", "desktop.ini"]:
                        continue
                    p = os.path.join(mods_dir, entry)
                    try:
                        if os.path.isdir(p):
                            shutil.rmtree(p)
                        else:
                            os.remove(p)
                        deleted_count += 1
                    except Exception as e:
                        errors.append(f"{entry}: {e}")
            if errors:
                return False, f"Deleted {deleted_count} mod(s), but {len(errors)} error(s) occurred."
            return True, f"Successfully deleted all {deleted_count} mod(s)."

        if self.wifi_url:
            def _wifi_del_all():
                nonlocal deleted_count
                listing = self._wifi_get_json("/api/list?dir=Mods")
                for item in listing.get("entries", []):
                    entry = item.get("name", "")
                    if entry in [".", "..", "SMAPI-config.json", ".DS_Store"]:
                        continue
                    try:
                        q_path = urllib.parse.quote(f"Mods/{entry}")
                        self._wifi_post(f"/api/delete?path={q_path}")
                        deleted_count += 1
                    except Exception as ex:
                        errors.append(f"{entry}: {ex}")
                if errors:
                    return False, f"Deleted {deleted_count} mod(s), but {len(errors)} error(s) occurred."
                return True, f"Successfully deleted all {deleted_count} mod(s)."

            try:
                return await asyncio.to_thread(_wifi_del_all)
            except Exception as e:
                return False, f"Wi-Fi delete all failed: {e}"

        if not self.house_arrest:
            return False, "AFC not available"

        try:
            for base_dir in ["/Documents/Mods", "/Documents/DisabledMods"]:
                if not await self.house_arrest.exists(base_dir):
                    continue
                entries = await self.house_arrest.listdir(base_dir)
                for entry in entries:
                    if entry in [".", "..", "SMAPI-config.json", ".DS_Store"]:
                        continue
                    p = f"{base_dir}/{entry}"
                    try:
                        await self.house_arrest.rm(p)
                        deleted_count += 1
                    except Exception as e:
                        errors.append(f"{entry}: {e}")

            if errors:
                return False, f"Deleted {deleted_count} mod(s), but {len(errors)} error(s) occurred."
            return True, f"Successfully deleted all {deleted_count} mod(s)."
        except Exception as e:
            return False, f"AFC delete all failed: {e}"


    async def install_mod_archive(self, archive_path: str, progress_callback: Optional[Callable[[str], None]] = None) -> tuple[bool, str]:
        """Extract a .zip or folder and push valid mod folders to the device."""
        if not self.is_connected:
            return False, "Not connected"

        if progress_callback:
            progress_callback("Extracting archive...")

        temp_dir = tempfile.mkdtemp(prefix="sdv_mod_")
        try:
            # 1. Extract archive
            if zipfile.is_zipfile(archive_path):
                with zipfile.ZipFile(archive_path, "r") as z:
                    z.extractall(temp_dir)
            elif os.path.isdir(archive_path):
                shutil.copytree(archive_path, os.path.join(temp_dir, os.path.basename(archive_path)))
            else:
                # Attempt extraction via system bsdtar (built-in Windows tar.exe handles .7z, .rar, .tar, .zip, etc.)
                extracted = False
                try:
                    subprocess.check_call(["tar", "-xf", archive_path, "-C", temp_dir], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    extracted = True
                except Exception:
                    pass

                # If tar failed or not available, check 7z / WinRAR
                if not extracted:
                    for archiver in ["7z", "7za", "winrar"]:
                        archiver_path = shutil.which(archiver)
                        if archiver_path:
                            try:
                                subprocess.check_call([archiver_path, "x", "-y", f"-o{temp_dir}", archive_path], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                                extracted = True
                                break
                            except Exception:
                                pass

                if not extracted:
                    return False, f"Unsupported file format ({os.path.splitext(archive_path)[1]}). Please choose a .zip, .7z, .rar, or folder."

            # 2. Discover all mod roots (directories containing manifest.json)
            mod_dirs = []
            for root, dirs, files in os.walk(temp_dir):
                if "manifest.json" in files:
                    mod_dirs.append(root)

            if not mod_dirs:
                # 2a. Check if this is a config pack (archive containing config.json files for existing mods)
                config_files = []
                for root, dirs, files in os.walk(temp_dir):
                    if "config.json" in files:
                        config_files.append((root, os.path.join(root, "config.json")))

                if config_files:
                    if progress_callback:
                        progress_callback(f"Deploying {len(config_files)} mod configuration(s)...")

                    applied_count = 0
                    existing_mods = set()
                    if self.local_mode_dir:
                        mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
                        if os.path.exists(mods_dir):
                            existing_mods = set(os.listdir(mods_dir))
                    elif self.wifi_url:
                        try:
                            listing = await asyncio.to_thread(self._wifi_get_json, "/api/list?dir=Mods")
                            existing_mods = {item.get("name", "") for item in listing.get("entries", []) if item.get("is_dir")}
                        except Exception:
                            pass
                    elif self.house_arrest:
                        try:
                            existing_mods = set(await self.house_arrest.listdir("/Documents/Mods"))
                        except Exception:
                            pass

                    for cfg_dir, cfg_path in config_files:
                        cfg_folder = os.path.basename(cfg_dir)
                        # Match folder name against installed mod folders
                        matched = next((m for m in existing_mods if m.lower() == cfg_folder.lower()), None)
                        if not matched:
                            parent_folder = os.path.basename(os.path.dirname(cfg_dir))
                            matched = next((m for m in existing_mods if m.lower() == parent_folder.lower()), None)

                        if matched:
                            if self.local_mode_dir:
                                dest_cfg = os.path.join(mods_dir, matched, "config.json")
                                shutil.copy2(cfg_path, dest_cfg)
                            elif self.wifi_url:
                                q_dest = urllib.parse.quote(f"Mods/{matched}/config.json")
                                with open(cfg_path, "rb") as f_cfg:
                                    await asyncio.to_thread(self._wifi_post, f"/upload?path={q_dest}", f_cfg.read())
                            elif self.house_arrest:
                                dest_cfg = f"/Documents/Mods/{matched}/config.json"
                                with open(cfg_path, "rb") as f_cfg:
                                    await self.house_arrest.set_file_contents(dest_cfg, f_cfg.read())
                            applied_count += 1

                    if applied_count > 0:
                        return True, f"Config pack applied to {applied_count} mod(s)."

                # 2b. Check if this is the SMAPI installer
                for root, dirs, files in os.walk(temp_dir):
                    if any("StardewModdingAPI" in f for f in files) and any("install" in f.lower() for f in files):
                        return True, "SMAPI installer detected (SMAPI is already built-in on SDViOS)."

                return False, "No valid Stardew Valley mod (manifest.json) or mod configurations found in archive."

            installed_names = []

            for mod_dir in mod_dirs:
                # Read name and version from manifest
                m_path = os.path.join(mod_dir, "manifest.json")
                mod_name = os.path.basename(mod_dir)
                new_ver = ""
                try:
                    with open(m_path, "rb") as f:
                        parsed_new = self._parse_manifest_bytes(os.path.basename(mod_dir), f.read())
                        mod_name = parsed_new.name or mod_name
                        new_ver = parsed_new.version or ""
                except Exception:
                    pass

                # Sanitize folder name
                folder_name = os.path.basename(mod_dir)
                if folder_name.startswith("sdv_mod_") or folder_name == os.path.basename(temp_dir):
                    folder_name = "".join(c for c in mod_name if c.isalnum() or c in (" ", "_", "-", "[", "]")).strip()
                    if not folder_name:
                        folder_name = "InstalledMod"

                if progress_callback:
                    progress_callback(f"Installing {mod_name}...")

                disabled_same_ver = get_disabled_folder_name(folder_name, new_ver)
                legacy_disabled = [f".disabled_{folder_name}", f".disabled-{folder_name}"]

                if self.local_mode_dir:
                    mods_dir = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() == "mods" else os.path.join(self.local_mode_dir, "Mods")
                    dst = os.path.join(mods_dir, folder_name)
                    if os.path.exists(dst):
                        dst_info = self._parse_manifest_local(folder_name, os.path.join(dst, "manifest.json"))
                        if dst_info.version and new_ver and dst_info.version != new_ver:
                            # Park the other version so profiles needing it keep their version & config
                            parked = os.path.join(mods_dir, get_disabled_folder_name(folder_name, dst_info.version))
                            if os.path.exists(parked):
                                shutil.rmtree(parked)
                            os.rename(dst, parked)
                        else:
                            shutil.rmtree(dst)
                    for leg in legacy_disabled:
                        leg_p = os.path.join(mods_dir, leg)
                        if os.path.exists(leg_p):
                            leg_info = self._parse_manifest_local(leg, os.path.join(leg_p, "manifest.json"))
                            if leg_info.version and new_ver and leg_info.version != new_ver:
                                parked = os.path.join(mods_dir, get_disabled_folder_name(folder_name, leg_info.version))
                                if os.path.exists(parked):
                                    shutil.rmtree(parked)
                                os.rename(leg_p, parked)
                            else:
                                shutil.rmtree(leg_p)
                    same_ver_parked = os.path.join(mods_dir, disabled_same_ver)
                    if os.path.exists(same_ver_parked):
                        shutil.rmtree(same_ver_parked)
                    shutil.copytree(mod_dir, dst)
                elif self.wifi_url:
                    def _push_mod_wifi(m_dir=mod_dir, f_name=folder_name):
                        q_del = urllib.parse.quote(f"Mods/{f_name}")
                        try:
                            self._wifi_post(f"/api/delete?path={q_del}")
                        except Exception:
                            pass
                        zip_tmp = os.path.join(temp_dir, f"_wifi_{f_name}.zip")
                        with zipfile.ZipFile(zip_tmp, "w", zipfile.ZIP_DEFLATED) as zf:
                            for r, _, fls in os.walk(m_dir):
                                for fl in fls:
                                    full_f = os.path.join(r, fl)
                                    rel_f = os.path.join(f_name, os.path.relpath(full_f, m_dir))
                                    zf.write(full_f, rel_f)
                        with open(zip_tmp, "rb") as z_in:
                            self._wifi_post("/api/upload-zip?dest=Mods", z_in.read(), timeout=120.0)

                    await asyncio.to_thread(_push_mod_wifi)
                else:
                    remote_target = f"/Documents/Mods/{folder_name}"
                    try:
                        # 1. If an active folder exists with a DIFFERENT version, park it at .disabled_<folder>__v<ver>
                        if await self.house_arrest.exists(remote_target):
                            cand_ver = ""
                            cand_manifest = f"{remote_target}/manifest.json"
                            if await self.house_arrest.exists(cand_manifest):
                                raw_b = await self.house_arrest.get_file_contents(cand_manifest)
                                cand_ver = self._parse_manifest_bytes(folder_name, raw_b).version
                            if cand_ver and new_ver and cand_ver != new_ver:
                                parked_remote = f"/Documents/Mods/{get_disabled_folder_name(folder_name, cand_ver)}"
                                if await self.house_arrest.exists(parked_remote):
                                    await self.house_arrest.rm(parked_remote)
                                await self.house_arrest.rename(remote_target, parked_remote)
                            else:
                                await self.house_arrest.rm(remote_target)

                        # 2. Migrate any legacy unversioned .disabled_<folder> so it won't collide
                        for leg in legacy_disabled:
                            leg_remote = f"/Documents/Mods/{leg}"
                            if await self.house_arrest.exists(leg_remote):
                                leg_ver = ""
                                leg_manifest = f"{leg_remote}/manifest.json"
                                if await self.house_arrest.exists(leg_manifest):
                                    raw_b = await self.house_arrest.get_file_contents(leg_manifest)
                                    leg_ver = self._parse_manifest_bytes(leg, raw_b).version
                                if leg_ver and new_ver and leg_ver != new_ver:
                                    parked_remote = f"/Documents/Mods/{get_disabled_folder_name(folder_name, leg_ver)}"
                                    if await self.house_arrest.exists(parked_remote):
                                        await self.house_arrest.rm(parked_remote)
                                    await self.house_arrest.rename(leg_remote, parked_remote)
                                else:
                                    await self.house_arrest.rm(leg_remote)

                        # 3. If the exact same version was parked as disabled, remove the disabled copy
                        same_parked = f"/Documents/Mods/{disabled_same_ver}"
                        if await self.house_arrest.exists(same_parked):
                            await self.house_arrest.rm(same_parked)
                    except Exception:
                        pass

                    # Push directory
                    await self.house_arrest.push(mod_dir, "/Documents/Mods", progress_bar=False)

                installed_names.append(mod_name)

            return True, f"Successfully installed: {', '.join(installed_names)}"

        except Exception as e:
            return False, f"Installation failed: {e}"
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)

    async def get_smapi_log(self, previous: bool = False) -> str:
        """Fetch latest (or previous crash) SMAPI log."""
        if not self.is_connected:
            return "Not connected."

        log_filename = "SMAPI-previous.txt" if previous else "SMAPI-latest.txt"
        engine_filename = "engine-previous.log" if previous else "engine-latest.log"

        if self.local_mode_dir:
            base = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() != "mods" else os.path.dirname(self.local_mode_dir)
            for cand in [
                os.path.join(base, ".config", "StardewValley", "ErrorLogs", log_filename),
                os.path.join(base, "ErrorLogs", log_filename),
                os.path.join(base, "ErrorLogs", engine_filename),
            ]:
                if os.path.isfile(cand):
                    with open(cand, "r", encoding="utf-8", errors="ignore") as f:
                        return f.read()
            return f"No {log_filename} found on device."

        if self.wifi_url:
            def _fetch_wifi_log():
                endpoint = "/log-previous" if previous else "/log"
                raw = self._wifi_get_bytes(endpoint, timeout=15.0)
                return raw.decode("utf-8", errors="ignore")

            try:
                return await asyncio.to_thread(_fetch_wifi_log)
            except Exception as e:
                return f"Error retrieving {log_filename} over Wi-Fi: {e}"

        if not self.house_arrest:
            return "AFC not available."

        try:
            for candidate in [
                f"/Documents/.config/StardewValley/ErrorLogs/{log_filename}",
                f"/Documents/ErrorLogs/{log_filename}",
                f"/Documents/ErrorLogs/{engine_filename}",
            ]:
                if await self.house_arrest.exists(candidate):
                    raw = await self.house_arrest.get_file_contents(candidate)
                    return raw.decode("utf-8", errors="ignore")
            return f"No {log_filename} found on device in /Documents/ErrorLogs/ or /Documents/.config/StardewValley/ErrorLogs/."
        except Exception as e:
            return f"Error retrieving SMAPI log: {e}"

    async def list_saves(self) -> list[str]:
        """List all save game folder names across all known iOS Stardew locations."""
        if not self.is_connected:
            return []

        self._save_locations = {}

        if self.local_mode_dir:
            base = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() != "mods" else os.path.dirname(self.local_mode_dir)
            search_paths = [
                os.path.join(base, ".config", "StardewValley", "Saves"),
                os.path.join(base, "Saves"),
                os.path.join(base, "StardewValley", "Saves"),
                os.path.join(base, "StardewValley"),
            ]
            for folder in search_paths:
                if os.path.isdir(folder):
                    try:
                        for entry in os.listdir(folder):
                            p = os.path.join(folder, entry)
                            if os.path.isdir(p) and entry not in [".", "..", ".DS_Store", "ErrorLogs", "Content", "Mods", ".smapi", ".Trash"]:
                                if os.path.exists(os.path.join(p, "SaveGameInfo")) or os.path.exists(os.path.join(p, entry)) or "_" in entry:
                                    if entry not in self._save_locations:
                                        self._save_locations[entry] = p
                    except Exception:
                        pass

            try:
                for entry in os.listdir(base):
                    p = os.path.join(base, entry)
                    if os.path.isdir(p) and entry not in [".", "..", ".DS_Store", "ErrorLogs", "Content", "Mods", "Saves", "StardewValley", ".config", ".Trash"]:
                        if os.path.exists(os.path.join(p, "SaveGameInfo")):
                            if entry not in self._save_locations:
                                self._save_locations[entry] = p
            except Exception:
                pass

            return sorted(list(self._save_locations.keys()))

        if self.wifi_url:
            def _wifi_list_saves():
                for rel_folder in [".config/StardewValley/Saves", "Saves", "StardewValley/Saves"]:
                    try:
                        listing = self._wifi_get_json(f"/api/list?dir={urllib.parse.quote(rel_folder)}")
                        for item in listing.get("entries", []):
                            if not item.get("is_dir"):
                                continue
                            entry = item.get("name", "")
                            if entry in [".", "..", ".DS_Store", "ErrorLogs", "Content", "Mods", ".smapi", ".Trash"]:
                                continue
                            if entry not in self._save_locations:
                                self._save_locations[entry] = f"{rel_folder}/{entry}"
                    except Exception:
                        pass
                return sorted(list(self._save_locations.keys()))

            try:
                return await asyncio.to_thread(_wifi_list_saves)
            except Exception as e:
                print(f"[Backend] Error listing saves over Wi-Fi: {e}")
                return []

        if not self.house_arrest:
            return []

        try:
            search_paths = [
                "/Documents/.config/StardewValley/Saves",
                "/Documents/Saves",
                "/Documents/StardewValley/Saves",
                "/Documents/StardewValley",
            ]
            for folder in search_paths:
                try:
                    if await self.house_arrest.exists(folder):
                        entries = await self.house_arrest.listdir(folder)
                        for entry in entries:
                            if entry in [".", "..", ".DS_Store", "ErrorLogs", "Content", "Mods", ".smapi", ".Trash"]:
                                continue
                            full_p = f"{folder}/{entry}"
                            try:
                                if await self.house_arrest.isdir(full_p):
                                    sub_entries = await self.house_arrest.listdir(full_p)
                                    if "SaveGameInfo" in sub_entries or entry in sub_entries or "_" in entry:
                                        if entry not in self._save_locations:
                                            self._save_locations[entry] = full_p
                            except Exception:
                                pass
                except Exception:
                    pass

            # Also check top-level /Documents for any folder containing SaveGameInfo
            try:
                doc_entries = await self.house_arrest.listdir("/Documents")
                for entry in doc_entries:
                    if entry in [".", "..", ".DS_Store", "ErrorLogs", "Content", "Mods", "Saves", "StardewValley", ".config", ".Trash", "smapi-internal"]:
                        continue
                    full_p = f"/Documents/{entry}"
                    try:
                        if await self.house_arrest.isdir(full_p):
                            sub_entries = await self.house_arrest.listdir(full_p)
                            if "SaveGameInfo" in sub_entries:
                                if entry not in self._save_locations:
                                    self._save_locations[entry] = full_p
                    except Exception:
                        pass
            except Exception:
                pass

            return sorted(list(self._save_locations.keys()))
        except Exception as e:
            print(f"[Backend] Error listing saves: {e}")
            return []

    async def backup_save(self, save_name: str, local_dest_dir: str) -> tuple[bool, str]:
        """Download a save folder and zip it."""
        if not self.is_connected:
            return False, "Not connected"

        out_zip = os.path.join(local_dest_dir, f"{save_name}_backup.zip")
        temp_dir = tempfile.mkdtemp(prefix="sdv_save_")

        try:
            if self.local_mode_dir:
                src = self._save_locations.get(save_name)
                if not src or not os.path.exists(src):
                    base = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() != "mods" else os.path.dirname(self.local_mode_dir)
                    for cand in [
                        os.path.join(base, ".config", "StardewValley", "Saves", save_name),
                        os.path.join(base, "Saves", save_name),
                        os.path.join(base, "StardewValley", "Saves", save_name),
                        os.path.join(base, save_name),
                    ]:
                        if os.path.exists(cand):
                            src = cand
                            break
                if not src or not os.path.exists(src):
                    return False, f"Save directory '{save_name}' not found."

                shutil.copytree(src, os.path.join(temp_dir, save_name))
            elif self.wifi_url:
                def _wifi_backup():
                    rel_src = self._save_locations.get(save_name, f".config/StardewValley/Saves/{save_name}").lstrip("/")
                    if rel_src.startswith("Documents/"):
                        rel_src = rel_src[len("Documents/"):]
                    listing = self._wifi_get_json(f"/api/list?dir={urllib.parse.quote(rel_src)}")
                    save_dst = os.path.join(temp_dir, save_name)
                    os.makedirs(save_dst, exist_ok=True)
                    for item in listing.get("entries", []):
                        if item.get("is_dir"):
                            continue
                        fname = item.get("name", "")
                        raw = self._wifi_get_bytes(f"/download?path={urllib.parse.quote(f'{rel_src}/{fname}')}", timeout=30.0)
                        with open(os.path.join(save_dst, fname), "wb") as f_out:
                            f_out.write(raw)

                await asyncio.to_thread(_wifi_backup)
            else:
                remote_src = self._save_locations.get(save_name)
                if not remote_src:
                    for cand in [
                        f"/Documents/.config/StardewValley/Saves/{save_name}",
                        f"/Documents/Saves/{save_name}",
                        f"/Documents/StardewValley/Saves/{save_name}",
                        f"/Documents/{save_name}",
                    ]:
                        if await self.house_arrest.exists(cand):
                            remote_src = cand
                            break
                if not remote_src:
                    return False, f"Save directory '{save_name}' not found on device."

                await self.house_arrest.pull(remote_src, temp_dir, progress_bar=False)

            # Zip contents
            with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as z:
                for root, dirs, files in os.walk(temp_dir):
                    for file in files:
                        p = os.path.join(root, file)
                        arc = os.path.relpath(p, temp_dir)
                        z.write(p, arc)

            return True, f"Saved backup to: {out_zip}"
        except Exception as e:
            return False, f"Backup failed: {e}"
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)

    async def import_save(self, source_path: str) -> tuple[bool, str]:
        """Import a save folder or .zip file into the game saves directory."""
        if not self.is_connected:
            return False, "Not connected"

        temp_dir = tempfile.mkdtemp(prefix="sdv_import_save_")
        try:
            extracted_folder = None
            if os.path.isfile(source_path) and (source_path.lower().endswith(".zip") or zipfile.is_zipfile(source_path)):
                with zipfile.ZipFile(source_path, "r") as z:
                    z.extractall(temp_dir)
                for root, dirs, files in os.walk(temp_dir):
                    if "SaveGameInfo" in files:
                        extracted_folder = root
                        break
            elif os.path.isdir(source_path):
                if os.path.exists(os.path.join(source_path, "SaveGameInfo")):
                    extracted_folder = source_path
                else:
                    for entry in os.listdir(source_path):
                        sub = os.path.join(source_path, entry)
                        if os.path.isdir(sub) and os.path.exists(os.path.join(sub, "SaveGameInfo")):
                            extracted_folder = sub
                            break

            if not extracted_folder or not os.path.exists(extracted_folder):
                return False, "Could not find a valid Stardew Valley save (must contain 'SaveGameInfo')."

            save_name = os.path.basename(extracted_folder)

            if self.local_mode_dir:
                base = self.local_mode_dir if os.path.basename(self.local_mode_dir).lower() != "mods" else os.path.dirname(self.local_mode_dir)
                dest_dir = os.path.join(base, ".config", "StardewValley", "Saves", save_name)
                os.makedirs(os.path.dirname(dest_dir), exist_ok=True)
                if os.path.exists(dest_dir):
                    shutil.rmtree(dest_dir)
                shutil.copytree(extracted_folder, dest_dir)

                # Also sync to /Saves/ if it exists
                alt_dir = os.path.join(base, "Saves", save_name)
                if os.path.exists(os.path.dirname(alt_dir)):
                    if os.path.exists(alt_dir):
                        shutil.rmtree(alt_dir)
                    shutil.copytree(extracted_folder, alt_dir)
            elif self.wifi_url:
                def _wifi_import():
                    zip_tmp = os.path.join(temp_dir, f"_wifi_save_{save_name}.zip")
                    with zipfile.ZipFile(zip_tmp, "w", zipfile.ZIP_DEFLATED) as zf:
                        for r, _, fls in os.walk(extracted_folder):
                            for fl in fls:
                                full_f = os.path.join(r, fl)
                                rel_f = os.path.join(save_name, os.path.relpath(full_f, extracted_folder))
                                zf.write(full_f, rel_f)
                    with open(zip_tmp, "rb") as z_in:
                        payload = z_in.read()
                    for dest_rel in [".config/StardewValley/Saves", "Saves"]:
                        try:
                            self._wifi_post(f"/api/upload-zip?dest={urllib.parse.quote(dest_rel)}", payload, timeout=60.0)
                        except Exception:
                            pass

                await asyncio.to_thread(_wifi_import)
            else:
                # iOS device over AFC
                dest_config = f"/Documents/.config/StardewValley/Saves"
                if not await self.house_arrest.exists(dest_config):
                    await self.house_arrest.makedirs(dest_config)
                remote_save_cfg = f"{dest_config}/{save_name}"
                if await self.house_arrest.exists(remote_save_cfg):
                    await self.house_arrest.rm(remote_save_cfg)
                await self.house_arrest.push(extracted_folder, dest_config, progress_bar=False)

                # Also sync to /Documents/Saves
                try:
                    if await self.house_arrest.exists("/Documents/Saves"):
                        remote_alt = f"/Documents/Saves/{save_name}"
                        if await self.house_arrest.exists(remote_alt):
                            await self.house_arrest.rm(remote_alt)
                        await self.house_arrest.push(extracted_folder, "/Documents/Saves", progress_bar=False)
                except Exception:
                    pass

            return True, f"Successfully imported save '{save_name}'."
        except Exception as e:
            return False, f"Import failed: {e}"
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)

    async def get_system_update_status(self) -> dict:
        """Check Stardew Valley game version, SDViOS port version, and SMAPI version against latest upstream releases."""
        local_game_version = "1.6.15"
        local_port_version = "v3.0.0"
        local_smapi_version = "4.5.2"

        if self.is_connected and not self.local_mode_dir and self.lockdown:
            try:
                async with InstallationProxyService(self.lockdown) as ip:
                    apps = await ip.get_apps()
                    for bid, meta in apps.items():
                        if "sdvios" in bid.lower() or "stardew" in bid.lower():
                            local_game_version = meta.get("CFBundleShortVersionString", local_game_version)
                            break
            except Exception:
                pass

        # 1. Check latest SDViOS GitHub release tag
        latest_port_version = "v3.0.0"
        port_release_url = "https://github.com/DeadAKJ/SDViosport/releases"
        try:
            req = urllib.request.Request("https://api.github.com/repos/DeadAKJ/SDViosport/tags", headers={"User-Agent": "SDViOS-ModManager"})
            with urllib.request.urlopen(req, timeout=8) as resp:
                tags = json.loads(resp.read().decode())
                if tags and isinstance(tags, list):
                    latest_port_version = tags[0].get("name", latest_port_version)
        except Exception as e:
            print(f"[Backend] Port update check: {e}")

        # 2. Check latest SMAPI release on GitHub
        latest_smapi_version = "4.5.2"
        smapi_release_url = "https://github.com/Pathoschild/SMAPI/releases"
        try:
            req = urllib.request.Request("https://api.github.com/repos/Pathoschild/SMAPI/releases/latest", headers={"User-Agent": "SDViOS-ModManager"})
            with urllib.request.urlopen(req, timeout=8) as resp:
                det = json.loads(resp.read().decode())
                latest_smapi_version = det.get("tag_name", latest_smapi_version).lstrip("v")
        except Exception as e:
            print(f"[Backend] SMAPI update check: {e}")

        # 3. Check official Stardew Valley version (1.6.15 is latest official 1.6 branch)
        latest_game_version = "1.6.15"
        game_wiki_url = "https://stardewvalleywiki.com/Version_History"

        return {
            "game": {
                "name": "Stardew Valley (iOS)",
                "installed_version": local_game_version,
                "latest_version": latest_game_version,
                "has_update": is_version_newer(latest_game_version, local_game_version),
                "url": game_wiki_url,
            },
            "port": {
                "name": "SDViOS Port Engine",
                "installed_version": local_port_version,
                "latest_version": latest_port_version,
                "has_update": is_version_newer(latest_port_version, local_port_version),
                "url": port_release_url,
            },
            "smapi": {
                "name": "SMAPI for iOS",
                "installed_version": local_smapi_version,
                "latest_version": latest_smapi_version,
                "has_update": is_version_newer(latest_smapi_version, local_smapi_version),
                "url": smapi_release_url,
            }
        }

    async def check_all_mod_updates(self, mods: list[ModInfo], nexus_api: Optional[Any] = None) -> list[dict]:
        """Check online repositories (Nexus Mods, GitHub) for updates to installed mods."""
        results = []

        for m in mods:
            mod_id = m.nexus_id
            github_repo = None

            for uk in m.update_keys:
                uk_lower = uk.lower().strip()
                if uk_lower.startswith("nexus:"):
                    part = uk[6:].split("@")[0].strip()
                    if part.isdigit():
                        mod_id = int(part)
                elif uk_lower.startswith("github:"):
                    github_repo = uk[7:].strip()

            if not mod_id and m.unique_id:
                uid_lower = m.unique_id.lower()
                if uid_lower in KNOWN_FRAMEWORK_NEXUS_IDS:
                    mod_id = KNOWN_FRAMEWORK_NEXUS_IDS[uid_lower]
                elif uid_lower.startswith("flashshifter.stardewvalleyexpanded"):
                    mod_id = 3753

            latest_v = None
            url = ""
            source = "Unknown"

            if mod_id and nexus_api and getattr(nexus_api, "api_key", None):
                try:
                    det = nexus_api.get_mod_details("stardewvalley", mod_id)
                    latest_v = det.get("version")
                    url = f"https://www.nexusmods.com/stardewvalley/mods/{mod_id}"
                    source = "Nexus Mods"
                except Exception:
                    pass

            if not latest_v and github_repo:
                try:
                    req = urllib.request.Request(
                        f"https://api.github.com/repos/{github_repo}/releases/latest",
                        headers={"User-Agent": "SDViOS-ModManager"}
                    )
                    with urllib.request.urlopen(req, timeout=6) as resp:
                        det = json.loads(resp.read().decode())
                        latest_v = det.get("tag_name", "").lstrip("v")
                        url = f"https://github.com/{github_repo}/releases"
                        source = "GitHub"
                except Exception:
                    pass

            has_update = is_version_newer(latest_v, m.version) if latest_v else False

            results.append({
                "mod": m,
                "name": m.name,
                "unique_id": m.unique_id,
                "folder_name": m.folder_name,
                "installed_version": m.version,
                "latest_version": latest_v or m.version,
                "has_update": has_update,
                "url": url,
                "nexus_id": mod_id,
                "github_repo": github_repo,
                "source": source
            })

        return results
