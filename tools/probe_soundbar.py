"""Diagnostic probe for Sony BRAVIA Connect soundbars.

Connects to the soundbar, discovers all audio capabilities, and prints
the current live telemetry without modifying device settings.
"""

from __future__ import annotations

import json
import logging
import sys
from pathlib import Path

# Add project root to sys.path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from pybravia_connect import BraviaConnectClient, load_credentials
from src.discovery import discover_one
from src.icon_manager import classify_codec, human_readable_codec


def main() -> None:
    logging.basicConfig(level=logging.WARNING)
    root = Path(__file__).resolve().parent.parent
    keys_path = root / "session_keys.json"

    if not keys_path.exists():
        print(f"ERROR: Session keys not found at {keys_path}", file=sys.stderr)
        print("Run: bravia-connect-keys --login --open -o session_keys.json", file=sys.stderr)
        sys.exit(1)

    creds = load_credentials(keys_path)
    result, source = discover_one(timeout=6, fallback_port=55051)
    if result is None:
        print("ERROR: No soundbar discovered on local network.", file=sys.stderr)
        sys.exit(2)

    print(f"Target: {result.label} (source: {source})")
    client = BraviaConnectClient(
        host=result.ip or result.hostname.split(".")[0],
        port=result.port,
        device_id=creds["device_id"],
        hmac_key=creds["hmac_key"],
        key_id=creds.get("key_id"),
        session_key=creds.get("session_key"),
    )
    client.connect(timeout=10.0)

    try:
        caps = client.get_capabilities(timeout=10.0)
        af_caps = caps.get("playback_control.audio_format")
        if af_caps:
            print("\nSupported audio_format values:")
            for val in af_caps.values or ():
                kind = classify_codec(val)
                label = human_readable_codec(val)
                print(f"  - {val:<26} -> kind={kind:<8} ({label})")

        states = client.get_states(
            [
                "playback_control.audio_format",
                "playback_control.audio_channel",
                "volume",
                "power",
                "mute",
                "sound_setting.night_mode",
                "sound_setting.sound_field",
                "sound_setting.sound_effect",
            ],
            timeout=10.0,
        )
        print("\nLive State:")
        print(json.dumps(states, indent=2, default=str))

        cur_codec = states.get("playback_control.audio_format")
        cur_chan = states.get("playback_control.audio_channel")
        print(f"\nActive Codec Classification: {classify_codec(cur_codec)}")
        print(f"Active Tooltip Label:        {human_readable_codec(cur_codec, cur_chan)}")

    finally:
        client.close()


if __name__ == "__main__":
    main()
