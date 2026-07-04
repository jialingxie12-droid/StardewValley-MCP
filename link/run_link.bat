@echo off
REM 星露谷连接线 — 双击这个文件启动（先按手册把下面的 token 填好）
REM 手册：文档页 → 参考资料 → 星露谷安装手册

set STARDEW_WS=wss://soren-xie.uk/stardew/ws
set STARDEW_TOKEN=在这里粘贴token

REM BRIDGE_DIR 要指向"游戏里装好的模组文件夹"（bridge_data.json 就生成在那里面）。
REM Steam 默认路径如下，如果你的游戏装在别处就照着改：
set BRIDGE_DIR=C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\Mods\StardewMCPBridge

node "%~dp0stardew_link.js"
pause
