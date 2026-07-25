#nullable disable

using System;
using System.Collections.Generic;
using System.Text;

using Terraria;
using TShockAPI;
using TerrariaApi.Server;
using System.IO;
using System.IO.Streams;

using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using System.Runtime.Serialization.Formatters.Binary;
using Microsoft.Data.Sqlite;

namespace PerPlayerLoot
{
    public class JItem
    {
        public int id { get; set; }
        public int stack { get; set; }
        public byte prefix { get; set; }
    }

    public class FakeChestDatabase
    {
        // Map { UUID: { ChestID: Chest } }
        public static Dictionary<string, Dictionary<int, Chest>> fakeChestsMap = new Dictionary<string, Dictionary<int, Chest>> { };

        // Stores (x, y) of player-placed chests where y = top tile = Main.chest[].y
        public static HashSet<(int, int)> playerPlacedChests = new HashSet<(int, int)>();

        private static string connString = "Data Source=tshock/perplayerloot.sqlite";

        public FakeChestDatabase() { }

        public void Initialize()
        {
            CreateTables();
            LoadFakeChests();
        }

        public void CreateTables()
        {
            TSPlayer.Server.SendInfoMessage("Setting up per-player chests database...");
            using (SqliteConnection conn = new SqliteConnection(connString))
            {
                conn.Open();

                string sql = @"
                    CREATE TABLE IF NOT EXISTS chests (
                        id INTEGER NOT NULL,
                        playerUuid TEXT NOT NULL,
                        x INTEGER NOT NULL,
                        y INTEGER NOT NULL,
                        items BLOB NOT NULL,
                        PRIMARY KEY (id, playerUuid)
                    );

                    CREATE TABLE IF NOT EXISTS placed (
                        x INTEGER NOT NULL,
                        y INTEGER NOT NULL,
                        PRIMARY KEY (x, y)
                    );
                ";

                using (var cmd = new SqliteCommand(sql, conn))
                    cmd.ExecuteNonQuery();
            }
        }

        public void LoadFakeChests()
        {
            TSPlayer.Server.SendInfoMessage("Loading per-player loot chest inventories...");
            int count = 0;

            using (SqliteConnection conn = new SqliteConnection(connString))
            {
                conn.Open();

                using (var cmd = new SqliteCommand("SELECT id, playerUuid, x, y, items FROM chests;", conn))
                {
                    SqliteDataReader reader = cmd.ExecuteReader();

                    while (reader.Read())
                    {
                        string playerUuid = Convert.ToString(reader["playerUuid"]);
                        int chestId = Convert.ToInt32(reader["id"]);

                        List<Item> items = new List<Item>();

                        MemoryStream itemsRaw = new MemoryStream((byte[]) reader["items"]);
                        using (var br = new BsonReader(itemsRaw))
                        {
                            br.ReadRootValueAsArray = true;

                            var jItems = (new JsonSerializer()).Deserialize<IList<JItem>>(br);

                            foreach (var jItem in jItems)
                            {
                                if (jItem == null)
                                {
                                    items.Add(new Item());
                                    continue; // <-- important: skip item creation below
                                }

                                var item = new Item();
                                item.netDefaults(jItem.id);
                                item.stack = jItem.stack;
                                item.prefix = jItem.prefix;

                                items.Add(item);
                            }
                        }

                        Chest chest = new Chest(0, Convert.ToInt32(reader["x"]), Convert.ToInt32(reader["y"]));

                        // Initialize all slots before copying
                        for (int i = 0; i < chest.item.Length; i++)
                            chest.item[i] = new Item();

                        for (int i = 0; i < items.Count && i < chest.item.Length; i++)
                            chest.item[i] = items[i];

                        var playerChests = fakeChestsMap.GetValueOrDefault(playerUuid, new Dictionary<int, Chest>());
                        fakeChestsMap[playerUuid] = playerChests;
                        fakeChestsMap[playerUuid][chestId] = chest;

                        count++;
                    }
                }

                using (var cmd = new SqliteCommand("SELECT x, y FROM placed;", conn))
                {
                    SqliteDataReader reader = cmd.ExecuteReader();

                    playerPlacedChests.Clear();

                    while (reader.Read())
                    {
                        int x = Convert.ToInt32(reader["x"]);
                        int y = Convert.ToInt32(reader["y"]);
                        playerPlacedChests.Add((x, y));
                    }
                }
            }
        }

