using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Rust;
using System;
using System.Collections.Generic;
using UnityEngine;
using static BaseEntity;

namespace Oxide.Plugins
{
    [Info("Item Sourcer", "WhiteThunder", "0.2.1")]
    [Description("Allows players to build, craft, reload and more using items from external containers.")]
    internal class ItemSourcer : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin Backpacks;

        private const int InventorySize = 24;

        private const string PermissionAdmin = "itemsourcer.admin";

        private object True = true;
        private object False = false;

        private ContainerManager _containerManager = new ContainerManager();

        private bool _callingCanCraft = false;

        #endregion

        #region Hooks

        private void Unload()
        {
            _containerManager.RemoveContainers();
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            _containerManager.RemoveContainers(plugin);
        }

        private void OnEntitySaved(BasePlayer player, BaseNetworkable.SaveInfo saveInfo)
        {
            AddItemsForNetwork(saveInfo.msg.basePlayer.inventory.invMain, player);
        }

        private void OnInventoryNetworkUpdate(PlayerInventory inventory, ItemContainer container, ProtoBuf.UpdateItemContainer updatedItemContainer, PlayerInventory.Type inventoryType)
        {
            if (inventoryType != PlayerInventory.Type.Main)
                return;

            AddItemsForNetwork(updatedItemContainer.container[0], inventory.baseEntity);
        }

        private object OnInventoryItemsCount(PlayerInventory inventory, int itemId)
        {
            return SumPlayerItems(inventory.baseEntity, itemId);
        }

        private object OnInventoryItemsTake(PlayerInventory inventory, List<Item> collect, int itemId, int amount)
        {
            return TakePlayerItems(inventory.baseEntity, collect, itemId, amount);
        }

        private object OnInventoryItemsFind(PlayerInventory inventory, int itemId)
        {
            List<Item> list = new List<Item>();
            FindPlayerItems(inventory.baseEntity, list, itemId);
            return list;
        }

        private object OnInventoryAmmoFind(PlayerInventory inventory, List<Item> collect, AmmoTypes ammoType)
        {
            FindPlayerAmmo(inventory.baseEntity, collect, ammoType);
            return False;
        }

        private object OnIngredientsCollect(ItemCrafter itemCrafter, ItemBlueprint blueprint, ItemCraftTask task, int amount, BasePlayer player)
        {
            var collect = new List<Item>();
            foreach (ItemAmount ingredient in blueprint.ingredients)
            {
                TakePlayerItems(player, collect, ingredient.itemid, (int)ingredient.amount * amount);
            }

            task.potentialOwners = new List<ulong>();

            foreach (Item item in collect)
            {
                item.CollectedForCrafting(player);
                if (!task.potentialOwners.Contains(player.userID))
                {
                    task.potentialOwners.Add(player.userID);
                }
            }

            task.takenItems = collect;
            return False;
        }

        private object CanCraft(ItemCrafter itemCrafter, ItemBlueprint blueprint, int amount, bool free)
        {
            if (_callingCanCraft)
                return null;

            _callingCanCraft = true;

            var canCraftResult = Interface.Oxide.CallHook("CanCraft", itemCrafter, blueprint, amount, free);
            if (canCraftResult != null)
                return null;

            _callingCanCraft = false;

            var basePlayer = itemCrafter.baseEntity;

            foreach (ItemAmount ingredient in blueprint.ingredients)
            {
                if (SumPlayerItems(basePlayer, ingredient.itemid) < ingredient.amount * amount)
                    return null;
            }

            return True;
        }

        #endregion

        #region Commands

        [Command("source.add"), Permission(PermissionAdmin)]
        private void CommandAddContainer(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyIsPlayer(player, out basePlayer))
                return;

            RaycastHit hit;
            if (!Physics.Raycast(basePlayer.eyes.HeadRay(), out hit, 9, Rust.Layers.Solid, QueryTriggerInteraction.Ignore))
            {
                player.Reply($"No raycast hit.");
                return;
            }

            var containerEntity = hit.GetEntity() as IItemContainerEntity;
            if (containerEntity == null)
            {
                player.Reply($"No container entity found.");
                return;
            }

