@echo off
REM Stardew Valley Link - double click to start
REM Paste your token below (replace the placeholder)

set STARDEW_WS=wss://soren-xie.uk/stardew/ws
set STARDEW_TOKEN=paste-token-here

REM Farmhand instance (player 2): uncomment next line
REM set NAGI_URL=http://127.0.0.1:7843

node "%~dp0stardew_link.js"
pause