        public void SaveFakeChests(string PlayerUuid = null, int? ChestId = null)
        {
            int count = 0;

            using (SqliteConnection conn = new SqliteConnection(connString))
            {
                conn.Open();

                foreach (KeyValuePair<string, Dictionary<int, Chest>> playerEntry in fakeChestsMap)
                {
                    string playerUuid = playerEntry.Key;
                    if (PlayerUuid != null && playerUuid != PlayerUuid)
                        continue;

                    var playerChests = playerEntry.Value;

                    foreach (KeyValuePair<int, Chest> chestEntry in playerChests)
                    {
                        int chestId = chestEntry.Key;
                        if (ChestId != null && chestId != ChestId)
                            continue;

                        var chest = chestEntry.Value;

                        List<JItem> jItems = new List<JItem>(chest.item.Length);

                        foreach (var item in chest.item)
                        {
                            if (item == null)
                            {
                                jItems.Add(null);
                                continue;
                            }

                            jItems.Add(new JItem
                            {
                                id = item.type,
                                stack = item.stack,
                                prefix = item.prefix
                            });
                        }

                        MemoryStream itemsMs = new MemoryStream();
                        using (var writer = new BsonWriter(itemsMs))
                        {
                            JsonSerializer serializer = new JsonSerializer();
                            serializer.Serialize(writer, jItems);
                        }

                        using (var cmd = new SqliteCommand(
                            "REPLACE INTO chests (id, playerUuid, x, y, items) VALUES (@id, @playerUuid, @x, @y, @items);", conn))
                        {
                            cmd.Parameters.AddWithValue("@id", chestId);
                            cmd.Parameters.AddWithValue("@playerUuid", playerUuid);
                            cmd.Parameters.AddWithValue("@x", chest.x);
                            cmd.Parameters.AddWithValue("@y", chest.y);
                            cmd.Parameters.AddWithValue("@items", itemsMs.ToArray());
                            cmd.ExecuteNonQuery();
                        }

                        count++;
                    }
                }

                foreach ((int x, int y) in playerPlacedChests)
                {
                    using (var cmd = new SqliteCommand(
                        "REPLACE INTO placed (x, y) VALUES (@x, @y);", conn))
                    {
                        cmd.Parameters.AddWithValue("@x", x);
                        cmd.Parameters.AddWithValue("@y", y);
                        cmd.ExecuteNonQuery();
                    }
                }
            }

            TSPlayer.Server.SendSuccessMessage($"Saved {count} loot chest inventories, {playerPlacedChests.Count} player-placed chests.");
        }

        public Chest GetOrCreateFakeChest(int chestId, string playerUuid)
        {
            var playerChests = fakeChestsMap.GetValueOrDefault(playerUuid, new Dictionary<int, Chest>());
            fakeChestsMap[playerUuid] = playerChests;

            if (!playerChests.ContainsKey(chestId))
            {
                var realChest = Main.chest[chestId];

                var fakeChest = new Chest(0, realChest.x, realChest.y);

                for (int i = 0; i < fakeChest.item.Length; i++)
                    fakeChest.item[i] = new Item();

                for (int i = 0; i < realChest.item.Length; i++)
                {
                    if (realChest.item[i] != null)
                        fakeChest.item[i] = realChest.item[i].Clone();
                }

                fakeChestsMap[playerUuid][chestId] = fakeChest;
                SaveFakeChests(playerUuid, chestId);

                return fakeChest;
            }

            return playerChests[chestId];
        }

        public void SetChestPlayerPlaced(int tileX, int tileY)
        {
            playerPlacedChests.Add((tileX, tileY));
        }

        public bool IsChestPlayerPlaced(int tileX, int tileY)
        {
            return playerPlacedChests.Contains((tileX, tileY));
        }
    }
}
