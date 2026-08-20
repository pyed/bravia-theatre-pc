"""Unit tests for Sony BRAVIA Theatre Tray Controller."""

import sys
import tempfile
import unittest
from pathlib import Path

ROOT_DIR = Path(__file__).resolve().parent.parent
if str(ROOT_DIR) not in sys.path:
    sys.path.insert(0, str(ROOT_DIR))

from PIL import Image

from src.icon_manager import (
    CODEC_KINDS,
    IconManager,
    classify_codec,
    human_readable_codec,
    normalize_codec,
)

ASSETS_DIR = ROOT_DIR / "assets" / "icons"


class TestCodecClassification(unittest.TestCase):
    def test_dolby_atmos_codecs(self):
        self.assertEqual(classify_codec("dolby_atmos_truehd"), "atmos_truehd")
        self.assertEqual(classify_codec("dolby_atmos_mat"), "atmos")
        self.assertEqual(classify_codec("dolby_atmos_digital_plus"), "atmos")
        for token in ("dolby_atmos_truehd", "dolby_atmos_mat", "dolby_atmos_digital_plus"):
            self.assertIn("Atmos", human_readable_codec(token))

    def test_dolby_truehd(self):
        self.assertEqual(classify_codec("dolby_digital_truehd"), "truehd")
        self.assertEqual(human_readable_codec("dolby_digital_truehd"), "Dolby TrueHD")

    def test_dolby_digital_plus_and_mat(self):
        self.assertEqual(classify_codec("dolby_digital_plus"), "ddplus")
        self.assertEqual(human_readable_codec("dolby_digital_plus"), "Dolby Digital Plus")
        self.assertEqual(classify_codec("dolby_mat"), "ddplus")
        self.assertEqual(human_readable_codec("dolby_mat"), "Dolby MAT")

    def test_dolby_digital(self):
        self.assertEqual(classify_codec("dolby_digital"), "dd")
        self.assertEqual(human_readable_codec("dolby_digital"), "Dolby Digital")

    def test_dts_x_family(self):
        self.assertEqual(classify_codec("dts_x"), "dtsx")
        self.assertEqual(classify_codec("dts_x_master_audio"), "dtsx")
        self.assertEqual(classify_codec("imax_dts_x"), "dtsx")
        self.assertIn("DTS:X", human_readable_codec("dts_x"))

    def test_dts_hd_family(self):
        self.assertEqual(classify_codec("dts_hd_master_audio"), "dtshd")
        self.assertEqual(classify_codec("dts_hd_high_resolution"), "dtshd")
        self.assertIn("DTS-HD", human_readable_codec("dts_hd_master_audio"))

    def test_dts_legacy_family(self):
        for token in ("dts", "dts_es_6.1_matrix", "dts_es_6.1_discrete", "dts_96_24", "dts_express"):
            self.assertEqual(classify_codec(token), "dts")
            self.assertIn("DTS", human_readable_codec(token))

    def test_imax(self):
        self.assertEqual(classify_codec("imax_dts"), "imax")
        self.assertIn("IMAX", human_readable_codec("imax_dts"))

    def test_pcm(self):
        self.assertEqual(classify_codec("lpcm"), "pcm")
        self.assertEqual(classify_codec("multichannel_pcm"), "pcm")
        self.assertEqual(human_readable_codec("lpcm"), "LPCM")
        self.assertEqual(human_readable_codec("multichannel_pcm"), "Multichannel PCM")

    def test_aac(self):
        self.assertEqual(classify_codec("mpeg-2_aac"), "aac")
        self.assertEqual(classify_codec("mpeg-4_aac"), "aac")

    def test_360ra(self):
        self.assertEqual(classify_codec("360ra"), "360ra")
        self.assertEqual(human_readable_codec("360ra"), "360 Reality Audio")

    def test_idle_and_unknown(self):
        self.assertEqual(classify_codec("unknown"), "idle")
        self.assertEqual(classify_codec("imax_off"), "idle")
        self.assertEqual(classify_codec(""), "idle")
        self.assertEqual(classify_codec(None), "idle")

    def test_human_readable_with_channel(self):
        self.assertEqual(human_readable_codec("lpcm", "2.0"), "LPCM (2.0 ch)")
        self.assertEqual(human_readable_codec("dolby_digital", "5.1"), "Dolby Digital (5.1 ch)")
        self.assertEqual(human_readable_codec("dolby_atmos_mat", "7.1.4"), "Dolby Atmos (MAT) (7.1.4 ch)")
        self.assertEqual(human_readable_codec("unknown", "2.0"), "Unknown (2.0 ch)")
        self.assertEqual(human_readable_codec("lpcm", None), "LPCM")


