#!/usr/bin/env node
/**
 * stardew_link.js — 农场↔家里服务器的连接线（soren-play 分支自研）
 *
 * 跑在游戏这台电脑上。零 npm 依赖，需要 Node 22+（自带 WebSocket）。
 * 它做三件事：
 *   1. 主动拨出 wss 连到咱家 relay（不开任何入站端口）
 *   2. 替服务器读 bridge_data.json / 往 actions/ 写指令文件（这就是模组的全部接口）
 *   3. 盯着桥文件里的 chat 数组，玩家在游戏里说了话就推给服务器（唤醒 AI）
 *
 * 用法：
 *   node stardew_link.js
 * 配置读环境变量（setup 脚本会写好 link.env）：
 *   STARDEW_WS      wss://soren-xie.uk/stardew/ws
 *   STARDEW_TOKEN   与 relay 侧一致的专用 token
 *   BRIDGE_DIR      指向 Mods/StardewMCPBridge（bridge_data.json 所在目录）
 */
"use strict";
const fs = require("fs");
const path = require("path");

const WS_URL = process.env.STARDEW_WS || "wss://soren-xie.uk/stardew/ws";
const TOKEN = process.env.STARDEW_TOKEN || "";
const BRIDGE_DIR = process.env.BRIDGE_DIR || path.resolve(__dirname, "../smapi-mod");
const BRIDGE_FILE = path.join(BRIDGE_DIR, "bridge_data.json");
const ACTION_DIR = path.join(BRIDGE_DIR, "actions");

if (!TOKEN) { console.error("[link] STARDEW_TOKEN 未设置，退出"); process.exit(1); }
if (typeof WebSocket === "undefined") { console.error("[link] 需要 Node 22+（自带WebSocket），当前 " + process.version); process.exit(1); }

let actionSeq = 0;
function writeAction(action) {
  fs.mkdirSync(ACTION_DIR, { recursive: true });
  const name = `${Date.now()}-${String(actionSeq++).padStart(6, "0")}.json`;
  const tmp = path.join(ACTION_DIR, name + ".tmp");
  fs.writeFileSync(tmp, JSON.stringify(action));
  fs.renameSync(tmp, path.join(ACTION_DIR, name));
}
function readBridge() {
  try { return JSON.parse(fs.readFileSync(BRIDGE_FILE, "utf-8")); }
  catch { return { error: "bridge_data.json 不可读——游戏开着吗？SMAPI模组装了吗？" }; }
}

let ws = null, alive = false, lastChatSeq = 0, chatTimer = null;

function connect() {
  console.log(`[link] 拨号 ${WS_URL} …`);
  ws = new WebSocket(WS_URL);

  ws.onopen = () => {
    ws.send(JSON.stringify({ type: "hello", token: TOKEN, agent: "stardew_link", v: "1.0" }));
  };

  ws.onmessage = (ev) => {
    let msg; try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.type === "hello_ack") {
      alive = true;
      console.log("[link] 已连上家里的服务器 ✓");
      startChatWatch();
      return;
    }
    if (msg.type === "req") {
      let res = { type: "res", id: msg.id };
      try {
        if (msg.op === "state") res.data = readBridge();
        else if (msg.op === "action") { writeAction(msg.action || {}); res.ok = true; }
        else res.error = "unknown op: " + msg.op;
      } catch (e) { res.error = String(e.message || e); }
      try { ws.send(JSON.stringify(res)); } catch {}
      return;
    }
    if (msg.type === "ping") { try { ws.send(JSON.stringify({ type: "pong" })); } catch {} }
  };

  ws.onclose = () => {
    alive = false;
    stopChatWatch();
    console.log("[link] 连接断开，5秒后重拨");
    setTimeout(connect, 5000);
  };
  ws.onerror = () => { /* onclose 会跟着触发并重连 */ };
}

// 玩家在游戏里说话 → 推给服务器。桥文件每0.5秒刷新，这里1秒看一眼足够。
function startChatWatch() {
  stopChatWatch();
  // 开局先对齐序号：登录前的旧聊天不重播
  const seed = readBridge();
  if (Array.isArray(seed.chat)) for (const c of seed.chat) if (c.seq > lastChatSeq) lastChatSeq = c.seq;
  chatTimer = setInterval(() => {
    if (!alive) return;
    const data = readBridge();
    if (!Array.isArray(data.chat)) return;
    const fresh = data.chat.filter(c => c.seq > lastChatSeq);
    if (!fresh.length) return;
    lastChatSeq = fresh[fresh.length - 1].seq;
    try { ws.send(JSON.stringify({ type: "chat", messages: fresh })); } catch {}
  }, 1000);
}
function stopChatWatch() { if (chatTimer) { clearInterval(chatTimer); chatTimer = null; } }

console.log(`[link] 桥目录: ${BRIDGE_DIR}`);
connect();
