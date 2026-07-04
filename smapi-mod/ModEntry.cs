using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Characters;

namespace StardewMCPBridge
{
    public class ModEntry : Mod
    {
        private string bridgePath;
        private string actionDir;
        private BotManager botManager;

        // --- chat capture (soren-play): mirror in-game chat into bridge_data.json ---
        // The vanilla ChatBox trims its list, so we diff snapshots instead of
        // trusting indices. Messages we sent ourselves (gold, via the "chat"
        // action) are filtered out through recentSent so the AI only sees the
        // player's side plus game notices.
        private List<string> chatPrev;                       // last poll's raw texts (null until first poll seeds it)
        private readonly List<object> chatOut = new();        // rolling window included in bridge JSON
        private readonly Queue<string> recentSent = new();    // our own outbound texts, to skip on capture
        private long chatSeq = 0;
        private Texture2D companion1Portrait;
        private Texture2D companion2Portrait;
        private Texture2D companion1Sprite;
        private Texture2D companion2Sprite;

        public override void Entry(IModHelper helper)
        {
            this.botManager = new BotManager(this.Monitor, helper);
            this.bridgePath = Path.Combine(helper.DirectoryPath, "bridge_data.json");
            this.actionDir = Path.Combine(helper.DirectoryPath, "actions");

            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.DayEnding += this.OnDayEnding;
            helper.Events.GameLoop.TimeChanged += this.OnTimeChanged;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
            helper.Events.Content.AssetRequested += this.OnAssetRequested;

            this.Monitor.Log("Stardew MCP Bridge initialized. Content pipeline registered.", LogLevel.Debug);
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            this.companion1Portrait = this.Helper.ModContent.Load<Texture2D>("assets/Companion1_portrait.png");
            this.companion2Portrait = this.Helper.ModContent.Load<Texture2D>("assets/Companion2_portrait.png");
            this.companion1Sprite = this.Helper.ModContent.Load<Texture2D>("assets/Companion1_sprite.png");
            this.companion2Sprite = this.Helper.ModContent.Load<Texture2D>("assets/Companion2_sprite.png");
            this.Monitor.Log("Bridge online. Portraits and sprites loaded. Waiting for world.", LogLevel.Info);
        }

        private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
        {
            // Inject portrait textures
            if (e.NameWithoutLocale.IsEquivalentTo("Portraits/Companion1"))
            {
                e.LoadFrom(() => this.companion1Portrait, AssetLoadPriority.Exclusive);
            }
            else if (e.NameWithoutLocale.IsEquivalentTo("Portraits/Companion2"))
            {
                e.LoadFrom(() => this.companion2Portrait, AssetLoadPriority.Exclusive);
            }
            // Custom sprite sheets for walking animation
            else if (e.NameWithoutLocale.IsEquivalentTo("Characters/Companion1"))
            {
                e.LoadFrom(() => this.companion1Sprite, AssetLoadPriority.Exclusive);
            }
            else if (e.NameWithoutLocale.IsEquivalentTo("Characters/Companion2"))
            {
                e.LoadFrom(() => this.companion2Sprite, AssetLoadPriority.Exclusive);
            }
            // Inject NPC data so the game considers us valid
            else if (e.NameWithoutLocale.IsEquivalentTo("Data/Characters"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, CharacterData>();

                    if (!data.Data.ContainsKey("Companion1"))
                    {
                        data.Data["Companion1"] = new CharacterData
                        {
                            DisplayName = "Companion1",
                            HomeRegion = "Town",
                        };
                    }

                    if (!data.Data.ContainsKey("Companion2"))
                    {
                        data.Data["Companion2"] = new CharacterData
                        {
                            DisplayName = "Companion2",
                            HomeRegion = "Town",
                        };
                    }
                });
            }
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            // AI ticks every frame (60/sec) for responsive combat, pathfinding, stuck detection
            this.botManager.Update();

            // Bridge I/O every 30 ticks (~0.5s) to avoid thrashing disk
            if (e.IsMultipleOf(30))
            {
                this.SyncGameState();
                this.ProcessActions();
            }
        }

        private void OnTimeChanged(object sender, TimeChangedEventArgs e)
        {
            // Safety net: if it's 2:00 AM (forced pass-out time), signal bots ready
            if (e.NewTime >= 2600)
                this.botManager.SignalAllSleepReady();
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            // Signal all bot farmers as sleep-ready so the game doesn't deadlock
            this.botManager.SignalAllSleepReady();
            this.Monitor.Log("Day ending: bot farmers signaled sleep ready", LogLevel.Debug);
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            this.botManager.OnDayStarted();
            this.Monitor.Log("New day: companion stamina restored", LogLevel.Info);
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            this.botManager.Cleanup();
            this.Monitor.Log("Returned to title: companions cleaned up", LogLevel.Info);
        }