class TestIconManager(unittest.TestCase):
    def test_all_custom_assets_present(self):
        im = IconManager(ASSETS_DIR)
        active = im.active_assets
        for kind in CODEC_KINDS:
            self.assertIsNotNone(active.get(kind), f"Custom asset missing for kind: {kind}")
            path = active[kind]
            self.assertTrue(path.exists(), f"Asset path does not exist: {path}")

    def test_image_generation_all_sizes(self):
        im = IconManager(ASSETS_DIR)
        for kind in CODEC_KINDS:
            for size in (16, 24, 32, 48, 64, 128, 256):
                img = im.image_for(kind, size=size)
                self.assertIsInstance(img, Image.Image)
                self.assertEqual(img.size, (size, size))
                self.assertEqual(img.mode, "RGBA")

    def test_procedural_fallback_without_assets(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            im = IconManager(Path(tmpdir))
            for kind in CODEC_KINDS:
                img = im.image_for(kind, size=64)
                self.assertIsInstance(img, Image.Image)
                self.assertEqual(img.size, (64, 64))
                self.assertEqual(img.mode, "RGBA")


class MockEngine:
    """Mock engine for testing UI components without live network connections."""
    def __init__(self):
        from src.grpc_engine import EngineConfig
        self.cfg = EngineConfig("192.168.1.118", 55051, {}, Path("test.log"))
        self._state = {
            "codec": "dolby_atmos_mat",
            "channel": "7.1.4",
            "volume": 42,
            "power": True,
            "mute": False,
            "night_mode": False,
            "sound_field": True,
        }
        self.commands_sent = []

    def snapshot(self):
        return dict(self._state)

    def cmd_set_volume(self, val):
        self._state["volume"] = val
        self.commands_sent.append(("volume", val))
        return True

    def cmd_toggle_power(self):
        self._state["power"] = not self._state["power"]
        self.commands_sent.append(("power", self._state["power"]))
        return True

    def cmd_toggle_mute(self):
        self._state["mute"] = not self._state["mute"]
        self.commands_sent.append(("mute", self._state["mute"]))
        return True

    def cmd_toggle_night_mode(self):
        self._state["night_mode"] = not self._state["night_mode"]
        self.commands_sent.append(("night_mode", self._state["night_mode"]))
        return True

    def cmd_toggle_sound_field(self):
        self._state["sound_field"] = not self._state["sound_field"]
        self.commands_sent.append(("sound_field", self._state["sound_field"]))
        return True


class TestFlyoutUI(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from PySide6.QtWidgets import QApplication
        cls.app = QApplication.instance() or QApplication(["-platform", "offscreen"])

    def test_flyout_initialization_and_state_updates(self):
        from src.flyout_ui import FlyoutWindow
        mock_engine = MockEngine()
        im = IconManager(ASSETS_DIR)
        flyout = FlyoutWindow(mock_engine, im, "Sony BRAVIA Theatre Bar 9")

        # Push state and verify widget texts
        snap = mock_engine.snapshot()
        flyout.push_state(snap, conn_up=True)
        self.app.processEvents()

        self.assertEqual(flyout.codec_label.text(), "Dolby Atmos (MAT) (7.1.4 ch)")
        self.assertEqual(flyout.volume_num.text(), "42")
        self.assertTrue(flyout.tile_power._is_active)
        self.assertTrue(flyout.tile_sf._is_active)
        self.assertFalse(flyout.tile_night._is_active)

        # Push standby state
        snap["power"] = False
        flyout.push_state(snap, conn_up=True)
        self.app.processEvents()

        self.assertIn("Standby", flyout.codec_label.text())
        self.assertFalse(flyout.tile_power._is_active)

    def test_volume_slider_updates(self):
        from src.flyout_ui import FlyoutWindow
        mock_engine = MockEngine()
        im = IconManager(ASSETS_DIR)
        flyout = FlyoutWindow(mock_engine, im)

        flyout.volume_slider.setValue(65)
        self.app.processEvents()
        flyout._on_slider_released()

        self.assertIn(("volume", 65), mock_engine.commands_sent)


if __name__ == "__main__":
    unittest.main()
