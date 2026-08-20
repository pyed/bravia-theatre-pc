# Tray Icon Assets & Custom Overrides
#
# Every codec state has a crisp, pre-built 256x256 master icon and a procedural
# badge fallback generated with Pillow.
#
# Custom artwork dropped into this folder will automatically take priority:
#
#   atmos.png         -> Dolby Atmos family (dolby_atmos_mat, dolby_atmos_digital_plus)
#   atmos_truehd.png  -> Dolby Atmos TrueHD (dolby_atmos_truehd)
#   truehd.png        -> Dolby TrueHD (dolby_digital_truehd)
#   ddplus.png        -> Dolby Digital Plus & Dolby MAT (dolby_digital_plus, dolby_mat)
#   dd.png            -> Dolby Digital (dolby_digital)
#   dtsx.png          -> DTS:X family (dts_x, dts_x_master_audio, imax_dts_x)
#   dtshd.png         -> DTS-HD family (dts_hd_master_audio, dts_hd_high_resolution)
#   dts.png           -> Standard DTS family (dts, dts_es_*, dts_96_24, dts_express)
#   imax.png          -> IMAX Enhanced DTS (imax_dts)
#   pcm.png / lpcm.png-> Uncompressed LPCM / Multichannel PCM
#   aac.png           -> MPEG-2 / MPEG-4 AAC
#   360ra.png         -> Sony 360 Reality Audio
#   idle.png          -> Standby / Unknown / Inactive
#
# Recommended source size: 256x256 RGBA PNG.
# Run `python3 build.py` from this folder to regenerate all master assets from raw/.
