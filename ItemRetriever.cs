using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Rust;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Item Retriever", "WhiteThunder", "0.3.0")]
    [Description("Allows players to build, craft, reload and more using items from external containers.")]
    internal class ItemRetriever : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin Backpacks;

        private const int InventorySize = 24;

        private const string PermissionAdmin = "itemretriever.admin";

        private static readonly object True = true;
        private static readonly object False = false;

        private ContainerManager _containerManager = new ContainerManager();
        private readonly ApiInstance _api;

        private bool _callingCanCraft = false;

        public ItemRetriever()
        {
            _api = new ApiInstance(this);
        }

        #endregion

        #region Hooks

        private void Unload()
        {
            _containerManager.RemoveContainers();
            ObjectCache.Clear<int>();
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
            var itemQuery = new ItemIdQuery(itemId);
            return ObjectCache.Get(SumPlayerItems(inventory.baseEntity, ref itemQuery));
        }

        private object OnInventoryItemsTake(PlayerInventory inventory, List<Item> collect, int itemId, int amount)
        {
            var itemQuery = new ItemIdQuery(itemId);
            return ObjectCache.Get(TakePlayerItems(inventory.baseEntity, ref itemQuery, amount, collect));
        }

        private object OnInventoryItemsFind(PlayerInventory inventory, int itemId)
        {
            var itemQuery = new ItemIdQuery(itemId);
            var list = new List<Item>();
            FindPlayerItems(inventory.baseEntity, ref itemQuery, list);
            return list;
        }

        private object OnInventoryAmmoFind(PlayerInventory inventory, List<Item> collect, AmmoTypes ammoType)
        {
            FindPlayerAmmo(inventory.baseEntity, ammoType, collect);
            return False;
        }

        private object OnIngredientsCollect(ItemCrafter itemCrafter, ItemBlueprint blueprint, ItemCraftTask task, int amount, BasePlayer player)
        {
            var collect = new List<Item>();
            foreach (ItemAmount ingredient in blueprint.ingredients)
            {
                var itemQuery = new ItemIdQuery(ingredient.itemid);
                TakePlayerItems(player, ref itemQuery, (int)ingredient.amount * amount, collect);
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

            var canCraftResult = Interface.CallHook("CanCraft", itemCrafter, blueprint, ObjectCache.Get(amount), BooleanNoAlloc(free));
            if (canCraftResult != null)
                return null;

            _callingCanCraft = false;

            var basePlayer = itemCrafter.baseEntity;

            foreach (ItemAmount ingredient in blueprint.ingredients)
            {
                var itemQuery = new ItemIdQuery(ingredient.itemid);
                if (SumPlayerItems(basePlayer, ref itemQuery) < ingredient.amount * amount)
                    return null;
            }

            return True;
        }

        #endregion

        #region Commands

        [Command("retriever.add"), Permission(PermissionAdmin)]
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

        [Command("retriever.remove"), Permission(PermissionAdmin)]
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

        [Command("retriever.backpack.add"), Permission(PermissionAdmin)]
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

        [Command("retriever.backpack.remove"), Permission(PermissionAdmin)]
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

        private class ApiInstance
        {
            public readonly Dictionary<string, object> ApiWrapper;

            private readonly ItemRetriever _plugin;
            private ContainerManager _containerManager => _plugin._containerManager;

            public ApiInstance(ItemRetriever plugin)
            {
                _plugin = plugin;

                ApiWrapper = new Dictionary<string, object>
                {
                    [nameof(AddContainer)] = new Action<Plugin, BasePlayer, IItemContainerEntity, ItemContainer, Func<Plugin, BasePlayer, ItemContainer, bool>>(AddContainer),
                    [nameof(RemoveContainer)] = new Action<Plugin, BasePlayer, ItemContainer>(RemoveContainer),
                    [nameof(RemoveAllContainersForPlayer)] = new Action<Plugin, BasePlayer>(RemoveAllContainersForPlayer),
                    [nameof(RemoveAllContainersForPlugin)] = new Action<Plugin>(RemoveAllContainersForPlugin),
                    [nameof(FindPlayerItems)] = new Action<BasePlayer, Dictionary<string, object>, List<Item>>(FindPlayerItems),
                    [nameof(SumPlayerItems)] = new Func<BasePlayer, Dictionary<string, object>, int>(SumPlayerItems),
                    [nameof(TakePlayerItems)] = new Func<BasePlayer, Dictionary<string, object>, int, List<Item>, int>(TakePlayerItems),
                    [nameof(FindPlayerAmmo)] = new Action<BasePlayer, AmmoTypes, List<Item>>(FindPlayerAmmo),
                };
            }

            public void AddContainer(Plugin plugin, BasePlayer player, IItemContainerEntity containerEntity, ItemContainer container, Func<Plugin, BasePlayer, ItemContainer, bool> canUseContainer = null)
            {
                if (_containerManager.AddContainer(plugin, player, containerEntity, container, canUseContainer))
                {
                    SendInventoryUpdate(player);
                }
            }

            public void RemoveContainer(Plugin plugin, BasePlayer player, ItemContainer container)
            {
                if (_containerManager.RemoveContainer(plugin, player, container))
                {
                    SendInventoryUpdate(player);
                }
            }

            public void RemoveAllContainersForPlayer(Plugin plugin, BasePlayer player)
            {
                if (_containerManager.RemoveContainers(plugin, player))
                {
                    SendInventoryUpdate(player);
                }
            }

            public void RemoveAllContainersForPlugin(Plugin plugin)
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

            public void FindPlayerItems(BasePlayer player, Dictionary<string, object> itemQueryDict, List<Item> collect)
            {
                var itemQuery = ItemQuery.FromDict(itemQueryDict);
                _plugin.FindPlayerItems(player, ref itemQuery, collect);
            }

            public int SumPlayerItems(BasePlayer player, Dictionary<string, object> itemQueryDict)
            {
                var itemQuery = ItemQuery.FromDict(itemQueryDict);
                return _plugin.SumPlayerItems(player, ref itemQuery);
            }

            public int TakePlayerItems(BasePlayer player, Dictionary<string, object> itemQueryDict, int amount, List<Item> collect)
            {
                var itemQuery = ItemQuery.FromDict(itemQueryDict);
                return _plugin.TakePlayerItems(player, ref itemQuery, amount, collect);
            }

            public void FindPlayerAmmo(BasePlayer player, AmmoTypes ammoType, List<Item> collect)
            {
                _plugin.FindPlayerAmmo(player, ammoType, collect);
            }
        }

        [HookMethod(nameof(API_GetApi))]
        public Dictionary<string, object> API_GetApi()
        {
            return _api.ApiWrapper;
        }

        [HookMethod(nameof(API_AddContainer))]
        public void API_AddContainer(Plugin plugin, BasePlayer player, IItemContainerEntity containerEntity, ItemContainer container, Func<Plugin, BasePlayer, ItemContainer, bool> canUseContainer = null)
        {
            _api.AddContainer(plugin, player, containerEntity, container, canUseContainer);
        }

        [HookMethod(nameof(API_RemoveAllContainersForPlayer))]
        public void API_RemoveAllContainersForPlayer(Plugin plugin, BasePlayer player, ItemContainer container)
        {
            _api.RemoveContainer(plugin, player, container);
        }

        [HookMethod(nameof(API_RemoveContainers))]
        public void API_RemoveContainers(Plugin plugin, BasePlayer player)
        {
            _api.RemoveAllContainersForPlayer(plugin, player);
        }

        [HookMethod(nameof(API_RemoveAllContainersForPlugin))]
        public void API_RemoveAllContainersForPlugin(Plugin plugin)
        {
            _api.RemoveAllContainersForPlugin(plugin);
        }

        [HookMethod(nameof(API_FindPlayerItems))]
        public void API_FindPlayerItems(BasePlayer player, Dictionary<string, object> itemQuery, List<Item> collect)
        {
            _api.FindPlayerItems(player, itemQuery, collect);
        }

        [HookMethod(nameof(API_SumPlayerItems))]
        public object API_SumPlayerItems(BasePlayer player, Dictionary<string, object> itemQuery)
        {
            return ObjectCache.Get(_api.SumPlayerItems(player, itemQuery));
        }

        [HookMethod(nameof(API_TakePlayerItems))]
        public object API_TakePlayerItems(BasePlayer player, Dictionary<string, object> itemQuery, int amount, List<Item> collect)
        {
            return ObjectCache.Get(_api.TakePlayerItems(player, itemQuery, amount, collect));
        }

        [HookMethod(nameof(API_FindPlayerAmmo))]
        public void API_FindPlayerAmmo(BasePlayer player, AmmoTypes ammoType, List<Item> collect)
        {
            _api.FindPlayerAmmo(player, ammoType, collect);
        }

        #endregion

        #region Helper Methods

        private static object BooleanNoAlloc(bool value)
        {
            return value ? True : False;
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
                    if (!containerEntry.CanUse())
                        continue;

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

        private void FindContainerItems<T>(ItemContainer container, ref T itemQuery, List<Item> collect, bool childItemsOnly = false) where T : IItemQuery
        {
            foreach (var item in container.itemList)
            {
                var usableAmount = childItemsOnly ? 0 : itemQuery.GetUsableAmount(item);
                if (usableAmount > 0)
                {
                    collect.Add(item);
                }

                ItemContainer childContainer;
                if (HasSearchableContainer(item, out childContainer))
                {
                    FindContainerItems(childContainer, ref itemQuery, collect);
                }
            }
        }

        private void FindPlayerItems<T>(BasePlayer player, ref T itemQuery, List<Item> collect) where T : IItemQuery
        {
            FindContainerItems(player.inventory.containerMain, ref itemQuery, collect);
            FindContainerItems(player.inventory.containerBelt, ref itemQuery, collect);
            FindContainerItems(player.inventory.containerWear, ref itemQuery, collect, childItemsOnly: true);

            var containerList = _containerManager.GetContainerList(player);
            if (containerList != null)
            {
                foreach (var containerEntry in containerList)
                {
                    if (!containerEntry.CanUse())
                        continue;

                    FindContainerItems(containerEntry.Container, ref itemQuery, collect);
                }
            }
        }

        private int SumContainerItems<T>(ItemContainer container, ref T itemQuery, bool childItemsOnly = false) where T : IItemQuery
        {
            var sum = 0;

            foreach (var item in container.itemList)
            {
                sum += childItemsOnly ? 0 : itemQuery.GetUsableAmount(item);

                ItemContainer childContainer;
                if (HasSearchableContainer(item, out childContainer))
                {
                    sum += SumContainerItems(childContainer, ref itemQuery);
                }
            }

            return sum;
        }

        private int SumPlayerItems<T>(BasePlayer player, ref T itemQuery) where T : IItemQuery
        {
            var sum = SumContainerItems(player.inventory.containerMain, ref itemQuery)
                + SumContainerItems(player.inventory.containerBelt, ref itemQuery)
                + SumContainerItems(player.inventory.containerWear, ref itemQuery, childItemsOnly: true);

            var containerList = _containerManager.GetContainerList(player);
            if (containerList != null)
            {
                foreach (var containerEntry in containerList)
                {
                    if (!containerEntry.CanUse())
                        continue;

                    sum += SumContainerItems(containerEntry.Container, ref itemQuery);
                }
            }

            return sum;
        }

        private int TakeContainerItems<T>(ItemContainer container, ref T itemQuery, int totalAmountToTake, List<Item> collect, bool childItemsOnly = false) where T : IItemQuery
        {
            var totalAmountTaken = 0;

            for (var i = container.itemList.Count - 1; i >= 0; i--)
            {
                var amountToTake = totalAmountToTake - totalAmountTaken;
                if (amountToTake <= 0)
                    break;

                var item = container.itemList[i];
                var usableAmount = childItemsOnly ? 0 : itemQuery.GetUsableAmount(item);
                if (usableAmount > 0)
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
                    totalAmountTaken += TakeContainerItems(childContainer, ref itemQuery, amountToTake, collect);
                }

                if (totalAmountTaken >= totalAmountToTake)
                    return totalAmountTaken;
            }

            return totalAmountTaken;
        }

        private int TakePlayerItems<T>(BasePlayer player, ref T itemQuery, int amountToTake, List<Item> collect) where T : IItemQuery
        {
            var amountTaken = TakeContainerItems(player.inventory.containerMain, ref itemQuery, amountToTake, collect);
            if (amountTaken >= amountToTake)
                return amountTaken;

            amountTaken += TakeContainerItems(player.inventory.containerBelt, ref itemQuery, amountToTake - amountTaken, collect);
            if (amountTaken >= amountToTake)
                return amountTaken;

            amountTaken += TakeContainerItems(player.inventory.containerWear, ref itemQuery, amountToTake - amountTaken, collect, childItemsOnly: true);
            if (amountTaken >= amountToTake)
                return amountTaken;

            var containerList = _containerManager.GetContainerList(player);
            if (containerList != null)
            {
                foreach (var containerEntry in containerList)
                {
                    if (!containerEntry.CanUse())
                        continue;

                    amountTaken += TakeContainerItems(containerEntry.Container, ref itemQuery, amountToTake - amountTaken, collect);
                    if (amountTaken >= amountToTake)
                        return amountTaken;
                }
            }

            return amountTaken;
        }

        private void FindPlayerAmmo(BasePlayer player, AmmoTypes ammoType, List<Item> collect)
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
                    if (!containerEntry.CanUse())
                        continue;

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

        #region Helper Classes

        private static class StringUtils
        {
            public static bool Equals(string a, string b) =>
                string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;

            public static bool Contains(string haystack, string needle) =>
                haystack.Contains(needle, CompareOptions.IgnoreCase);
        }

        private static class ObjectCache
        {
            private static class StaticObjectCache<T>
            {
                private static readonly Dictionary<T, object> _cacheByValue = new Dictionary<T, object>();

                public static object Get(T value)
                {
                    object cachedObject;
                    if (!_cacheByValue.TryGetValue(value, out cachedObject))
                    {
                        cachedObject = value;
                        _cacheByValue[value] = cachedObject;
                    }
                    return cachedObject;
                }

                public static void Clear()
                {
                    _cacheByValue.Clear();
                }
            }

            public static object Get<T>(T value)
            {
                return StaticObjectCache<T>.Get(value);
            }

            public static void Clear<T>()
            {
                StaticObjectCache<T>.Clear();
            }
        }

        #endregion

        #region Item Query

        private interface IItemQuery
        {
            int GetUsableAmount(Item item);
        }

        private struct ItemIdQuery : IItemQuery
        {
            public int ItemId;

            public ItemIdQuery(int itemId)
            {
                ItemId = itemId;
            }

            public int GetUsableAmount(Item item)
            {
                return ItemId != item.info.itemid ? 0 : item.amount;
            }
        }

        private struct ItemQuery : IItemQuery
        {
            public static ItemQuery FromDict(Dictionary<string, object> dict)
            {
                var itemQuery = new ItemQuery
                {
                    RawQuery = dict,
                };

                GetOption(dict, "BlueprintId", out itemQuery.BlueprintId);
                GetOption(dict, "DisplayName", out itemQuery.DisplayName);
                GetOption(dict, "DataInt", out itemQuery.DataInt);
                GetOption(dict, "FlagsContain", out itemQuery.FlagsContain);
                GetOption(dict, "FlagsEqual", out itemQuery.FlagsEqual);
                GetOption(dict, "ItemDefinition", out itemQuery.ItemDefinition);
                GetOption(dict, "ItemId", out itemQuery.ItemId);
                GetOption(dict, "MinCondition", out itemQuery.MinCondition);
                GetOption(dict, "RequireEmpty", out itemQuery.RequireEmpty);
                GetOption(dict, "SkinId", out itemQuery.SkinId);

                return itemQuery;
            }

            private static void GetOption<T>(Dictionary<string, object> dict, string key, out T result)
            {
                object value;
                result = dict.TryGetValue(key, out value) && value is T
                    ? (T)value
                    : default(T);
            }

            public Dictionary<string, object> RawQuery;
            public int? BlueprintId;
            public int? DataInt;
            public string DisplayName;
            public Item.Flag? FlagsContain;
            public Item.Flag? FlagsEqual;
            public ItemDefinition ItemDefinition;
            public int? ItemId;
            public float MinCondition;
            public bool RequireEmpty;
            public ulong? SkinId;

            public int GetUsableAmount(Item item)
            {
                var itemId = GetItemId();
                if (itemId.HasValue && itemId != item.info.itemid)
                    return 0;

                if (SkinId.HasValue && SkinId != item.skin)
                    return 0;

                if (BlueprintId.HasValue && BlueprintId != item.blueprintTarget)
                    return 0;

                if (DataInt.HasValue && DataInt != (item.instanceData?.dataInt ?? 0))
                    return 0;

                if (FlagsContain.HasValue && !item.flags.HasFlag(FlagsContain.Value))
                    return 0;

                if (FlagsEqual.HasValue && FlagsEqual != item.flags)
                    return 0;

                if (MinCondition > 0 && HasCondition() && (item.conditionNormalized < MinCondition || item.maxConditionNormalized < MinCondition))
                    return 0;

                if (!string.IsNullOrEmpty(DisplayName) && !StringUtils.Equals(DisplayName, item.name))
                    return 0;

                return RequireEmpty && item.contents?.itemList?.Count > 0
                    ? Math.Max(0, item.amount - 1)
                    : item.amount;
            }

            private int? GetItemId()
            {
                if (ItemDefinition != null)
                    return ItemDefinition?.itemid ?? ItemId;

                return ItemId;
            }

            private ItemDefinition GetItemDefinition()
            {
                if ((object)ItemDefinition == null && ItemId.HasValue)
                {
                    ItemDefinition = ItemManager.FindItemDefinition(ItemId.Value);
                }

                return ItemDefinition;
            }

            private bool HasCondition()
            {
                return GetItemDefinition()?.condition.enabled ?? false;
            }
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
