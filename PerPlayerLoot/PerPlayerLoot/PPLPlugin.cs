using System;
using System.Collections.Generic;
using System.Text;

using Terraria;
using TShockAPI;
using TerrariaApi.Server;
using System.IO;
using System.IO.Streams;

namespace PerPlayerLoot
{
    [ApiVersion(2, 1)]
    public class PPLPlugin : TerrariaPlugin
    {
        #region info
        public override string Name => "PerPlayerLoot_TS_V6.1.0.0";

        public override Version Version => new Version(2, 1);

        public override string Author => "Made by Codian. Update by Hebbo98";

        public override string Description => "Duplicate loot chest inventories for each player.";
        #endregion

        public static FakeChestDatabase fakeChestDb = new FakeChestDatabase();

        public static bool enablePpl = true;

        public PPLPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            ServerApi.Hooks.GamePostInitialize.Register(this, OnWorldLoaded);
            ServerApi.Hooks.WorldSave.Register(this, OnWorldSave);
            ServerApi.Hooks.NetGetData.Register(this, OnGetData);

            TShockAPI.GetDataHandlers.PlaceChest += OnChestPlace;
            TShockAPI.GetDataHandlers.ChestOpen += OnChestOpen;
            TShockAPI.GetDataHandlers.ChestItemChange += OnChestItemChange;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GamePostInitialize.Deregister(this, OnWorldLoaded);
                ServerApi.Hooks.WorldSave.Deregister(this, OnWorldSave);
                ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);

