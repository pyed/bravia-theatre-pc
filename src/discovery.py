"""mDNS / zeroconf auto-discovery for Sony Smart Home (BRAVIA Connect) devices.

The 2024+ Sony Home Audio / BRAVIA Theatre line advertises
``_sonysmarthome._tcp.local.`` with a ``txt`` payload that carries the
friendly name (``imName``), the LAN IP and the encrypted gRPC control port
(55051 on the Theatre line, e.g. the BRAVIA Theatre Bar 9 / HT-A9000).

Discovery uses zeroconf's *synchronous* API and is designed to run from any
thread (app.py calls it once at startup); it never blocks the Qt UI
loop and never blocks the gRPC worker.
"""

from __future__ import annotations

import logging
import socket
import threading
import time
from dataclasses import dataclass
from typing import Any, Callable, Optional

_LOGGER = logging.getLogger(__name__)

#: Service type broadcast by Sony Smart Home / BRAVIA Connect devices.
SONY_SMART_HOME_TYPE = "_sonysmarthome._tcp.local."

#: Default Theatre gRPC port (fixed on the HT-A9000 line).
DEFAULT_PORT = 55051


@dataclass
class DiscoveryResult:
    """One discovered Sony Smart Home service instance."""

    name: str        # mDNS instance name, e.g. "HT-A9000"
    hostname: str    # full mDNS instance name (name + ".local.")
    ip: str          # IPv4 address from the A record ("" if unresolved)
    port: int        # TCP port from TXT (default 55051)
    txt: dict        # decoded TXT payload (imName, ip, port, mac, ...)

    @property
    def label(self) -> str:
        return f"{self.name} @ {self.ip}:{self.port}"


def _decode_txt(raw: dict[str, bytes] | None) -> dict[str, str]:
    """Decode a zeroconf TXT dict into plain str -> str.

    Recent zeroconf versions return *bytes keys* (older ones use str keys),
    so both dimensions are decoded defensively.
    """
    out: dict[str, str] = {}
    for key, value in (raw or {}).items():
        k = key.decode("utf-8", "replace") if isinstance(key, bytes) else key
        if value is None:
            continue
        if isinstance(value, bytes):
            try:
                out[k] = value.decode("utf-8")
            except UnicodeDecodeError:
                out[k] = value.decode("latin-1", "replace")
        else:
            out[k] = str(value)
    return out


def _extract_ip(info: Any) -> str:
    """Best-effort IPv4 extraction from a ServiceInfo.

    ``info.addresses`` is a list of plain IPv4 strings in recent zeroconf
    versions, and a list of packed 4-byte ``bytes`` in older ones.
    """
    for addr in getattr(info, "addresses", None) or ():
        if isinstance(addr, bytes):
            if len(addr) == 4:  # packed IPv4
                return ".".join(str(b) for b in addr)
        elif addr:
            return str(addr)
    return ""


def _run_browse(timeout: float, on_found: Callable[[DiscoveryResult], None]) -> list[DiscoveryResult]:
    """Browse for SONY_SMART_HOME_TYPE synchronously until ``timeout``."""
    from zeroconf import ServiceBrowser, ServiceStateChange, Zeroconf

    zc = Zeroconf()
    found: list[DiscoveryResult] = []
    found_lock = threading.Lock()
    any_found = threading.Event()

    def _handler(
        zeroconf: Any = None,
        service_type: str = "",
        name: str = "",
        state: ServiceStateChange = None,
        state_change: ServiceStateChange = None,
    ) -> None:
        # zeroconf 0.150 fires handlers with keyword args; older versions
        # used positional. Accept both spellings of the state argument.
        state = state or state_change
        if state is not ServiceStateChange.Added:
            return
        try:
            # Synchronous blocking lookup; retry once if the response raced us.
            info = zc.get_service_info(service_type, name, 1500)
            if info is None:
                info = zc.get_service_info(service_type, name, 1500)
            if info is None:
                return
            txt = _decode_txt(info.properties)
            ip = (
                _extract_ip(info)
                or txt.get("ipAddr", "")
                or txt.get("ip", "")
            )
            port = int(txt.get("port", 0)) or DEFAULT_PORT
            result = DiscoveryResult(
                name=txt.get("imName") or (name.split(".")[0] if name else name),
                hostname=name,
                ip=ip,
                port=port,
                txt=txt,
            )
            with found_lock:
                if any(f.name == result.name and f.ip == result.ip for f in found):
                    return
                found.append(result)
            _LOGGER.info("mDNS found %s", result.label)
            on_found(result)
            any_found.set()
        except Exception:  # noqa: BLE001 - one bad instance must not kill the browse
            _LOGGER.debug("Could not resolve mDNS instance %r", name, exc_info=True)

    # delay=250ms: fire the first query quickly instead of zeroconf's default
    # 10s coalescing delay (irrelevant for us, we're the only browser here).
    browser = ServiceBrowser(zc, SONY_SMART_HOME_TYPE, handlers=[_handler], delay=250)
    try:
        # Wait for the first hit, or for the whole window to elapse.
        any_found.wait(timeout)
        # Give the resolver a moment to finish populating late instances.
        time.sleep(0.5)
    finally:
        browser.cancel()
        zc.close()
    return found


def discover_devices(
    timeout: float = 6.0,
    on_found: Optional[Callable[[DiscoveryResult], None]] = None,
) -> list[DiscoveryResult]:
    """Synchronously discover Sony Smart Home devices via mDNS.

    Blocks for at most ~``timeout`` seconds. Returns every unique instance
    found; an empty list means nothing was found (callers should fall back
    to the configured IP). Never raises.
    """
    on_found = on_found or (lambda _r: None)
    try:
        import zeroconf  # noqa: F401 - fail fast with a clear message if missing
        return _run_browse(timeout, on_found)
    except Exception as exc:  # noqa: BLE001
        _LOGGER.warning("mDNS discovery failed (%s) — falling back to config", exc)
        return []


def discover_one(
    timeout: float = 6.0,
    fallback_host: Optional[str] = None,
    fallback_port: int = DEFAULT_PORT,
) -> tuple[Optional[DiscoveryResult], str]:
    """Discover a single soundbar.

    Returns ``(result, source)`` where ``source`` is one of
    ``"mDNS"``, ``"config-fallback"`` or ``"none"``. ``result`` is ``None``
    when discovery failed AND no fallback IP was configured.
    """
    if not fallback_host:
        results = discover_devices(timeout)
        if results:
            return results[0], "mDNS"
        return None, "none"

    # Fallback IP known: prefer mDNS, but fall back to the configured IP.
    results = discover_devices(timeout, on_found=lambda r: None)
    if results:
        return results[0], "mDNS"
    return (
        DiscoveryResult(
            name="configured",
            hostname=fallback_host,
            ip=fallback_host,
            port=fallback_port,
            txt={},
        ),
        "config-fallback",
    )


def verify_host_reachable(host: str, port: int, timeout: float = 2.0) -> bool:
    """Cheap TCP pre-flight so we don't hand the engine a dead address."""
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False
