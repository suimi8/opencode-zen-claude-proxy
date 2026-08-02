@echo off
cd /d "%~dp0"
setlocal
for /f "usebackq tokens=1,* delims==" %%a in (".env.local") do set "%%a=%%b"
node zen-proxy-launch.js
