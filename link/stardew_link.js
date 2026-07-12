#!/usr/bin/env node
/**
 * stardew_link.js — 农场↔家里服务器的连接线（soren-play 分支自研）
 * v2（2026-07-10）：模组换成 NagiBridge 后，本脚本从"读写桥文件"改成
 * "哑 HTTP 代理"——服务器要什么就转发给游戏本地的 NagiBridge HTTP API，
 * 17 个游戏工具全部住在服务器侧，以后加功能不用再改这台电脑上的任何东西。
 *
 * 跑在游戏这台电脑上。零 npm 依赖，需要 Node 22+（自带 WebSocket 和 fetch）。
 * 它做三件事：
 *   1. 主动拨出 wss 连到咱家 relay（不开任何入站端口）
 *   2. 把服务器的 req 转发到 NagiBridge（http://127.0.0.1:7842）并回传结果
 *   3. 每秒看一眼 /chat/history，玩家在游戏里说了新话就推给服务器（唤醒 AI）
 *
 * 用法：
 *   node stardew_link.js
 * 配置读环境变量（setup 脚本会写好 link.env）：
 *   STARDEW_WS      wss://soren-xie.uk/stardew/ws
 *   STARDEW_TOKEN   与 relay 侧一致的专用 token
 *   NAGI_URL        NagiBridge 地址，默认 http://127.0.0.1:7842（农场主）
 *                   二号玩家(farmhand)的游戏实例是 7843
 */
"use strict";

const WS_URL = process.env.STARDEW_WS || "wss://soren-xie.uk/stardew/ws";
const TOKEN = process.env.STARDEW_TOKEN || "";
const NAGI_URL = (process.env.NAGI_URL || "http://localhost:7842").replace(/\/$/, "");
const NAGI_FARMHAND = (process.env.NAGI_FARMHAND || "http://localhost:7843").replace(/\/$/, "");

if (!TOKEN) { console.error("[link] STARDEW_TOKEN not set"); process.exit(1); }
if (typeof WebSocket === "undefined" || typeof fetch === "undefined") {
  console.error("[link] Need Node 22+ (built-in WebSocket+fetch), got " + process.version);
  process.exit(1);
}

async function nagi(method, path, body, farmhand) {
  const base = farmhand ? NAGI_FARMHAND : NAGI_URL;
  const opts = { method, headers: {} };
  if (body != null) {
    opts.headers["Content-Type"] = "application/json";
    opts.body = JSON.stringify(body);
  }
  const r = await fetch(base + path, opts);
  const text = await r.text();
  let data; try { data = JSON.parse(text); } catch { data = { raw: text }; }
  if (!r.ok) throw new Error(`NagiBridge ${r.status}: ${text.slice(0, 200)}`);
  return data;
}

let ws = null, alive = false, chatTimer = null, lastChatKey = "";

function connect() {
  console.log(`[link] 拨号 ${WS_URL} …`);
  ws = new WebSocket(WS_URL);

  ws.onopen = () => {
    ws.send(JSON.stringify({ type: "hello", token: TOKEN, agent: "stardew_link_nagi", v: "2.0" }));
  };

  ws.onmessage = async (ev) => {
    let msg; try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.type === "hello_ack") {
      alive = true;
      console.log("[link] 已连上家里的服务器 ✓");
      startChatWatch();
      return;
    }
    if (msg.type === "req") {
      const res = { type: "res", id: msg.id };
      try {
        if (msg.op === "http") {
          res.data = await nagi(msg.method || "GET", msg.path || "/state", msg.body, !!msg.farmhand);
        } else if (msg.op === "state") {
          res.data = await nagi("GET", "/state", null, !!msg.farmhand);
        } else if (msg.op === "action") {
          const a = msg.action || {};
          if (a.actionType === "chat") {
            res.data = await nagi("POST", "/chat/push", { sender: "苏林", message: a.metadata?.message || "" });
            res.ok = true;
          } else res.error = "旧版action已退役，请用 op=http";
        } else res.error = "unknown op: " + msg.op;
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

// 玩家在游戏里说话 → 推给服务器。轮询 /chat/history，用"最后一条的指纹"去重。
function startChatWatch() {
  stopChatWatch();
  chatTimer = setInterval(async () => {
    if (!alive) return;
    let hist;
    try { hist = await nagi("GET", "/chat/history"); } catch { return; } // 游戏没开时安静等
    const list = Array.isArray(hist) ? hist : (hist.messages || hist.history || []);
    if (!list.length) return;
    const idx = lastChatKey ? list.findIndex(m => JSON.stringify(m) === lastChatKey) : list.length - 1;
    const fresh = idx >= 0 ? list.slice(idx + 1) : list;
    lastChatKey = JSON.stringify(list[list.length - 1]);
    if (!fresh.length) return;
    try { ws.send(JSON.stringify({ type: "chat", messages: fresh })); } catch {}
  }, 1000);
}
function stopChatWatch() { if (chatTimer) { clearInterval(chatTimer); chatTimer = null; } }

console.log(`[link] NagiBridge: ${NAGI_URL}`);
connect();