            _containerManager.AddContainer(this, basePlayer, containerEntity, containerEntity.inventory);
            SendInventoryUpdate(basePlayer);
            player.Reply($"Successfully added container.");
        }

        [Command("source.remove"), Permission(PermissionAdmin)]
        private void CommandRemoveContainer(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyIsPlayer(player, out basePlayer))
                return;

            RaycastHit hit;
            if (!Physics.Raycast(basePlayer.eyes.HeadRay(), out hit, 9, Rust.Layers.Solid, QueryTriggerInteraction.Ignore))
            {
                player.Reply($"No raycast hit.");
                return;
            }

            var containerEntity = hit.GetEntity() as IItemContainerEntity;
            if (containerEntity == null)
            {
                player.Reply($"No container entity found.");
                return;
            }

            _containerManager.RemoveContainer(this, basePlayer, containerEntity.inventory);
            SendInventoryUpdate(basePlayer);
            player.Reply($"Successfully removed container.");
        }

        [Command("source.backpack.add"), Permission(PermissionAdmin)]
        private void CommandBackpackAdd(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyIsPlayer(player, out basePlayer))
                return;

            var container = Backpacks?.Call("API_GetBackpackContainer", basePlayer.userID) as ItemContainer;
            if (container == null)
            {
                player.Reply($"No Backpack container found.");
                return;
            }

            _containerManager.AddContainer(this, basePlayer, container.entityOwner as IItemContainerEntity, container);
            SendInventoryUpdate(basePlayer);
            player.Reply($"Successfully added Backpack container.");
        }

        [Command("source.backpack.remove"), Permission(PermissionAdmin)]
        private void CommandBackpackRemove(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyIsPlayer(player, out basePlayer))
                return;

            var container = Backpacks?.Call("API_GetBackpackContainer", basePlayer.userID) as ItemContainer;
            if (container == null)
            {
                player.Reply($"No Backpack container found.");
                return;
            }

            _containerManager.RemoveContainer(this, basePlayer, container);
            SendInventoryUpdate(basePlayer);
            player.Reply($"Successfully removed Backpack container.");
        }

        #endregion

        #region API

        [HookMethod(nameof(API_AddContainer))]
        public void API_AddContainer(Plugin plugin, BasePlayer player, IItemContainerEntity containerEntity, ItemContainer container, Func<Plugin, BasePlayer, ItemContainer, bool> canUseContainer = null)
        {
            if (_containerManager.AddContainer(plugin, player, containerEntity, container, canUseContainer))
            {
                SendInventoryUpdate(player);
            }
        }

        [HookMethod(nameof(API_RemoveContainer))]
        public void API_RemoveContainer(Plugin plugin, BasePlayer player, ItemContainer container)
        {
            if (_containerManager.RemoveContainer(plugin, player, container))
            {
                SendInventoryUpdate(player);
            }
        }

        [HookMethod(nameof(API_RemoveContainers))]
        public void API_RemoveContainers(Plugin plugin, BasePlayer player)
        {
            if (_containerManager.RemoveContainers(plugin, player))
            {
                SendInventoryUpdate(player);
            }
        }

        [HookMethod(nameof(API_RemoveContainers))]
        public void API_RemoveContainers(Plugin plugin)
        {
            var updatedPlayers = _containerManager.RemoveContainers(plugin);
            if ((updatedPlayers?.Count ?? 0) > 0)
            {
                foreach (var player in updatedPlayers)
                {
                    SendInventoryUpdate(player);
                }
            }
        }

        [HookMethod(nameof(API_FindItems))]
        public void API_FindItems(BasePlayer player, List<Item> collect, int itemId, ulong skinId = 0)
        {
            FindPlayerItems(player, collect, itemId, skinId);
        }

        [HookMethod(nameof(API_SumPlayerItems))]
        public int API_SumPlayerItems(BasePlayer player, int itemId, ulong skinId = 0)
        {
            return SumPlayerItems(player, itemId, skinId);
        }

        [HookMethod(nameof(API_TakePlayerItems))]
        public int API_TakePlayerItems(BasePlayer player, List<Item> collect, int itemId, int amount, ulong skinId = 0)
        {
            var amountTaken = TakePlayerItems(player, collect, itemId, amount, skinId);
            ShowGiveNotice(player, itemId, -amount);
            return amountTaken;
        }

        [HookMethod(nameof(API_FindPlayerAmmo))]
        public void API_FindPlayerAmmo(BasePlayer player, List<Item> collect, AmmoTypes ammoType)
        {
            FindPlayerAmmo(player, collect, ammoType);
        }

        #endregion

        #region Helper Methods

        private static void ShowGiveNotice(BasePlayer player, int itemId, int amount, string name = null, GiveItemReason reason = GiveItemReason.Generic)
        {
            player.Command("note.inv", itemId, amount, name, (int)reason);
        }

        private static void SendInventoryUpdate(BasePlayer player)
        {
            player.inventory.SendUpdatedInventory(PlayerInventory.Type.Main, player.inventory.containerMain);
        }

        private bool VerifyIsPlayer(IPlayer player, out BasePlayer basePlayer)
        {
            if (player.IsServer)
            {
                basePlayer = null;
                return false;
            }

            basePlayer = player.Object as BasePlayer;
            return true;
        }

        private int GetHighestUsedSlot(ProtoBuf.ItemContainer containerData)
        {
            var highestUsedSlot = -1;

            foreach (var item in containerData.contents)
            {
                if (item.slot > highestUsedSlot)
                {
                    highestUsedSlot = item.slot;
                }
            }

            return highestUsedSlot;
        }

        private int AddContainerItemForNetwork(ProtoBuf.ItemContainer containerData, ItemContainer container, ref int nextInvisibleSlot, bool addChildContainersOnly = false)
        {
            var itemsAdded = 0;

            foreach (var item in container.itemList)
            {
                if (!addChildContainersOnly)
                {
                    var itemData = item.Save();
                    itemData.slot = nextInvisibleSlot++;

                    containerData.contents.Add(itemData);
                    itemsAdded++;
                }

                ItemContainer childContainer;
                if (HasSearchableContainer(item, out childContainer))
                {
                    itemsAdded += AddContainerItemForNetwork(containerData, childContainer, ref nextInvisibleSlot);
                }
            }

            return itemsAdded;
        }

        private void AddItemsForNetwork(ProtoBuf.ItemContainer containerData, BasePlayer player)
        {
            if (containerData == null)
                return;

            var firstAvailableInvisibleSlot = Math.Max(InventorySize, GetHighestUsedSlot(containerData) + 1);
            var nextInvisibleSlot = firstAvailableInvisibleSlot;
            var itemsAdded = 0;

            // Add child containers.
            itemsAdded += AddContainerItemForNetwork(containerData, player.inventory.containerMain, ref nextInvisibleSlot, addChildContainersOnly: true);
            itemsAdded += AddContainerItemForNetwork(containerData, player.inventory.containerBelt, ref nextInvisibleSlot, addChildContainersOnly: true);
            itemsAdded += AddContainerItemForNetwork(containerData, player.inventory.containerWear, ref nextInvisibleSlot, addChildContainersOnly: true);

            var containerList = _containerManager.GetContainerList(player);
            if (containerList != null)
            {
                // Add external containers.
                foreach (var containerEntry in containerList)
                {
                    itemsAdded += AddContainerItemForNetwork(containerData, containerEntry.Container, ref nextInvisibleSlot);
                }
            }

            if (itemsAdded > 0)
            {
                if (containerData.slots >= 24)
                {
                    containerData.slots = firstAvailableInvisibleSlot + itemsAdded;
                }
            }
        }

        private bool ItemMatches(Item item, int itemId, ulong skinId)
        {
            if (item.info.itemid != itemId)
                return false;

            if (skinId != 0 && item.skin != skinId)
                return false;

            return true;
        }

        private void FindContainerItems(ItemContainer container, List<Item> collect, int itemId, ulong skinId = 0)
        {
            foreach (var item in container.itemList)
            {
                if (ItemMatches(item, itemId, skinId))
                {
                    collect.Add(item);
                }

                ItemContainer childContainer;
                if (HasSearchableContainer(item, out childContainer))
                {
                    FindContainerItems(childContainer, collect, itemId, skinId);
                }
            }
        }

        private void FindPlayerItems(BasePlayer player, List<Item> collect, int itemId, ulong skinId = 0)
        {
            FindContainerItems(player.inventory.containerMain, collect, itemId, skinId);
            FindContainerItems(player.inventory.containerBelt, collect, itemId, skinId);
            FindContainerItems(player.inventory.containerWear, collect, itemId, skinId);

            var containerList = _containerManager.GetContainerList(player);
            if (containerList != null)
            {
                foreach (var containerEntry in containerList)
                {
                    FindContainerItems(containerEntry.Container, collect, itemId, skinId);
                }
            }
        }

        private int SumContainerItems(ItemContainer container, int itemId, ulong skinId = 0)
        {
            var count = 0;

            foreach (var item in container.itemList)
            {
                if (ItemMatches(item, itemId, skinId))
                {
                    count += item.amount;
                }

                ItemContainer childContainer;
                if (HasSearchableContainer(item, out childContainer))
                {
                    count += SumContainerItems(childContainer, itemId, skinId);
                }
            }

            return count;
        }

        private int SumPlayerItems(BasePlayer player, int itemId, ulong skinId = 0)
        {
            var sum = SumContainerItems(player.inventory.containerMain, itemId, skinId)
                + SumContainerItems(player.inventory.containerBelt, itemId, skinId)
                + SumContainerItems(player.inventory.containerWear, itemId, skinId);

            var containerList = _containerManager.GetContainerList(player);
            if (containerList != null)
            {
                foreach (var containerEntry in containerList)
                {
                    sum += SumContainerItems(containerEntry.Container, itemId, skinId);
                }
            }

            return sum;
        }

        private int TakeContainerItems(ItemContainer container, List<Item> collect, int itemId, int totalAmountToTake, ulong skinId = 0)
        {
            var totalAmountTaken = 0;

            for (var i = container.itemList.Count - 1; i >= 0; i--)
            {
                var amountToTake = totalAmountToTake - totalAmountTaken;
                if (amountToTake <= 0)
                    break;

                var item = container.itemList[i];
                if (ItemMatches(item, itemId, skinId))
                {
                    amountToTake = Math.Min(item.amount, amountToTake);

                    if (item.amount > amountToTake)
                    {
                        if (collect != null)
                        {
                            var splitItem = item.SplitItem(amountToTake);
                            var playerOwner = splitItem.GetOwnerPlayer();
                            if (playerOwner != null)
                            {
                                splitItem.CollectedForCrafting(playerOwner);
                            }
                            collect.Add(splitItem);
                        }
                        else
                        {
                            item.amount -= amountToTake;
                            item.MarkDirty();
                        }
                    }
                    else
                    {
                        item.RemoveFromContainer();

                        if (collect != null)
                        {
                            collect.Add(item);
                        }
                        else
                        {
                            item.Remove();
                        }
                    }

                    totalAmountTaken += amountToTake;
                }

                ItemContainer childContainer;
                if (HasSearchableContainer(item, out childContainer))
                {
                    totalAmountTaken += TakeContainerItems(childContainer, collect, itemId, amountToTake, skinId);
                }

                if (totalAmountTaken >= totalAmountToTake)
                    return totalAmountTaken;
            }

            return totalAmountTaken;
        }

        private int TakePlayerItems(BasePlayer player, List<Item> collect, int itemId, int amountToTake, ulong skinId = 0)
        {
            var amountTaken = TakeContainerItems(player.inventory.containerMain, collect, itemId, amountToTake, skinId);
            if (amountTaken >= amountToTake)
                return amountTaken;

            amountTaken += TakeContainerItems(player.inventory.containerBelt, collect, itemId, amountToTake - amountTaken, skinId);
            if (amountTaken >= amountToTake)
                return amountTaken;

            amountTaken += TakeContainerItems(player.inventory.containerWear, collect, itemId, amountToTake - amountTaken, skinId);
            if (amountTaken >= amountToTake)
                return amountTaken;

            var containerList = _containerManager.GetContainerList(player);
            if (containerList != null)
            {
                foreach (var containerEntry in containerList)
                {
                    amountTaken += TakeContainerItems(containerEntry.Container, collect, itemId, amountToTake - amountTaken, skinId);
                    if (amountTaken >= amountToTake)
                        return amountTaken;
                }
            }
            return amountTaken;
        }

        private void FindPlayerAmmo(BasePlayer player, List<Item> collect, AmmoTypes ammoType)
        {
            if (player.inventory.containerMain != null)
            {
                player.inventory.containerMain.FindAmmo(collect, ammoType);
            }

            if (player.inventory.containerBelt != null)
            {
                player.inventory.containerBelt.FindAmmo(collect, ammoType);
            }

            var containerList = _containerManager.GetContainerList(player);
            if (containerList != null)
            {
                foreach (var containerEntry in containerList)
                {
                    containerEntry.Container.FindAmmo(collect, ammoType);
                }
            }
        }

        private bool HasItemMod<T>(ItemDefinition itemDefinition) where T : ItemMod
        {
            foreach (var itemMod in itemDefinition.itemMods)
            {
                if (itemMod is T)
                    return true;
            }

            return false;
        }

        private bool HasSearchableContainer(Item item, out ItemContainer container)
        {
            container = item.contents;
            if (container == null)
                return false;

            // Don't consider vanilla containers searchable (i.e., don't take low grade out of a miner's hat).
            return !HasItemMod<ItemModContainer>(item.info);
        }

        #endregion

        #region Container Manager

        private class EntityTracker : FacepunchBehaviour
        {
            public static EntityTracker AddToEntity(BaseEntity entity, ContainerManager containerManager)
            {
                var entityTracker = entity.gameObject.AddComponent<EntityTracker>();
                entityTracker._entity = entity;
                entityTracker._containerManager = containerManager;
                return entityTracker;
            }

            private BaseEntity _entity;
            private ContainerManager _containerManager;
            private List<ContainerEntry> _containerList = new List<ContainerEntry>();

            public void AddContainer(ContainerEntry containerEntry)
            {
                _containerList.Add(containerEntry);
            }

            public void RemoveContainer(ContainerEntry containerEntry)
            {
                if (!_containerList.Remove(containerEntry))
                    return;

                if (_containerList.Count == 0 && _entity != null && !_entity.IsDestroyed)
                {
                    DestroyImmediate(this);
                }
            }

            private void OnDestroy()
            {
                var entityDestroyed = _entity == null || _entity.IsDestroyed;

                for (var i = _containerList.Count - 1; i >= 0; i--)
                {
                    var containerEntry = _containerList[i];
                    _containerManager.RemoveContainer(containerEntry);

                    if (entityDestroyed)
                    {
                        SendInventoryUpdate(containerEntry.Player);
                    }
                }

                _containerManager.UnregisterEntity(_entity);
            }
        }

        private struct ContainerEntry
        {
            public Plugin Plugin;
            public BasePlayer Player;
            public EntityTracker EntityTracker;
            public ItemContainer Container;
            public Func<Plugin, BasePlayer, ItemContainer, bool> CanUseContainer;

            public void Deactivate()
            {
                if (EntityTracker != null)
                {
                    EntityTracker.RemoveContainer(this);
                }
            }

            public bool CanUse()
            {
                return CanUseContainer?.Invoke(Plugin, Player, Container) ?? true;
            }
        }

        private class ContainerManager
        {
            private Dictionary<ulong, List<ContainerEntry>> _playerContainerEntries = new Dictionary<ulong, List<ContainerEntry>>();
            private Dictionary<BaseEntity, EntityTracker> _entityTrackers = new Dictionary<BaseEntity, EntityTracker>();

            public void UnregisterEntity(BaseEntity entity)
            {
                _entityTrackers.Remove(entity);
            }

            public bool AddContainer(Plugin plugin, BasePlayer player, IItemContainerEntity containerEntity, ItemContainer container, Func<Plugin, BasePlayer, ItemContainer, bool> canUseContainer = null)
            {
                var containerList = GetContainerList(player);
                if (containerList == null)
                {
                    containerList = new List<ContainerEntry>();
                    _playerContainerEntries[player.userID] = containerList;
                }

                if (HasContainer(containerList, container))
                    return false;

                var containerEntry = new ContainerEntry
                {
                    Plugin = plugin,
                    Player = player,
                    Container = container,
                    CanUseContainer = canUseContainer,
                };

                var entity = containerEntity as BaseEntity;
                if ((object)entity != null)
                {
                    EntityTracker entityTracker;
                    if (!_entityTrackers.TryGetValue(entity, out entityTracker))
                    {
                        entityTracker = EntityTracker.AddToEntity(entity, this);
                        _entityTrackers[entity] = entityTracker;
                    }
                    containerEntry.EntityTracker = entityTracker;
                    entityTracker.AddContainer(containerEntry);
                }

                containerList.Add(containerEntry);
                return true;
            }

            public bool RemoveContainer(Plugin plugin, BasePlayer player, ItemContainer container)
            {
                var anyRemoved = false;

                var containerList = GetContainerList(player);
                if (containerList != null)
                {
                    anyRemoved |= RemoveEntry(containerList, plugin, player, container);
                }

                if (containerList.Count == 0)
                {
                    _playerContainerEntries.Remove(player.userID);
                }

                return anyRemoved;
            }

            public bool RemoveContainer(ContainerEntry containerEntry)
            {
                return RemoveContainer(containerEntry.Plugin, containerEntry.Player, containerEntry.Container);
            }

            public bool RemoveContainers(Plugin plugin, BasePlayer player)
            {
                var anyRemoved = false;

                var containerList = GetContainerList(player);
                if (containerList != null)
                {
                    anyRemoved |= RemoveEntries(containerList, player);

                    if (containerList.Count == 0)
                    {
                        _playerContainerEntries.Remove(player.userID);
                    }
                }

                return anyRemoved;
            }

            public HashSet<BasePlayer> RemoveContainers(Plugin plugin)
            {
                HashSet<BasePlayer> updatedPlayers = null;

                List<ulong> removePlayerIds = null;

                foreach (var playerEntry in _playerContainerEntries)
                {
                    var player = playerEntry.Key;
                    var containerList = playerEntry.Value;

                    List<ContainerEntry> removeContainers = null;

                    foreach (var containerEntry in containerList)
                    {
                        if (containerEntry.Plugin == plugin)
                        {
                            if (removeContainers == null)
                            {
                                removeContainers = new List<ContainerEntry>();
                            }

                            removeContainers.Add(containerEntry);

                            if (updatedPlayers == null)
                            {
                                updatedPlayers = new HashSet<BasePlayer>();
                            }

                            updatedPlayers.Add(containerEntry.Player);
                        }
                    }

                    if (removeContainers != null)
                    {
                        foreach (var containerEntry in removeContainers)
                        {
                            containerList.Remove(containerEntry);
                            containerEntry.Deactivate();
                        }
                    }

                    if (containerList.Count == 0)
                    {
                        if (removePlayerIds == null)
                        {
                            removePlayerIds = new List<ulong>();
                        }

                        removePlayerIds.Add(player);
                    }
                }

                if (removePlayerIds != null)
                {
                    foreach (var playerId in removePlayerIds)
                    {
                        _playerContainerEntries.Remove(playerId);
                    }
                }

                return updatedPlayers;
            }

            public void RemoveContainers()
            {
                foreach (var containerList in _playerContainerEntries.Values)
                {
                    foreach (var containerEntry in containerList)
                    {
                        containerEntry.Deactivate();
                    }
                }
            }

            public List<ContainerEntry> GetContainerList(BasePlayer player)
            {
                List<ContainerEntry> containerList;
                return _playerContainerEntries.TryGetValue(player.userID, out containerList)
                    ? containerList
                    : null;
            }

            private bool HasContainer(List<ContainerEntry> containerList, ItemContainer container)
            {
                foreach (var containerEntry in containerList)
                {
                    if (containerEntry.Container == container)
                        return true;
                }

                return false;
            }

            private bool RemoveEntries(List<ContainerEntry> containerList, Plugin plugin)
            {
                var anyRemoved = false;

                for (var i = containerList.Count - 1; i >= 0; i--)
                {
                    var containerEntry = containerList[i];
                    if (containerEntry.Plugin == plugin)
                    {
                        containerList.RemoveAt(i);
                        containerEntry.Deactivate();
                        anyRemoved = true;
                    }
                }

                return anyRemoved;
            }

            private bool RemoveEntries(List<ContainerEntry> containerList, BasePlayer player)
            {
                var anyRemoved = false;

                for (var i = containerList.Count - 1; i >= 0; i--)
                {
                    var containerEntry = containerList[i];
                    if (containerEntry.Player == player)
                    {
                        containerList.RemoveAt(i);
                        containerEntry.Deactivate();
                        anyRemoved = true;
                    }
                }

                return anyRemoved;
            }

            private bool RemoveEntry(List<ContainerEntry> containerList, Plugin plugin, BasePlayer player, ItemContainer container)
            {
                for (var i = containerList.Count - 1; i >= 0; i--)
                {
                    var containerEntry = containerList[i];
                    if (containerEntry.Plugin == plugin
                        && containerEntry.Player == player
                        && containerEntry.Container == container)
                    {
                        containerList.RemoveAt(i);
                        containerEntry.Deactivate();
                        return true;
                    }
                }

                return false;
            }
        }

        #endregion
    }
}
