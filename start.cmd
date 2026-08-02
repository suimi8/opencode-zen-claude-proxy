@echo off
cd /d "%~dp0"
"C:\Program Files\nodejs\node.exe" src/server.js > proxy.log 2> proxy.err.log
