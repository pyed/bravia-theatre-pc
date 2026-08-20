@echo off
title BRAVIA Theatre PC
cd /d "%~dp0"
echo Starting Sony BRAVIA Theatre PC Controller...
python3 src/app.py
if errorlevel 1 (
    echo.
    echo Application exited with an error.
    pause
)
