@echo off
REM 星露谷连接线 — 双击这个文件启动（先按手册把下面的 token 填好）
REM 手册：文档页 → 参考资料 → 星露谷安装手册
REM v2：模组换成 NagiBridge 后不再需要 BRIDGE_DIR，游戏本地API默认 7842

set STARDEW_WS=wss://soren-xie.uk/stardew/ws
set STARDEW_TOKEN=在这里粘贴token

REM 如果苏林玩的是二号玩家(farmhand)实例，把下面一行行首的 REM 去掉：
REM set NAGI_URL=http://127.0.0.1:7843

node "%~dp0stardew_link.js"
pause