        private void SyncGameState()
        {
            try
            {
                var state = new
                {
                    time = Game1.timeOfDay,
                    day = Game1.dayOfMonth,
                    season = Game1.currentSeason,
                    weather = Game1.isLightning ? "storm" : Game1.isRaining ? "rain" : Game1.isSnowing ? "snow" : Game1.isDebrisWeather ? "windy" : "sunny",
                    location = Game1.currentLocation?.Name,
                    player = new
                    {
                        name = Game1.player.Name,
                        health = Game1.player.health,
                        stamina = Game1.player.Stamina,
                        money = Game1.player.Money,
                        position = new { x = Game1.player.Position.X, y = Game1.player.Position.Y }
                    },
                    companions = this.botManager.GetBotStatus(),
                    chat = this.CollectChat(),
                    npcs = Game1.currentLocation?.characters.Select(c => new {
                        name = c.Name,
                        position = new { x = c.Position.X, y = c.Position.Y }
                    }).ToList(),
                    syncedAt = DateTime.UtcNow.ToString("o")
                };

                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                // Atomic write: temp file then rename, so MCP never reads partial JSON
                string tmpPath = this.bridgePath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, this.bridgePath, true);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Bridge Sync Error: {ex.Message}", LogLevel.Error);
            }
        }

        private void ProcessActions()
        {
            try
            {
                if (!Directory.Exists(this.actionDir))
                    return;

                // Drain the queue oldest-first. Each command is its own file named
                // <timestamp>-<seq>.json, so ordinal filename sort is chronological.
                string[] files = Directory.GetFiles(this.actionDir, "*.json");
                if (files.Length == 0)
                    return;
                Array.Sort(files, StringComparer.Ordinal);

                foreach (string file in files)
                {
                    string json;
                    try
                    {
                        json = File.ReadAllText(file);
                        // Delete immediately so each command is consumed exactly once,
                        // even if handling below throws.
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log($"Action read error ({Path.GetFileName(file)}): {ex.Message}", LogLevel.Error);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(json))
                        continue;

                    try
                    {
                        this.HandleAction(json);
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log($"Action handling error: {ex.Message}", LogLevel.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Action Processing Error: {ex.Message}", LogLevel.Error);
            }
        }

        private static string ChatMessageText(StardewValley.Menus.ChatMessage m)
        {
            if (m?.message == null) return "";
            // emoji snippets carry no text; render a placeholder so "❤ alone" isn't dropped as empty
            return string.Concat(m.message.Select(s => s.message ?? "[emoji]"));
        }

        private object CollectChat()
        {
            try
            {
                var box = Game1.chatBox;
                if (box?.messages == null) return this.chatOut;
                var cur = box.messages.Select(ChatMessageText).ToList();

                if (this.chatPrev == null)
                {
                    // first poll after load: seed the snapshot, don't replay history
                    this.chatPrev = cur;
                    return this.chatOut;
                }

                // The list appends new messages and trims old ones from the front,
                // so the new tail of `cur` is whatever doesn't overlap `chatPrev`:
                // find the largest m where cur[0..m) == the last m entries of chatPrev.
                int overlap = 0;
                for (int m = Math.Min(cur.Count, this.chatPrev.Count); m > 0; m--)
                {
                    bool match = true;
                    for (int i = 0; i < m; i++)
                    {
                        if (cur[i] != this.chatPrev[this.chatPrev.Count - m + i]) { match = false; break; }
                    }
                    if (match) { overlap = m; break; }
                }

                for (int i = overlap; i < cur.Count; i++)
                {
                    string text = cur[i];
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (this.recentSent.Contains(text)) continue;   // our own gold message echoing back
                    this.chatOut.Add(new { seq = ++this.chatSeq, text, at = DateTime.UtcNow.ToString("o") });
                }
                while (this.chatOut.Count > 30) this.chatOut.RemoveAt(0);
                this.chatPrev = cur;
            }
            catch (Exception ex)
            {
                // chat capture must never break the bridge sync
                this.Monitor.Log($"Chat capture error: {ex.Message}", LogLevel.Trace);
            }
            return this.chatOut;
        }

        private void HandleAction(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("actionType", out var actionType))
                return;

            // Route to bot manager for companion actions
            this.botManager.ProcessAction(json);

            // Handle chat separately (not a companion action)
            if (actionType.GetString() == "chat")
            {
                if (root.TryGetProperty("metadata", out var meta) &&
                    meta.TryGetProperty("message", out var msg))
                {
                    string text = msg.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        // remember our own text so CollectChat doesn't echo it back to the AI
                        this.recentSent.Enqueue(text);
                        while (this.recentSent.Count > 10) this.recentSent.Dequeue();
                        Game1.chatBox?.addMessage(text, Microsoft.Xna.Framework.Color.Gold);
                        this.Monitor.Log($"Chat sent: {text}", LogLevel.Info);
                    }
                }
            }
        }
    }
}