                TShockAPI.GetDataHandlers.PlaceChest -= OnChestPlace;
                TShockAPI.GetDataHandlers.ChestOpen -= OnChestOpen;
                TShockAPI.GetDataHandlers.ChestItemChange -= OnChestItemChange;
            }

            base.Dispose(disposing);
        }

        private void OnWorldSave(WorldSaveEventArgs args)
        {
            fakeChestDb.SaveFakeChests();
        }

        private void OnWorldLoaded(EventArgs args)
        {
            fakeChestDb.Initialize();
            Commands.ChatCommands.Add(new Command("perplayerloot.toggle", ToggleCommand, "ppltoggle"));
        }

        private void ToggleCommand(CommandArgs args)
        {
            enablePpl = !enablePpl;
            if (enablePpl)
            {
                args.Player.SendSuccessMessage("Per player loot is now enabled!");
            }
            else
            {
                args.Player.SendSuccessMessage("Per player loot is now disabled! You can modify chests now and they will count as loot chests.");
            }
        }

        private void OnGetData(GetDataEventArgs e)
        {
            if (!enablePpl) return;

            // Packet 34 = PlaceChest
            // We intercept this RAW packet before TShock's PlaceChest handler fires.
            // This guarantees playerPlacedChests is populated before any ChestOpen
            // packet from the same client can be processed.
            if ((int)e.MsgID == 34)
            {
                try
                {
                    using (var stream = new MemoryStream(e.Msg.readBuffer, e.Index, e.Length))
                    using (var reader = new BinaryReader(stream))
                    {
                        byte action = reader.ReadByte(); // 0 = place, 1 = destroy
                        int tileX = reader.ReadInt16();
                        int tileY = reader.ReadInt16();

                        // e.TileY from PlaceChest event = bottom tile of chest.
                        // Main.chest[].y = top tile = tileY - 1.
                        // We store tileY - 1 to match the lookup key used in
                        // OnChestOpen / OnChestItemChange which use realChest.y.
                        if (action == 0)
                            fakeChestDb.SetChestPlayerPlaced(tileX, tileY - 1);
                    }
                }
                catch { /* malformed packet — ignore */ }
                return;
            }

            // Packet 85 = Quick Stack to Nearby Chests
            if ((int)e.MsgID != 85) return;

            TSPlayer player = TShock.Players[e.Msg.whoAmI];
            if (player == null) return;

            if (IsWorldGenChestNearby(player))
            {
                player.SendErrorMessage("Cannot quick-stack: a world-generated loot chest is nearby!");

                // Resync complete player inventory to prevent local client-side duplication
                for (short slot = 0; slot < 59; slot++)
                    player.SendData(PacketTypes.PlayerSlot, "", player.Index, slot);

                // Stop the server from processing Packet 85 completely!
                e.Handled = true;
            }
        }

        // Returns true if the tile type is a bank chest (piggy bank, safe, etc.)
        private bool IsBankChestTile(int tileType)
        {
            // 97  = Piggy Bank
            // 198 = Safe
            // 216 = Defender's Forge
            // 287 = Void Vault
            // 467 = Chester (cat)
            return tileType == 97 ||
                   tileType == 198 ||
                   tileType == 216 ||
                   tileType == 287 ||
                   tileType == 467;
        }

        // Returns true if there is any world-generated chest within range of the player.
        private bool IsWorldGenChestNearby(TSPlayer player, int range = 30)
        {
            int playerTileX = (int)(player.X / 16);
            int playerTileY = (int)(player.Y / 16);

            for (int i = 0; i < Main.chest.Length; i++)
            {
                Chest chest = Main.chest[i];
                if (chest == null) continue;

                Terraria.Tile tile = (Terraria.Tile)Main.tile[chest.x, chest.y];
                if (IsBankChestTile(tile.type)) continue;

                // chest.y = top tile, matches what we stored in playerPlacedChests
                if (fakeChestDb.IsChestPlayerPlaced(chest.x, chest.y)) continue;

                int dx = Math.Abs(chest.x - playerTileX);
                int dy = Math.Abs(chest.y - playerTileY);
                if (dx <= range && dy <= range)
                    return true;
            }

            return false;
        }

        private void OnChestItemChange(object sender, GetDataHandlers.ChestItemEventArgs e)
        {
            if (!enablePpl) return;

            Chest realChest = Main.chest[e.ID];
            if (realChest == null)
                return;

            // skip bank chests
            if (realChest.bankChest)
                return;

            // skip player-placed chests (realChest.y = top tile = what we stored)
            if (fakeChestDb.IsChestPlayerPlaced(realChest.x, realChest.y))
                return;

            // construct an item from the event data
            Item item = new Item();
            item.netDefaults(e.Type);
            item.stack = e.Stacks;
            item.prefix = e.Prefix;

            // get the per-player chest
            Chest fakeChest = fakeChestDb.GetOrCreateFakeChest(e.ID, e.Player.UUID);

            // update the slot with the item
            fakeChest.item[e.Slot] = item;

            e.Handled = true;
        }

        private byte[] ConstructSpoofedChestItemPacket(int chestId, int slot, Item item)
        {
            MemoryStream memoryStream = new MemoryStream();
            OTAPI.PacketWriter packetWriter = new OTAPI.PacketWriter(memoryStream);

            packetWriter.BaseStream.Position = 0L;
            long position = packetWriter.BaseStream.Position;

            packetWriter.BaseStream.Position += 2L;
            packetWriter.Write((byte)PacketTypes.ChestItem);

            packetWriter.Write((short)chestId);
            packetWriter.Write((byte)slot);

            short netId = 0;
            if (item != null && item.type > 0)
                netId = (short)item.type;

            packetWriter.Write((short)item.stack);
            packetWriter.Write(item.prefix);
            packetWriter.Write(netId);

            int positionAfter = (int)packetWriter.BaseStream.Position;

            packetWriter.BaseStream.Position = position;
            packetWriter.Write((ushort)positionAfter);
            packetWriter.BaseStream.Position = positionAfter;

            return memoryStream.ToArray();
        }

        private void OnChestOpen(object sender, GetDataHandlers.ChestOpenEventArgs e)
        {
            if (e.Handled) return;
            if (!enablePpl) return;

            int chestId = Chest.FindChest(e.X, e.Y);
            if (chestId == -1) return;

            Chest realChest = Main.chest[chestId];
            if (realChest == null)
                return;

            // skip bank chests
            if (realChest.bankChest)
                return;

            // skip player-placed chests (realChest.y = top tile = what we stored)
            if (fakeChestDb.IsChestPlayerPlaced(realChest.x, realChest.y))
                return;

            // make a per-player chest
            Chest fakeChest = fakeChestDb.GetOrCreateFakeChest(chestId, e.Player.UUID);

            e.Player.SendInfoMessage("Loot in this chest is saved per-player!");

            // clear all slots clientside first to prevent item duplication HEy there :D  its already cheating to have loot for all player :D so keep it like this !
            for (int slot = 0; slot < 40; slot++)
            {
                byte[] clearPayload = ConstructSpoofedChestItemPacket(chestId, slot, new Item());
                e.Player.SendRawData(clearPayload);
            }

            // send the correct per-player chest contents
            for (int slot = 0; slot < 40; slot++)
            {
                Item item = fakeChest.item[slot] ?? new Item();
                byte[] payload = ConstructSpoofedChestItemPacket(chestId, slot, item);
                e.Player.SendRawData(payload);
            }

            // trigger chest open
            e.Player.SendData(PacketTypes.ChestOpen, "", chestId);

            // set the active chest serverside
            e.Player.ActiveChest = chestId;
            Main.player[e.Player.Index].chest = chestId;
            e.Player.SendData(PacketTypes.SyncPlayerChestIndex, null, e.Player.Index, chestId);

            e.Handled = true;
        }

        private void OnChestPlace(object sender, GetDataHandlers.PlaceChestEventArgs e)
        {
            if (!enablePpl) return;

            // OnGetData (packet 34) already registered this chest as player-placed
            // before this handler ran. We just do the item-wipe here because
            // Main.chest[] is now populated by the time this handler fires.
            // e.TileY = bottom tile, so e.TileY - 1 = top tile = Main.chest[].y
            int chestId = Chest.FindChest(e.TileX, e.TileY - 1);
            if (chestId != -1 && Main.chest[chestId] != null)
            {
                for (int i = 0; i < Main.chest[chestId].item.Length; i++)
                    Main.chest[chestId].item[i] = new Item();
            }
        }
    }
}