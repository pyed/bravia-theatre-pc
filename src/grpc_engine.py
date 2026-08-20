"""Single background gRPC engine for the BRAVIA Theatre.

Everything — telemetry (notify wiretap) and control (ExecCommand) — flows
through ONE :class:`pybravia_connect.BraviaConnectClient` on port 55051.
No secondary sockets, no legacy TCP.

Threading model
---------------
* ``GrpcEngine.run()`` executes on the engine's daemon thread (started by
  :class:`~src.app.BraviaApp`). It owns the full
  discover -> connect -> subscribe -> snapshot -> idle-poll loop and
  implements exponential-backoff reconnection.
* The Qt UI thread never touches the client directly. It calls the
  public ``cmd_*`` methods, which enqueue (path, value) items on a
  ``threading.Queue`` that the engine drains between events.
* The library's own notify worker thread (sparked by
  ``client.start_notify(...)``) delivers deltas to
  :meth:`GrpcEngine._on_delta`, which updates the thread-safe state cache
  and pushes updates to the UI callback (invoked with the lock released).
"""

from __future__ import annotations

import logging
import queue
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Optional, Tuple

import pybravia_connect
from pybravia_connect import (
    AuthError,
    BraviaConnectClient,
    ConnectionError,
    load_credentials,
)

_LOGGER = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# gRPC field paths (observed on HT-A9000 / BRAVIA Theatre Bar 9)
# ---------------------------------------------------------------------------

PATH_AUDIO_FORMAT = "playback_control.audio_format"
PATH_AUDIO_CHANNEL = "playback_control.audio_channel"
PATH_VOLUME = "volume"
PATH_POWER = "power"
PATH_MUTE = "mute"
PATH_NIGHT_MODE = "sound_setting.night_mode"
PATH_SOUND_FIELD = "sound_setting.sound_field"

#: Paths pulled up-front via GetStatesWithAuth (capability-safe on Theatre).
_SNAPSHOT_PATHS = [
    PATH_AUDIO_FORMAT,
    PATH_AUDIO_CHANNEL,
    PATH_VOLUME,
    PATH_POWER,
    PATH_MUTE,
    PATH_NIGHT_MODE,
    PATH_SOUND_FIELD,
]

DEFAULT_PORT = 55051


def _log_setup(log_file: Path, level: int = logging.INFO) -> None:
    """Configure root logging: console + rotating-ish file next to the app."""
    fmt = logging.Formatter(
        "%(asctime)s %(levelname)-7s %(name)s: %(message)s", "%H:%M:%S"
    )
    root = logging.getLogger()
    root.setLevel(level)
    handlers: list[logging.Handler] = []
    stream = logging.StreamHandler()
    stream.setFormatter(fmt)
    handlers.append(stream)
    try:
        fileh = logging.FileHandler(log_file, encoding="utf-8")
        fileh.setFormatter(fmt)
        handlers.append(fileh)
    except OSError:
        pass
    for h in handlers:
        if h not in root.handlers:
            root.addHandler(h)


@dataclass
class EngineConfig:
    """Everything the engine needs, resolved up-front by the app."""

    host: str
    port: int
    credentials: dict
    log_file: Path
    reconnect_min: float = 5.0
    reconnect_max: float = 60.0
    idle_poll_seconds: float = 45.0


