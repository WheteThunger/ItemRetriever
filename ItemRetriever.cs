using HarmonyLib;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Oxide.Plugins
{
    [Info("Item Retriever", "WhiteThunder", "0.7.4")]
    [Description("Allows players to build, craft, reload and more using items from external containers.")]
    internal class ItemRetriever : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private readonly Plugin InstantCraft;

        private static ItemRetriever _instance;

        private const int InventorySize = 24;
        private const Item.Flag SearchableItemFlag = (Item.Flag)(1 << 24);
        private const Item.Flag UnsearchableItemFlag = (Item.Flag)(1 << 25);
        private const ItemDefinition.Flag SearchableItemDefinitionFlag = (ItemDefinition.Flag)(1 << 24);

        // The CustomItemDefinitions plugin adds this flag to all custom item definitions.
        // Custom items to be skipped when adding items to network messages, since they have invalid item ids.
        private const ItemDefinition.Flag CustomItemDefinitionFlag = (ItemDefinition.Flag)128;

        private static readonly object True = true;
        private static readonly object False = false;

        private readonly CustomItemDefinitionTracker _customItemDefinitionTracker = new();
        private readonly SupplierManager _supplierManager;
        private readonly ContainerManager _containerManager;
        private readonly ApiInstance _api;
        private readonly Dictionary<int, int> _overridenIngredients = new();
        private readonly List<Item> _reusableItemList = new();
        private Func<ItemDefinition, bool> _isBlockedByInstantCraft;

        public ItemRetriever()
        {
            _api = new ApiInstance(this);
            _supplierManager = new(_customItemDefinitionTracker);
            _containerManager = new(_customItemDefinitionTracker);
        }

        #endregion

        #region Harmony Patches

        [AutoPatch]
        [HarmonyPatch(typeof(PlayerInventory), "FindItemByItemID", typeof(int))]
        private static class Patch_PlayerInventory_FindItemByItemID
        {
            private static readonly CodeInstruction OnInventoryItemFindInstruction = new(OpCodes.Ldstr, nameof(OnInventoryItemFind));

            private static readonly MethodInfo _replacementMethod = typeof(Patch_PlayerInventory_FindItemByItemID)
                .GetMethod(nameof(ReplacementMethod), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            private static readonly CodeInstruction[] _newInstructions =
            {
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldarg_1),
                new(OpCodes.Call, _replacementMethod),
                new(OpCodes.Ret),
            };

            private static Item ReplacementMethod(PlayerInventory playerInventory, int itemId)
            {
                if (_instance is { IsLoaded: true })
                {
                    return _instance.Call(nameof(OnInventoryItemFind), playerInventory, itemId) as Item;
                }

                return null;
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                if (instructions.Any(instruction => HarmonyUtils.InstructionsMatch(instruction, OnInventoryItemFindInstruction)))
                    return instructions;

                return _newInstructions;
            }
        }

        [AutoPatch]
        [HarmonyPatch(typeof(PlayerInventory), "FindAmmo", typeof(AmmoTypes))]
        private static class Patch_PlayerInventory_FindAmmo
        {
            private static readonly CodeInstruction OnInventoryAmmoItemFindInstruction = new(OpCodes.Ldstr, nameof(OnInventoryAmmoItemFind));

            private static readonly MethodInfo _replacementMethod = typeof(Patch_PlayerInventory_FindAmmo)
                .GetMethod(nameof(ReplacementMethod), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            private static readonly CodeInstruction[] _newInstructions =
            {
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldarg_1),
                new(OpCodes.Call, _replacementMethod),
                new(OpCodes.Ret),
            };

            private static Item ReplacementMethod(PlayerInventory playerInventory, AmmoTypes ammoTypes)
            {
                if (_instance is { IsLoaded: true })
                {
                    return _instance.Call(nameof(OnInventoryAmmoItemFind), playerInventory, ammoTypes) as Item;
                }

                return null;
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                if (instructions.Any(instruction => HarmonyUtils.InstructionsMatch(instruction, OnInventoryAmmoItemFindInstruction)))
                    return instructions;

                return _newInstructions;
            }
        }

        private static class HarmonyUtils
        {
            public static bool InstructionsMatch(CodeInstruction a, CodeInstruction b)
            {
                if (a.opcode != b.opcode)
                    return false;

                if (a.operand == null)
                    return b.operand == null;

                return a.operand.Equals(b.operand);
            }
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _instance = this;
        }

        private void OnServerInitialized()
        {
            RefreshInstantCraftCallback();
        }

        private void Unload()
        {
            _containerManager.RemoveContainers();
            ObjectCache.Clear<int>();
            ObjectCache.Clear<float>();
            ObjectCache.Clear<ulong>();
            ObjectCache.Clear<Item.Flag>();

            _instance = null;
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == nameof(InstantCraft))
            {
                RefreshInstantCraftCallback();
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            _supplierManager.RemoveSupplier(plugin);
            _containerManager.RemoveContainers(plugin);

            if (plugin.Name == nameof(InstantCraft))
            {
                RefreshInstantCraftCallback();
            }
        }

        private void OnEntitySaved(BasePlayer player, BaseNetworkable.SaveInfo saveInfo)
        {
            SerializeForNetwork(player, saveInfo.msg.basePlayer.inventory.invMain);
        }

        private void OnInventoryNetworkUpdate(PlayerInventory inventory, ItemContainer container, ProtoBuf.UpdateItemContainer updatedItemContainer, PlayerInventory.Type inventoryType)
        {
            if (inventoryType != PlayerInventory.Type.Main)
                return;

            SerializeForNetwork(inventory.baseEntity, updatedItemContainer.container[0]);
        }

        private object OnInventoryItemsCount(PlayerInventory inventory, int itemId)
        {
            var itemQuery = new ItemIdQuery(itemId);
            return ObjectCache.Get(SumPlayerItems(inventory.baseEntity, ref itemQuery, countPlayerWearables: true));
        }

        private object OnInventoryItemsTake(PlayerInventory inventory, List<Item> collect, int itemId, int amount)
        {
            var itemQuery = new ItemIdQuery(itemId);
            return ObjectCache.Get(TakePlayerItems(inventory.baseEntity, ref itemQuery, amount, collect, includePlayerWearables: true));
        }

        private object OnInventoryItemsFind(PlayerInventory inventory, int itemId)
        {
            var itemQuery = new ItemIdQuery(itemId);
            var list = new List<Item>();
            FindPlayerItems(inventory.baseEntity, ref itemQuery, list, includePlayerWearables: true);
            return list;
        }

        private object OnInventoryItemFind(PlayerInventory inventory, int itemId)
        {
            var itemQuery = new ItemIdQuery(itemId);
            _reusableItemList.Clear();
            FindPlayerItems(inventory.baseEntity, ref itemQuery, _reusableItemList, includePlayerWearables: true);
            return _reusableItemList.FirstOrDefault();
        }

        private object OnInventoryAmmoFind(PlayerInventory inventory, List<Item> collect, AmmoTypes ammoType)
        {
            FindPlayerAmmo(inventory.baseEntity, ammoType, collect);
            return False;
        }

        private Item OnInventoryAmmoItemFind(PlayerInventory inventory, ItemDefinition itemDefinition)
        {
            if ((object)itemDefinition == null)
                return null;

            _reusableItemList.Clear();
            var itemQuery = new ItemIdQuery(itemDefinition.itemid);
            FindPlayerItems(inventory.baseEntity, ref itemQuery, _reusableItemList, includePlayerWearables: true);
            return _reusableItemList.FirstOrDefault();
        }

        private Item OnInventoryAmmoItemFind(PlayerInventory inventory, AmmoTypes ammoType)
        {
            _reusableItemList.Clear();
            FindPlayerAmmo(inventory.baseEntity, ammoType, _reusableItemList);
            return _reusableItemList.FirstOrDefault();
        }

        private object OnIngredientsCollect(ItemCrafter itemCrafter, ItemBlueprint blueprint, ItemCraftTask task, int amount, BasePlayer player)
        {
            ExposedHooks.OnIngredientsDetermine(_overridenIngredients, blueprint, amount, player);

            var collect = new List<Item>();
            if (_overridenIngredients.Count > 0)
            {
                foreach (var entry in _overridenIngredients)
                {
                    if (entry.Value <= 0)
                        continue;

                    var itemQuery = new ItemIdQuery(entry.Key);
                    TakePlayerItems(player, ref itemQuery, entry.Value, collect, task);
                }
            }
            else
            {
                for (var i = 0; i < blueprint.ingredients.Count; i++)
                {
                    var ingredient = blueprint.ingredients[i];
                    var itemQuery = new ItemIdQuery(ingredient.itemid);
                    TakePlayerItems(player, ref itemQuery, (int)ingredient.amount * amount, collect, task);
                }
            }

            for (var i = 0; i < collect.Count; i++)
            {
                collect[i].CollectedForCrafting(player);
            }

            task.takenItems = collect;
            return False;
        }

        private object CanCraft(ItemCrafter itemCrafter, ItemBlueprint blueprint, int amount, bool free)
        {
            if (_isBlockedByInstantCraft?.Invoke(blueprint.targetItem) == true)
                return null;

            var basePlayer = itemCrafter.baseEntity;
            ExposedHooks.OnIngredientsDetermine(_overridenIngredients, blueprint, amount, basePlayer);

            if (_overridenIngredients.Count > 0)
            {
                foreach (var entry in _overridenIngredients)
                {
                    if (entry.Value <= 0)
                        continue;

                    var itemQuery = new ItemIdQuery(entry.Key);
                    if (SumPlayerItems(basePlayer, ref itemQuery) < entry.Value)
                        return False;
                }
            }
            else
            {
                for (var i = 0; i < blueprint.ingredients.Count; i++)
                {
                    var ingredient = blueprint.ingredients[i];
                    var itemQuery = new ItemIdQuery(ingredient.itemid);
                    if (SumPlayerItems(basePlayer, ref itemQuery) < ingredient.amount * amount)
                        return False;
                }
            }

            return True;
        }

        #endregion

        #region API

        private class ApiInstance
        {
            public readonly Dictionary<string, object> ApiWrapper;

            private readonly ItemRetriever _plugin;
            private SupplierManager _supplierManager => _plugin._supplierManager;
            private ContainerManager _containerManager => _plugin._containerManager;

            public ApiInstance(ItemRetriever plugin)
            {
                _plugin = plugin;

                ApiWrapper = new Dictionary<string, object>
                {
                    [nameof(AddSupplier)] = new Action<Plugin, Dictionary<string, object>>(AddSupplier),
                    [nameof(RemoveSupplier)] = new Action<Plugin>(RemoveSupplier),
                    [nameof(HasContainer)] = new Func<BasePlayer, ItemContainer, bool>(HasContainer),
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

            public void AddSupplier(Plugin plugin, Dictionary<string, object> spec)
            {
                if (plugin == null)
                    throw new ArgumentNullException(nameof(plugin));

                _supplierManager.AddSupplier(plugin, spec);
            }

            public void RemoveSupplier(Plugin plugin)
            {
                if (plugin == null)
                    throw new ArgumentNullException(nameof(plugin));

                _supplierManager.RemoveSupplier(plugin);
            }

            public bool HasContainer(BasePlayer player, ItemContainer container)
            {
                return _containerManager.HasContainer(player, container);
            }

            public void AddContainer(Plugin plugin, BasePlayer player, IItemContainerEntity containerEntity, ItemContainer container, Func<Plugin, BasePlayer, ItemContainer, bool> canUseContainer)
            {
                if (_containerManager.AddContainer(plugin, player, containerEntity, container, canUseContainer))
                {
                    MarkInventoryDirty(player);
                }
            }

            public void RemoveContainer(Plugin plugin, BasePlayer player, ItemContainer container)
            {
                if (_containerManager.RemoveContainer(plugin, player, container))
                {
                    MarkInventoryDirty(player);
                }
            }

            public void RemoveAllContainersForPlayer(Plugin plugin, BasePlayer player)
            {
                if (_containerManager.RemoveContainers(plugin, player))
                {
                    MarkInventoryDirty(player);
                }
            }

            public void RemoveAllContainersForPlugin(Plugin plugin)
            {
                var updatedPlayers = _containerManager.RemoveContainers(plugin);
                if ((updatedPlayers?.Count ?? 0) > 0)
                {
                    foreach (var player in updatedPlayers)
                    {
                        MarkInventoryDirty(player);
                    }
                }
            }

            public void FindPlayerItems(BasePlayer player, Dictionary<string, object> itemQueryDict, List<Item> collect)
            {
                var itemQuery = ItemQuery.Parse(itemQueryDict);
                _plugin.FindPlayerItems(player, ref itemQuery, collect);
            }

            public int SumPlayerItems(BasePlayer player, Dictionary<string, object> itemQueryDict)
            {
                var itemQuery = ItemQuery.Parse(itemQueryDict);
                return _plugin.SumPlayerItems(player, ref itemQuery);
            }

            public int TakePlayerItems(BasePlayer player, Dictionary<string, object> itemQueryDict, int amount, List<Item> collect)
            {
                var itemQuery = ItemQuery.Parse(itemQueryDict);
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

        [HookMethod(nameof(API_AddSupplier))]
        public void API_AddSupplier(Plugin plugin, Dictionary<string, object> spec)
        {
            _api.AddSupplier(plugin, spec);
        }

        [HookMethod(nameof(API_RemoveSupplier))]
        public void API_RemoveSupplier(Plugin plugin)
        {
            _api.RemoveSupplier(plugin);
        }

        [HookMethod(nameof(API_HasContainer))]
        public object API_HasContainer(BasePlayer player, ItemContainer container)
        {
            return ObjectCache.Get(_api.HasContainer(player, container));
        }

        [HookMethod(nameof(API_AddContainer))]
        public void API_AddContainer(Plugin plugin, BasePlayer player, IItemContainerEntity containerEntity, ItemContainer container, Func<Plugin, BasePlayer, ItemContainer, bool> canUseContainer = null)
        {
            _api.AddContainer(plugin, player, containerEntity, container, canUseContainer);
        }

        [HookMethod(nameof(API_RemoveContainer))]
        public void API_RemoveContainer(Plugin plugin, BasePlayer player, ItemContainer container)
        {
            _api.RemoveContainer(plugin, player, container);
        }

        [HookMethod(nameof(API_RemoveAllContainersForPlayer))]
        public void API_RemoveAllContainersForPlayer(Plugin plugin, BasePlayer player)
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

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static void OnIngredientsDetermine(Dictionary<int, int> overridenIngredients, ItemBlueprint blueprint, int amount, BasePlayer player)
            {
                overridenIngredients.Clear();
                Interface.CallHook("OnIngredientsDetermine", overridenIngredients, blueprint, ObjectCache.Get(amount), player);
            }
        }

        #endregion

        #region Helper Methods

        private static void MarkInventoryDirty(BasePlayer player)
        {
            player.inventory.containerMain?.MarkDirty();
        }

        private static int GetHighestUsedSlot(ProtoBuf.ItemContainer containerData)
        {
            var highestUsedSlot = -1;

            for (var i = 0; i < containerData.contents.Count; i++)
            {
                var item = containerData.contents[i];
                if (item.slot > highestUsedSlot)
                {
                    highestUsedSlot = item.slot;
                }
            }

            return highestUsedSlot;
        }

        private void RefreshInstantCraftCallback()
        {
            _isBlockedByInstantCraft = InstantCraft?.Call("API_GetIsItemBlockedCallback") as Func<ItemDefinition, bool>;
        }

        private void SerializeForNetwork(BasePlayer player, ProtoBuf.ItemContainer containerData)
        {
            if (containerData == null)
                return;

            var firstAvailableInvisibleSlot = Math.Max(InventorySize, GetHighestUsedSlot(containerData) + 1);
            var nextInvisibleSlot = firstAvailableInvisibleSlot;

            var itemsAdded = ItemUtils.SerializeForNetwork(_customItemDefinitionTracker, player.inventory.containerMain.itemList, containerData, ref nextInvisibleSlot, addChildContainersOnly: true)
                + ItemUtils.SerializeForNetwork(_customItemDefinitionTracker, player.inventory.containerBelt.itemList, containerData, ref nextInvisibleSlot, addChildContainersOnly: true)
                + ItemUtils.SerializeForNetwork(_customItemDefinitionTracker, player.inventory.containerWear.itemList, containerData, ref nextInvisibleSlot, addChildContainersOnly: true)
                + _supplierManager.SerializeForNetwork(player, containerData, ref nextInvisibleSlot)
                + _containerManager.SerializeForNetwork(player, containerData, ref nextInvisibleSlot);

            if (itemsAdded > 0)
            {
                if (containerData.slots >= 24)
                {
                    containerData.slots = firstAvailableInvisibleSlot + itemsAdded;
                }
            }
        }

        private void FindPlayerItems<T>(BasePlayer player, ref T itemQuery, List<Item> collect, bool includePlayerWearables = false) where T : IItemQuery
        {
            _supplierManager.FindPlayerItems(player, ref itemQuery, collect, beforeInventory: true);
            ItemUtils.FindItems(player.inventory.containerMain.itemList, ref itemQuery, collect);
            ItemUtils.FindItems(player.inventory.containerBelt.itemList, ref itemQuery, collect);
            ItemUtils.FindItems(player.inventory.containerWear.itemList, ref itemQuery, collect, childItemsOnly: !includePlayerWearables);
            _supplierManager.FindPlayerItems(player, ref itemQuery, collect);
            _containerManager.FindPlayerItems(player, ref itemQuery, collect);
        }

        private int SumPlayerItems<T>(BasePlayer player, ref T itemQuery, bool countPlayerWearables = false) where T : IItemQuery
        {
            return ItemUtils.SumItems(player.inventory.containerMain.itemList, ref itemQuery)
                + ItemUtils.SumItems(player.inventory.containerBelt.itemList, ref itemQuery)
                + ItemUtils.SumItems(player.inventory.containerWear.itemList, ref itemQuery, childItemsOnly: !countPlayerWearables)
                + _supplierManager.SumPlayerItems(player, ref itemQuery)
                + _containerManager.SumPlayerItems(player, ref itemQuery);
        }

        private int TakePlayerItems<T>(BasePlayer player, ref T itemQuery, int amountToTake, List<Item> collect, ItemCraftTask itemCraftTask = null, bool includePlayerWearables = false) where T : IItemQuery
        {
            var amountTaken = _supplierManager.TakePlayerItems(player, ref itemQuery, amountToTake, collect, itemCraftTask, beforeInventory: true);
            if (amountTaken >= amountToTake)
                return amountTaken;

            amountTaken += ItemUtils.TakeItems(player.inventory.containerMain.itemList, ref itemQuery, amountToTake - amountTaken, collect);
            if (amountTaken >= amountToTake)
                return amountTaken;

            amountTaken += ItemUtils.TakeItems(player.inventory.containerBelt.itemList, ref itemQuery, amountToTake - amountTaken, collect);
            if (amountTaken >= amountToTake)
                return amountTaken;

            amountTaken += ItemUtils.TakeItems(player.inventory.containerWear.itemList, ref itemQuery, amountToTake - amountTaken, collect, childItemsOnly: !includePlayerWearables);
            if (amountTaken >= amountToTake)
                return amountTaken;

            amountTaken += _supplierManager.TakePlayerItems(player, ref itemQuery, amountToTake - amountTaken, collect, itemCraftTask);
            if (amountTaken >= amountToTake)
                return amountTaken;

            amountTaken += _containerManager.TakePlayerItems(player, ref itemQuery, amountToTake - amountTaken, collect);

            return amountTaken;
        }

        private void FindPlayerAmmo(BasePlayer player, AmmoTypes ammoType, List<Item> collect)
        {
            _supplierManager.FindPlayerAmmo(player, ammoType, collect, beforeInventory: true);
            player.inventory.containerMain?.FindAmmo(collect, ammoType);
            player.inventory.containerBelt?.FindAmmo(collect, ammoType);
            player.inventory.containerWear?.FindAmmo(collect, ammoType);
            _supplierManager.FindPlayerAmmo(player, ammoType, collect);
            _containerManager.FindPlayerAmmo(player, ammoType, collect);
        }

        #endregion

        #region Helper Classes

        private static class StringUtils
        {
            public static bool EqualsIgnoreCase(string a, string b)
            {
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;
            }
        }

        private static class ObjectCache
        {
            private static class StaticObjectCache<T>
            {
                private static readonly Dictionary<T, object> _cacheByValue = new();

                public static object Get(T value)
                {
                    if (!_cacheByValue.TryGetValue(value, out var cachedObject))
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

            public static object Get(bool value)
            {
                return value ? True : False;
            }

            public static void Clear<T>()
            {
                StaticObjectCache<T>.Clear();
            }
        }

        private class CustomItemDefinitionTracker
        {
            private readonly HashSet<int> _customItemDefinitionIds = new();

            public bool IsCustomItemDefinition(ItemDefinition itemDefinition)
            {
                return itemDefinition.HasFlag(CustomItemDefinitionFlag);
            }

            public bool IsCustomItemDefinition(Item item)
            {
                return IsCustomItemDefinition(item.info);
            }

            public bool IsCustomItemDefinition(int itemId)
            {
                if (_customItemDefinitionIds.Contains(itemId))
                    return true;

                var itemDefinition = ItemManager.FindItemDefinition(itemId);
                if (itemDefinition == null)
                    return true;

                if (IsCustomItemDefinition(itemDefinition))
                {
                    _customItemDefinitionIds.Add(itemId);
                    return true;
                }

                return false;
            }
        }

        #endregion

        #region Item Utils

        private static class ItemUtils
        {
            public static int SerializeForNetwork(CustomItemDefinitionTracker customItemDefinitionTracker,
                List<Item> itemList, ProtoBuf.ItemContainer containerData, ref int nextInvisibleSlot, bool addChildContainersOnly = false)
            {
                var itemsAdded = 0;

                for (var i = 0; i < itemList.Count; i++)
                {
                    var item = itemList[i];
                    if (customItemDefinitionTracker.IsCustomItemDefinition(item))
                        continue;

                    if (!addChildContainersOnly)
                    {
                        var itemData = item.Save();
                        itemData.slot = nextInvisibleSlot++;
                        containerData.contents.Add(itemData);
                        itemsAdded++;
                    }

                    if (HasSearchableContainer(item, out var childItemList))
                    {
                        itemsAdded += SerializeForNetwork(customItemDefinitionTracker, childItemList, containerData, ref nextInvisibleSlot);
                    }
                }

                return itemsAdded;
            }

            public static int SerializeForNetwork(CustomItemDefinitionTracker customItemDefinitionTracker,
                List<ProtoBuf.Item> itemList, ProtoBuf.ItemContainer containerData, ref int nextInvisibleSlot)
            {
                var itemsAdded = 0;

                for (var i = 0; i < itemList.Count; i++)
                {
                    var itemData = itemList[i];
                    if (customItemDefinitionTracker.IsCustomItemDefinition(itemData.itemid))
                        continue;

                    itemData.slot = nextInvisibleSlot++;
                    if (itemData.UID.Value == 0)
                    {
                        // Items that don't have UIDs (fake items) need unique UIDs.
                        itemData.UID = new ItemId(ulong.MaxValue - (ulong)nextInvisibleSlot);
                    }

                    containerData.contents.Add(itemData);
                    itemsAdded++;

                    if (HasSearchableContainer(itemData, out var childItemList))
                    {
                        itemsAdded += SerializeForNetwork(customItemDefinitionTracker, childItemList, containerData, ref nextInvisibleSlot);
                    }
                }

                return itemsAdded;
            }

            public static void FindItems<T>(List<Item> itemList, ref T itemQuery, List<Item> collect, bool childItemsOnly = false) where T : IItemQuery
            {
                for (var i = 0; i < itemList.Count; i++)
                {
                    var item = itemList[i];
                    var usableAmount = childItemsOnly ? 0 : itemQuery.GetUsableAmount(item);
                    if (usableAmount > 0)
                    {
                        collect.Add(item);
                    }

                    if (HasSearchableContainer(item, out var childItemList))
                    {
                        FindItems(childItemList, ref itemQuery, collect);
                    }
                }
            }

            public static int SumItems<T>(List<Item> itemList, ref T itemQuery, bool childItemsOnly = false) where T : IItemQuery
            {
                var sum = 0;

                for (var i = 0; i < itemList.Count; i++)
                {
                    var item = itemList[i];
                    sum += childItemsOnly ? 0 : itemQuery.GetUsableAmount(item);

                    if (HasSearchableContainer(item, out var childItemList))
                    {
                        sum += SumItems(childItemList, ref itemQuery);
                    }
                }

                return sum;
            }

            public static int TakeItems<T>(List<Item> itemList, ref T itemQuery, int totalAmountToTake, List<Item> collect, bool childItemsOnly = false) where T : IItemQuery
            {
                var totalAmountTaken = 0;

                for (var i = itemList.Count - 1; i >= 0; i--)
                {
                    var amountToTake = totalAmountToTake - totalAmountTaken;
                    if (amountToTake <= 0)
                        break;

                    var item = itemList[i];
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

                    if (HasSearchableContainer(item, out var childItemList))
                    {
                        totalAmountTaken += TakeItems(childItemList, ref itemQuery, amountToTake, collect);
                    }

                    if (totalAmountTaken >= totalAmountToTake)
                        return totalAmountTaken;
                }

                return totalAmountTaken;
            }

            private static bool IsSearchableItemDefinition(ItemDefinition itemDefinition)
            {
                return (itemDefinition.flags & (ItemDefinition.Flag.Backpack | SearchableItemDefinitionFlag)) != 0;
            }

            private static bool IsSearchableItemDefinition(int itemId)
            {
                var itemDefinition = ItemManager.FindItemDefinition(itemId);
                if ((object)itemDefinition == null)
                    return false;

                return IsSearchableItemDefinition(itemDefinition);
            }

            private static bool HasSearchableContainer(Item item, out List<Item> itemList)
            {
                itemList = item.contents?.itemList;
                if (itemList is not { Count: > 0 })
                    return false;

                if (item.HasFlag(SearchableItemFlag))
                    return true;

                if (item.HasFlag(UnsearchableItemFlag))
                    return false;

                return IsSearchableItemDefinition(item.info);
            }

            private static bool HasSearchableContainer(ProtoBuf.Item itemData, out List<ProtoBuf.Item> itemList)
            {
                itemList = itemData.contents?.contents;
                if (itemList is not { Count: > 0 })
                    return false;

                var itemFlags = (Item.Flag)itemData.flags;
                if (itemFlags.HasFlag(SearchableItemFlag))
                    return true;

                if (itemFlags.HasFlag(UnsearchableItemFlag))
                    return false;

                return IsSearchableItemDefinition(itemData.itemid);
            }
        }

        #endregion

        #region Item Query

        private interface IItemQuery
        {
            int GetUsableAmount(Item item);
            void PopulateItemQuery(Dictionary<string, object> itemQueryDict);
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

            public void PopulateItemQuery(Dictionary<string, object> itemQueryDict)
            {
                itemQueryDict.Clear();
                itemQueryDict[ItemQuery.ItemIdField] = ObjectCache.Get(ItemId);
            }
        }

        private struct ItemQuery : IItemQuery
        {
            public const string BlueprintIdField = "BlueprintId";
            public const string DisplayNameField = "DisplayName";
            public const string DataIntField = "DataInt";
            public const string FlagsContainField = "FlagsContain";
            public const string FlagsEqualField = "FlagsEqual";
            public const string IgnoreItemField = "IgnoreItem";
            public const string ItemDefinitionField = "ItemDefinition";
            public const string ItemIdField = "ItemId";
            public const string MinConditionField = "MinCondition";
            public const string RequireEmptyField = "RequireEmpty";
            public const string SkinIdField = "SkinId";

            public static ItemQuery Parse(Dictionary<string, object> raw)
            {
                var itemQuery = new ItemQuery();

                GetOption(raw, BlueprintIdField, out itemQuery.BlueprintId);
                GetOption(raw, DisplayNameField, out itemQuery.DisplayName);
                GetOption(raw, DataIntField, out itemQuery.DataInt);
                GetOption(raw, FlagsContainField, out itemQuery.FlagsContain);
                GetOption(raw, FlagsEqualField, out itemQuery.FlagsEqual);
                GetOption(raw, IgnoreItemField, out itemQuery.IgnoreItem);
                GetOption(raw, ItemDefinitionField, out itemQuery.ItemDefinition);
                GetOption(raw, ItemIdField, out itemQuery.ItemId);
                GetOption(raw, MinConditionField, out itemQuery.MinCondition);
                GetOption(raw, RequireEmptyField, out itemQuery.RequireEmpty);
                GetOption(raw, SkinIdField, out itemQuery.SkinId);

                return itemQuery;
            }

            private static void GetOption<T>(Dictionary<string, object> dict, string key, out T result)
            {
                result = dict.TryGetValue(key, out var value) && value is T valueOfType
                    ? valueOfType
                    : default;
            }

            public int? BlueprintId;
            public int? DataInt;
            public string DisplayName;
            public Item.Flag? FlagsContain;
            public Item.Flag? FlagsEqual;
            public Item IgnoreItem;
            public ItemDefinition ItemDefinition;
            public int? ItemId;
            public float MinCondition;
            public bool RequireEmpty;
            public ulong? SkinId;

            public int GetUsableAmount(Item item)
            {
                if (IgnoreItem != null && item == IgnoreItem)
                    return 0;

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

                if (!string.IsNullOrEmpty(DisplayName) && !StringUtils.EqualsIgnoreCase(DisplayName, item.name))
                    return 0;

                return RequireEmpty && item.contents?.itemList?.Count > 0
                    ? Math.Max(0, item.amount - 1)
                    : item.amount;
            }

            public void PopulateItemQuery(Dictionary<string, object> itemQueryDict)
            {
                itemQueryDict.Clear();

                if (BlueprintId.HasValue)
                    itemQueryDict[BlueprintIdField] = ObjectCache.Get(BlueprintId.Value);

                if (DataInt.HasValue)
                    itemQueryDict[DataIntField] = ObjectCache.Get(DataInt.Value);

                if (DisplayName != null)
                    itemQueryDict[DisplayNameField] = DisplayName;

                if (FlagsContain.HasValue)
                    itemQueryDict[FlagsContainField] = ObjectCache.Get(FlagsContain.Value);

                if (FlagsEqual.HasValue)
                    itemQueryDict[FlagsEqualField] = ObjectCache.Get(FlagsEqual.Value);

                var itemId = GetItemId();
                if (itemId.HasValue)
                    itemQueryDict[ItemIdField] = ObjectCache.Get(itemId.Value);

                if (MinCondition > 0)
                    itemQueryDict[MinConditionField] = ObjectCache.Get(MinCondition);

                if (RequireEmpty)
                    itemQueryDict[RequireEmptyField] = True;

                if (SkinId.HasValue)
                    itemQueryDict[SkinIdField] = ObjectCache.Get(SkinId.Value);

                if (IgnoreItem != null)
                    itemQueryDict[IgnoreItemField] = IgnoreItem;
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

        #region Item Supplier Manager

        private class ItemSupplier
        {
            public static ItemSupplier FromSpec(Plugin plugin, Dictionary<string, object> spec)
            {
                var supplier = new ItemSupplier { Plugin = plugin };

                GetOption(spec, "Priority", out supplier.Priority);
                GetOption(spec, "FindPlayerItems", out supplier.FindPlayerItems);
                GetOption(spec, "SumPlayerItems", out supplier.SumPlayerItems);
                GetOption(spec, "TakePlayerItems", out supplier.TakePlayerItems);
                GetOption(spec, "TakePlayerItemsV2", out supplier.TakePlayerItemsV2);
                GetOption(spec, "FindPlayerAmmo", out supplier.FindPlayerAmmo);
                GetOption(spec, "SerializeForNetwork", out supplier.SerializeForNetwork);

                return supplier;
            }

            private static void GetOption<T>(Dictionary<string, object> dict, string key, out T result)
            {
                result = dict.TryGetValue(key, out var value) && value is T valueOfType
                    ? valueOfType
                    : default;
            }

            public Plugin Plugin { get; private set; }
            public int Priority;
            public Action<BasePlayer, Dictionary<string, object>, List<Item>> FindPlayerItems;
            public Func<BasePlayer, Dictionary<string, object>, int> SumPlayerItems;
            public Func<BasePlayer, Dictionary<string, object>, int, List<Item>, int> TakePlayerItems;
            public Func<BasePlayer, Dictionary<string, object>, int, List<Item>, ItemCraftTask, int> TakePlayerItemsV2;
            public Action<BasePlayer, AmmoTypes, List<Item>> FindPlayerAmmo;
            public Action<BasePlayer, List<ProtoBuf.Item>> SerializeForNetwork;
        }

        private class SupplierManager
        {
            private static void RemoveSupplier(List<ItemSupplier> supplierList, Plugin plugin)
            {
                for (var i = 0; i < supplierList.Count; i++)
                {
                    var supplier = supplierList[i];
                    if (supplier.Plugin.Name != plugin.Name)
                        continue;

                    supplierList.RemoveAt(i);
                    return;
                }
            }

            private static void SortSupplierList(List<ItemSupplier> supplierList)
            {
                supplierList.Sort((a, b) =>
                {
                    var priorityOrder = a.Priority.CompareTo(b.Priority);
                    if (priorityOrder != 0)
                        return priorityOrder;

                    return string.Compare(a.Plugin.Name, b.Plugin.Name, StringComparison.OrdinalIgnoreCase);
                });
            }

            private CustomItemDefinitionTracker _customItemDefinitionTracker;
            // Use a list with standard for loops for high performance.
            private List<ItemSupplier> _allSuppliers = new();
            private List<ItemSupplier> _beforeInventorySuppliers = new();
            private List<ItemSupplier> _afterInventorySuppliers = new();

            private List<ProtoBuf.Item> _reusableItemListForNetwork = new(32);
            private Dictionary<string, object> _reusableItemQuery = new();

            public SupplierManager(CustomItemDefinitionTracker customItemDefinitionTracker)
            {
                _customItemDefinitionTracker = customItemDefinitionTracker;
            }

            public void AddSupplier(Plugin plugin, Dictionary<string, object> spec)
            {
                RemoveSupplier(plugin);
                var supplier = ItemSupplier.FromSpec(plugin, spec);

                _allSuppliers.Add(supplier);

                if (supplier.Priority < 0)
                {
                    _beforeInventorySuppliers.Add(supplier);
                    SortSupplierList(_beforeInventorySuppliers);
                }
                else
                {
                    _afterInventorySuppliers.Add(supplier);
                    SortSupplierList(_afterInventorySuppliers);
                }
            }

            public void RemoveSupplier(Plugin plugin)
            {
                RemoveSupplier(_allSuppliers, plugin);
                RemoveSupplier(_beforeInventorySuppliers, plugin);
                RemoveSupplier(_afterInventorySuppliers, plugin);
            }

            public int SerializeForNetwork(BasePlayer player, ProtoBuf.ItemContainer containerData, ref int nextInvisibleSlot)
            {
                if (_allSuppliers.Count == 0)
                    return 0;

                _reusableItemListForNetwork.Clear();

                for (var i = 0; i < _allSuppliers.Count; i++)
                {
                    _allSuppliers[i].SerializeForNetwork?.Invoke(player, _reusableItemListForNetwork);
                }

                var itemsAdded = ItemUtils.SerializeForNetwork(_customItemDefinitionTracker, _reusableItemListForNetwork, containerData, ref nextInvisibleSlot);

                _reusableItemListForNetwork.Clear();

                return itemsAdded;
            }

            public void FindPlayerItems<T>(BasePlayer player, ref T itemQuery, List<Item> collect, bool beforeInventory = false) where T : IItemQuery
            {
                var suppliers = beforeInventory ? _beforeInventorySuppliers : _afterInventorySuppliers;
                if (suppliers.Count == 0)
                    return;

                itemQuery.PopulateItemQuery(_reusableItemQuery);

                for (var i = 0; i < suppliers.Count; i++)
                {
                    suppliers[i].FindPlayerItems?.Invoke(player, _reusableItemQuery, collect);
                }
            }

            public int SumPlayerItems<T>(BasePlayer player, ref T itemQuery) where T : IItemQuery
            {
                if (_allSuppliers.Count == 0)
                    return 0;

                itemQuery.PopulateItemQuery(_reusableItemQuery);

                var sum = 0;

                for (var i = 0; i < _allSuppliers.Count; i++)
                {
                    sum += _allSuppliers[i].SumPlayerItems?.Invoke(player, _reusableItemQuery) ?? 0;
                }

                return sum;
            }

            public int TakePlayerItems<T>(BasePlayer player, ref T itemQuery, int amountToTake, List<Item> collect, ItemCraftTask itemCraftTask, bool beforeInventory = false) where T : IItemQuery
            {
                var suppliers = beforeInventory ? _beforeInventorySuppliers : _afterInventorySuppliers;
                if (suppliers.Count == 0)
                    return 0;

                itemQuery.PopulateItemQuery(_reusableItemQuery);

                var amountTaken = 0;

                for (var i = 0; i < suppliers.Count; i++)
                {
                    amountTaken += suppliers[i].TakePlayerItemsV2?.Invoke(player, _reusableItemQuery, amountToTake - amountTaken, collect, itemCraftTask)
                        ?? suppliers[i].TakePlayerItems?.Invoke(player, _reusableItemQuery, amountToTake - amountTaken, collect)
                        ?? 0;

                    if (amountTaken >= amountToTake)
                        return amountTaken;
                }

                return amountTaken;
            }

            public void FindPlayerAmmo(BasePlayer player, AmmoTypes ammoType, List<Item> collect, bool beforeInventory = false)
            {
                var suppliers = beforeInventory ? _beforeInventorySuppliers : _afterInventorySuppliers;
                if (suppliers.Count == 0)
                    return;

                for (var i = 0; i < suppliers.Count; i++)
                {
                    suppliers[i].FindPlayerAmmo?.Invoke(player, ammoType, collect);
                }
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
            private List<ContainerEntry> _containerList = new();

            public void AddContainer(ContainerEntry containerEntry)
            {
                _containerList.Add(containerEntry);
            }

            public void RemoveContainer(ContainerEntry containerEntry)
            {
                if (!_containerList.Remove(containerEntry))
                    return;

                MarkInventoryDirty(containerEntry.Player);

                if (_containerList.Count == 0 && _entity != null && !_entity.IsDestroyed)
                {
                    DestroyImmediate(this);
                }
            }

            private void OnDestroy()
            {
                for (var i = _containerList.Count - 1; i >= 0; i--)
                {
                    _containerManager.RemoveContainer(_containerList[i]);
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
            private Action _handleDirty;

            public bool IsValid => Container?.itemList != null;

            public void Activate()
            {
                var player = Player;
                _handleDirty = () => MarkInventoryDirty(player);
                Container.onDirty += _handleDirty;
            }

            public void Deactivate()
            {
                if (EntityTracker != null)
                {
                    EntityTracker.RemoveContainer(this);
                }

                Container.onDirty -= _handleDirty;
            }

            public bool CanUse()
            {
                return CanUseContainer?.Invoke(Plugin, Player, Container) ?? true;
            }
        }

        private class ContainerManager
        {
            private static bool HasContainer(List<ContainerEntry> containerList, ItemContainer container)
            {
                foreach (var containerEntry in containerList)
                {
                    if (containerEntry.Container == container)
                        return true;
                }

                return false;
            }

            private static bool RemoveEntries(List<ContainerEntry> containerList, Plugin plugin, BasePlayer player)
            {
                var anyRemoved = false;

                for (var i = containerList.Count - 1; i >= 0; i--)
                {
                    var containerEntry = containerList[i];
                    if (containerEntry.Plugin.Name == plugin.Name && containerEntry.Player == player)
                    {
                        containerList.RemoveAt(i);
                        containerEntry.Deactivate();
                        anyRemoved = true;
                    }
                }

                return anyRemoved;
            }

            private static bool RemoveEntry(List<ContainerEntry> containerList, Plugin plugin, BasePlayer player, ItemContainer container)
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

            private CustomItemDefinitionTracker _customItemDefinitionTracker;
            private Dictionary<ulong, List<ContainerEntry>> _playerContainerEntries = new();
            private Dictionary<BaseEntity, EntityTracker> _entityTrackers = new();

            public ContainerManager(CustomItemDefinitionTracker customItemDefinitionTracker)
            {
                _customItemDefinitionTracker = customItemDefinitionTracker;
            }

            public void UnregisterEntity(BaseEntity entity)
            {
                _entityTrackers.Remove(entity);
            }

            public bool HasContainer(BasePlayer player, ItemContainer container)
            {
                var containerList = GetContainerList(player);
                return containerList != null && HasContainer(containerList, container);
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

                containerEntry.Activate();

                var entity = containerEntity as BaseEntity;
                if ((object)entity != null)
                {
                    if (!_entityTrackers.TryGetValue(entity, out var entityTracker))
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
                var containerList = GetContainerList(player);
                if (containerList == null)
                    return false;

                var removed = RemoveEntry(containerList, plugin, player, container);

                if (containerList.Count == 0)
                {
                    _playerContainerEntries.Remove(player.userID);
                }

                return removed;
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
                    anyRemoved |= RemoveEntries(containerList, plugin, player);

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

                foreach (var (player, containerList) in _playerContainerEntries)
                {
                    List<ContainerEntry> removeContainers = null;

                    foreach (var containerEntry in containerList)
                    {
                        if (containerEntry.Plugin == plugin)
                        {
                            removeContainers ??= new List<ContainerEntry>();
                            removeContainers.Add(containerEntry);

                            updatedPlayers ??= new HashSet<BasePlayer>();
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
                        removePlayerIds ??= new List<ulong>();
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
                return _playerContainerEntries.GetValueOrDefault(player.userID);
            }

            public int SerializeForNetwork(BasePlayer player, ProtoBuf.ItemContainer containerData, ref int nextInvisibleSlot)
            {
                var containerList = GetContainerList(player);
                if (containerList == null)
                    return 0;

                var itemsAdded = 0;

                for (var i = 0; i < containerList.Count; i++)
                {
                    var containerEntry = containerList[i];
                    if (!containerEntry.IsValid || !containerEntry.CanUse())
                        continue;

                    itemsAdded += ItemUtils.SerializeForNetwork(_customItemDefinitionTracker, containerEntry.Container.itemList, containerData, ref nextInvisibleSlot);
                }

                return itemsAdded;
            }

            public void FindPlayerItems<T>(BasePlayer player, ref T itemQuery, List<Item> collect) where T : IItemQuery
            {
                var containerList = GetContainerList(player);
                if (containerList == null)
                    return;

                for (var i = 0; i < containerList.Count; i++)
                {
                    var containerEntry = containerList[i];
                    if (!containerEntry.IsValid || !containerEntry.CanUse())
                        continue;

                    ItemUtils.FindItems(containerEntry.Container.itemList, ref itemQuery, collect);
                }
            }

            public int SumPlayerItems<T>(BasePlayer player, ref T itemQuery) where T : IItemQuery
            {
                var containerList = GetContainerList(player);
                if (containerList == null)
                    return 0;

                var sum = 0;

                for (var i = 0; i < containerList.Count; i++)
                {
                    var containerEntry = containerList[i];
                    if (!containerEntry.IsValid || !containerEntry.CanUse())
                        continue;

                    sum += ItemUtils.SumItems(containerEntry.Container.itemList, ref itemQuery);
                }

                return sum;
            }

            public int TakePlayerItems<T>(BasePlayer player, ref T itemQuery, int amountToTake, List<Item> collect) where T : IItemQuery
            {
                var containerList = GetContainerList(player);
                if (containerList == null)
                    return 0;

                var amountTaken = 0;

                for (var i = 0; i < containerList.Count; i++)
                {
                    var containerEntry = containerList[i];
                    if (!containerEntry.IsValid || !containerEntry.CanUse())
                        continue;

                    amountTaken += ItemUtils.TakeItems(containerEntry.Container.itemList, ref itemQuery, amountToTake - amountTaken, collect);

                    if (amountTaken >= amountToTake)
                        return amountTaken;
                }

                return amountTaken;
            }

            public void FindPlayerAmmo(BasePlayer player, AmmoTypes ammoType, List<Item> collect)
            {
                var containerList = GetContainerList(player);
                if (containerList == null)
                    return;

                for (var i = 0; i < containerList.Count; i++)
                {
                    var containerEntry = containerList[i];
                    if (!containerEntry.CanUse())
                        continue;

                    containerEntry.Container.FindAmmo(collect, ammoType);
                }
            }
        }

        #endregion
    }
}