class GrpcEngine:
    """Thread-safe bridge between the UI and the pybravia-connect client."""

    def __init__(
        self,
        config: EngineConfig,
        on_update: Callable[[dict[str, Any]], None],
        on_connection_state: Optional[Callable[[bool, str], None]] = None,
    ) -> None:
        self.cfg = config
        self.on_update = on_update
        self.on_connection_state = on_connection_state or (lambda up, why: None)

        self._cmd_q: queue.Queue[Tuple[str, Any]] = queue.Queue()
        self._reconnect_requested = threading.Event()
        self._state: dict[str, Any] = {
            "codec": None,
            "channel": None,
            "volume": 0,
            "power": False,
            "mute": False,
            "night_mode": False,
            "sound_field": False,
        }
        self._state_lock = threading.Lock()
        self._stop = threading.Event()
        self._worker: Optional[threading.Thread] = None
        self._client: Optional[BraviaConnectClient] = None

        self.start()

    # ------------------------------------------------------------------ UI API

    def snapshot(self) -> dict[str, Any]:
        """Thread-safe copy of the latest known device state."""
        with self._state_lock:
            return dict(self._state)

    def _emit(self) -> dict[str, Any]:
        snap = self.snapshot()
        try:
            self.on_update(snap)
        except Exception:  # noqa: BLE001
            _LOGGER.exception("on_update callback failed")
        return snap

    def _enqueue_command(self, path: str, value: Any) -> None:
        """Thread-safe enqueueing with deduplication for rapid volume commands."""
        if path == PATH_VOLUME:
            # Drain any pending unexecuted volume requests so we only send the latest target
            with self._cmd_q.mutex:
                # deque object in queue.Queue
                new_items = [item for item in self._cmd_q.queue if item[0] != PATH_VOLUME]
                self._cmd_q.queue.clear()
                self._cmd_q.queue.extend(new_items)
        self._cmd_q.put((path, value))

    # -- command entry points (UI thread) -----------------------------------

    def cmd_volume_up(self, step: int = 1) -> bool:
        """Volume up. Returns False when the state is not connected/on yet."""
        with self._state_lock:
            current = self._state["volume"]
            power = self._state["power"]
            if not power or current is None:
                return False
            target = max(0, min(100, int(current) + step))
            if target == int(current):
                return False
            self._state["volume"] = target  # optimistic
            muted = self._state["mute"]
        if muted:
            self._enqueue_command(PATH_MUTE, False)
        self._enqueue_command(PATH_VOLUME, target)
        self._emit()
        return True

    def cmd_volume_down(self, step: int = 1) -> bool:
        with self._state_lock:
            current = self._state["volume"]
            power = self._state["power"]
            if not power or current is None:
                return False
            target = max(0, min(100, int(current) - step))
            if target == int(current):
                return False
            self._state["volume"] = target  # optimistic
            muted = self._state["mute"]
        if muted:
            self._enqueue_command(PATH_MUTE, False)
        self._enqueue_command(PATH_VOLUME, target)
        self._emit()
        return True

    def cmd_set_volume(self, target: int) -> bool:
        """Set volume directly (0..100). Returns False when the soundbar is not on."""
        with self._state_lock:
            power = self._state["power"]
            if not power:
                return False
            target = max(0, min(100, int(target)))
            if target == self._state["volume"]:
                return False
            self._state["volume"] = target  # optimistic
            muted = self._state["mute"]
        if muted:
            self._enqueue_command(PATH_MUTE, False)
        self._enqueue_command(PATH_VOLUME, target)
        self._emit()
        return True

    def cmd_toggle_mute(self) -> bool:
        with self._state_lock:
            if not self._state["power"]:
                return False
            self._state["mute"] = not self._state["mute"]
            target = self._state["mute"]
        self._enqueue_command(PATH_MUTE, target)
        self._emit()
        return True

    def cmd_toggle_night_mode(self) -> bool:
        with self._state_lock:
            if not self._state["power"]:
                return False
            self._state["night_mode"] = not self._state["night_mode"]
            target = self._state["night_mode"]
        self._enqueue_command(PATH_NIGHT_MODE, target)
        self._emit()
        return True

    def cmd_toggle_sound_field(self) -> bool:
        with self._state_lock:
            if not self._state["power"]:
                return False
            self._state["sound_field"] = not self._state["sound_field"]
            target = self._state["sound_field"]
        self._enqueue_command(PATH_SOUND_FIELD, target)
        self._emit()
        return True

    def cmd_toggle_power(self) -> bool:
        with self._state_lock:
            self._state["power"] = not self._state["power"]
            target = self._state["power"]
            if target:  # waking up: reset transient UI state
                self._state["mute"] = False
        self._enqueue_command(PATH_POWER, target)
        self._emit()
        return True

    # ----------------------------------------------------------- worker entry

    def stop(self) -> None:
        """Ask the engine to shut down and join its threads."""
        self._stop.set()
        t = self._worker
        if t and t.is_alive():
            t.join(timeout=5.0)
        self._teardown_client()

    def start(self) -> None:
        if self._worker and self._worker.is_alive():
            return
        self._worker = threading.Thread(
            target=self._run_forever, name="bravia-engine", daemon=True
        )
        self._worker.start()

    def _run_forever(self) -> None:
        _log_setup(self.cfg.log_file)
        _LOGGER.info(
            "Engine starting for %s:%s", self.cfg.host, self.cfg.port
        )
        self.on_connection_state(False, "connecting")
        backoff = self.cfg.reconnect_min
        while not self._stop.is_set():
            try:
                self._connect_once()
            except Exception as exc:  # noqa: BLE001
                _LOGGER.warning("Connect failed (%s); retry in %.0fs", exc, backoff)
                self.on_connection_state(False, str(exc))
                self._sleep_backoff(backoff)
                backoff = min(backoff * 2, self.cfg.reconnect_max)
                continue
            # Connected — run the command drain loop until disconnect/stop.
            backoff = self.cfg.reconnect_min
            while not self._stop.is_set():
                try:
                    self._drain_commands(self.cfg.idle_poll_seconds)
                except AuthError as exc:
                    _LOGGER.error("Auth error: %s", exc)
                    self.on_connection_state(False, "auth error — re-login needed")
                    break
                except ConnectionError as exc:
                    _LOGGER.warning("Connection lost: %s", exc)
                    self.on_connection_state(False, "reconnecting")
                    self._teardown_client()
                    break
                except Exception as exc:  # noqa: BLE001
                    _LOGGER.exception("Engine error: %s", exc)
                    self._teardown_client()
                    self.on_connection_state(False, str(exc))
                    break
            self._teardown_client()
            if self._stop.is_set():
                break
            self._sleep_backoff(backoff)
            backoff = min(backoff * 2, self.cfg.reconnect_max)
        self.on_connection_state(False, "stopped")

    def _sleep_backoff(self, seconds: float) -> None:
        deadline = time.monotonic() + seconds
        while not self._stop.is_set() and time.monotonic() < deadline:
            time.sleep(0.25)

    # ---------------------------------------------------------- connect/setup

    def _teardown_client(self) -> None:
        client, self._client = self._client, None
        if client is None:
            return
        try:
            client.stop_notify()
            client.close()
        except Exception:  # noqa: BLE001
            pass

    def _connect_once(self) -> None:
        """Build the client, authenticate, subscribe and take a snapshot."""
        creds = self.cfg.credentials
        client = BraviaConnectClient(
            host=self.cfg.host,
            port=self.cfg.port,
            device_id=creds["device_id"],
            hmac_key=creds["hmac_key"],
            key_id=creds.get("key_id"),
            session_key=creds.get("session_key"),
        )
        self.on_connection_state(False, f"connecting {self.cfg.host}:{self.cfg.port}")
        client.connect(timeout=10.0)
        self._client = client
        self._reconnect_requested.clear()
        try:
            client.get_capabilities(timeout=10.0)
        except Exception as exc:  # noqa: BLE001
            _LOGGER.warning("get_capabilities failed (continuing): %s", exc)
        client.start_notify(
            self._on_delta,
            on_connection_lost=self._on_notify_lost,
        )
        # Initial state pull so the UI is correct before the first delta.
        try:
            states = client.get_states(_SNAPSHOT_PATHS, timeout=10.0)
            self._apply_states(states)
            self._emit()
        except Exception as exc:  # noqa: BLE001
            _LOGGER.warning("Initial get_states failed (notify will fill in): %s", exc)
        self.on_connection_state(True, f"{self.cfg.host}:{self.cfg.port}")

    def _on_notify_lost(self) -> None:
        """Library reports the notify stream died 3x — ask the loop to reconnect.

        Runs on the library's notify worker thread: must NOT join/teardown the
        client here (that would join the calling thread), so we only set a
        flag the drain loop observes within ~1s.
        """
        _LOGGER.warning("Notify stream lost — requesting reconnect")
        self._reconnect_requested.set()
        self.on_connection_state(False, "notify stream lost")

    # ---------------------------------------------------------------- deltas

    def _on_delta(self, path: str, value: Any) -> None:
        changed = False
        with self._state_lock:
            if path == PATH_AUDIO_FORMAT:
                self._state["codec"] = value
                changed = True
            elif path == PATH_AUDIO_CHANNEL:
                self._state["channel"] = value
                changed = True
            elif path == PATH_VOLUME:
                try:
                    self._state["volume"] = int(value)
                    changed = True
                except (TypeError, ValueError):
                    pass
            elif path == PATH_POWER:
                self._state["power"] = bool(value)
                changed = True
            elif path == PATH_MUTE:
                self._state["mute"] = bool(value)
                changed = True
            elif path == PATH_NIGHT_MODE:
                self._state["night_mode"] = bool(value)
                changed = True
            elif path == PATH_SOUND_FIELD:
                self._state["sound_field"] = bool(value)
                changed = True
            elif path == "playback_control.power":  # TV-side power echo, if any
                self._state["power"] = bool(value)
                changed = True
        if changed:
            self._emit()

    def _apply_states(self, states: dict[str, Any]) -> None:
        mapping = {
            PATH_AUDIO_FORMAT: "codec",
            PATH_AUDIO_CHANNEL: "channel",
            PATH_VOLUME: "volume",
            PATH_POWER: "power",
            PATH_MUTE: "mute",
            PATH_NIGHT_MODE: "night_mode",
            PATH_SOUND_FIELD: "sound_field",
        }
        with self._state_lock:
            for path, key in mapping.items():
                if path in states:
                    self._state[key] = states[path]

    # ------------------------------------------------------------ cmd draining

    def _drain_commands(self, idle_timeout: float) -> None:
        """Wait for UI commands and forward them; idle-poll for liveness."""
        idle_deadline = time.monotonic() + idle_timeout
        while not self._stop.is_set():
            if self._reconnect_requested.is_set():
                raise ConnectionError("notify stream lost")
            remaining = idle_deadline - time.monotonic()
            if remaining <= 0:
                self._idle_poll()
                idle_deadline = time.monotonic() + idle_timeout
                continue
            try:
                path, value = self._cmd_q.get(timeout=min(remaining, 1.0))
            except queue.Empty:
                continue
            self._send_command(path, value)

    def _send_command(self, path: str, value: Any) -> None:
        client = self._client
        if client is None:
            raise ConnectionError("no client")
        try:
            ok = client.exec_command(path, value)
        except Exception as exc:  # noqa: BLE001
            _LOGGER.warning("exec_command(%s, %r) raised: %s", path, value, exc)
            return
        if not ok:
            _LOGGER.warning("exec_command(%s, %r) returned False", path, value)

    def _idle_poll(self) -> None:
        """Cheap liveness check while the UI is idle; surfaces dead links."""
        client = self._client
        if client is None:
            return
        try:
            client.get_states([PATH_VOLUME, PATH_POWER, PATH_MUTE], timeout=5.0)
        except Exception as exc:  # noqa: BLE001
            _LOGGER.info("Idle poll failed (reconnect will engage): %s", exc)
            raise ConnectionError(f"idle poll: {exc}") from exc
