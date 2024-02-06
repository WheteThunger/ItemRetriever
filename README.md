## Features

- Allows players to build, craft, reload and more using resources from external sources, including vanilla backpacks
- Supports building, upgrading, crafting, repairing, reloading, opening key locks, purchasing from vending machines, purchasing from vehicle vendors, and unlocking blueprints via the tech tree
- Supports many plugins that call vanilla functions to find and take resources
- Functions as a router between plugins that consume resources and plugins that supply resources

## How it works

If you install this plugin by itself, it will allow players to build/craft/etc. using resources from their equipped _vanilla_ backpack. This capability does not require any permissions or configuration.

This plugin becomes more useful when you have other plugins that are compatible with it, including the following examples.

- When the [Backpacks](https://umod.org/plugins/backpacks) plugin is installed, players will be able to build/craft/etc. using resources from inside their backpacks, if they have the `backpacks.retrieve` permission and if they have toggled on Retrieve mode (which is a per-backpack-page setting).
- When the Bag of Holding plugin is installed, players will be able to build/craft/etc. using the resources inside their bags.
- When the [Virtual Items](https://umod.org/plugins/virtual-items) plugin is installed, players will be able to build/craft/etc. using no resources at all, according to the configuration and permissions in that plugin.

### Concepts

- **Item Consumers** -- Any plugin or vanilla function that takes or deletes items from player inventories.
  - Example vanilla functions: Building, upgrading, crafting, reloading, opening key locks, purchasing from vending machines, purchasing from vehicle vendors, and unlocking blueprints via the tech tree.
  - Example plugins: [Custom Vending Setup](https://umod.org/plugins/custom-vending-setup)
- **Item Suppliers** -- Any plugin that hooks into Item Retriever to provide items on-demand for Item Consumers. Allows loading or creating items on-demand.
  - Example plugins: [Backpacks](https://umod.org/plugins/backpacks), [Virtual Items](https://umod.org/plugins/virtual-items)
- **Container Suppliers** -- Any plugin that registers containers with Item Retriever. Item Retriever will search those containers on-demand on behalf of Item Consumers. For example, a plugin could add a UI button to storage containers, allowing players to individually toggle whether they can remotely utilize the contents of those containers.
  - Example plugins: None at this time.

In addition to _explicit_ Item Suppliers and Container Suppliers, some items may _implicitly_ function as Item Suppliers, including vanilla backpacks, and bags managed by the Bag of Holding plugin.

## Incompatible plugins

Any plugin which reduces the player inventory space to less than 24 is not compatible. For example, Clothing Slots.

## How developers should integrate with this plugin

### Item Consumers

If your plugin needs to take items from player inventories and only cares about item ids, then simply utilize vanilla Rust methods from the `PlayerInventory` class and that will call hooks that Item Retriever already intercepts.

If your plugin needs to take items from player inventories, and you care about more than just item ids, then do the following.

- While Item Retriever is loaded, sum and take player items via `API_SumPlayerItems` and `API_TakePlayerItems`. Do not search for items yourself.
- While Item Retriever is not loaded, implement custom logic to sum and take those items.

### Item Suppliers

If your plugin externally stores items for players, such as in data files, especially if that data is lazily loaded, then you probably want to be an Item Supplier. Use `API_AddSupplier` to register callbacks with Item Retriever, which will be called on-demand when Item Consumers want to sum or take items. Your callbacks will be passed the player that the items are for, plus a query describing the items for which to search.

In some cases, you may not need to create the actual `Item` instances.

- When summing items, you can simply enumerate the contents of a data file and return the result. If the items don't exist anywhere, such as free items, then you can simply return whatever sum you want.
- When taking items, if the `collect` list is `null`, that means another plugin simply wanted to delete the items, so you can simply update a data layer which represents the items, without creating any `Item` instances. If the items don't exist anywhere, such as free items, then you can simply create new items.

### Container Suppliers

If your plugin deals exclusively with containers that reside in the physical world of Rust (no external data), you probably want to become a Container Supplier. Use `API_AddContainer` and `API_RemoveContainer` to associate and disassociate specific containers with specific players. Item Retriever will search those containers on-demand when Item Consumers want to sum or take items.

## Developer API

#### API_GetApi

```cs
Dictionary<string, object> API_GetApi()
```

Returns a dictionary of delegates for high-performance API operations. Interacting with delegates avoids garbage allocations and Oxide overhead.

Example:

```cs
[PluginReference]
private readonly Plugin ItemRetriever;

private ItemRetrieverApi _itemRetrieverApi;
private Dictionary<string, object> _itemQuery;

// (Hook) When all plugins load, call ItemRetriever to cache its API.
private void OnServerInitialized()
{
    if (ItemRetriever != null)
    {
        CacheItemRetrieverApi();
    }
}

// (Hook) In case ItemRetriever reloads or loads late, refresh its API.
private void OnPluginLoaded(Plugin plugin)
{
    if (plugin.Name == nameof(ItemRetriever))
    {
        CacheItemRetrieverApi();
    }
}

// (Helper method) Call ItemRetriever via Oxide to get its API.
private void CacheItemRetrieverApi()
{
    _itemRetrieverApi = new ItemRetrieverApi(ItemRetriever.Call("API_GetApi") as Dictionary<string, object>);
}

// (Helper class) This abstraction allows you to call ItemRetriever API methods with low CPU/memory overhead.
private class ItemRetrieverApi
{
    // All available API methods are defined here, but you can shorten this list for brevity if you only use select APIs.
    public Action<Plugin, Dictionary<string, object>> AddSupplier { get; }
    public Action<Plugin> RemoveSupplier { get; }
    public Func<BasePlayer, ItemContainer, bool> HasContainer { get; }
    public Action<Plugin, BasePlayer, IItemContainerEntity, ItemContainer, Func<Plugin, BasePlayer, ItemContainer, bool>> AddContainer { get; }
    public Action<Plugin, BasePlayer, ItemContainer> RemoveContainer { get; }
    public Action<Plugin, BasePlayer> RemoveAllContainersForPlayer { get; }
    public Action<Plugin> RemoveAllContainersForPlugin { get; }
    public Action<BasePlayer, Dictionary<string, object>, List<Item>> FindPlayerItems { get; }
    public Func<BasePlayer, Dictionary<string, object>, int> SumPlayerItems { get; }
    public Func<BasePlayer, Dictionary<string, object>, int, List<Item>, int> TakePlayerItems { get; }
    public Action<BasePlayer, AmmoTypes, List<Item>> FindPlayerAmmo { get; }

    public ItemRetrieverApi(Dictionary<string, object> apiDict)
    {
        AddSupplier = apiDict[nameof(AddSupplier)] as Action<Plugin, Dictionary<string, object>>;
        RemoveSupplier = apiDict[nameof(RemoveSupplier)] as Action<Plugin>;
        HasContainer = apiDict[nameof(HasContainer)] as Func<BasePlayer, ItemContainer, bool>;
        AddContainer = apiDict[nameof(AddContainer)] as Action<Plugin, BasePlayer, IItemContainerEntity, ItemContainer, Func<Plugin, BasePlayer, ItemContainer, bool>;
        RemoveContainer = apiDict[nameof(RemoveContainer)] as Action<Plugin, BasePlayer, ItemContainer>;
        RemoveAllContainersForPlayer = apiDict[nameof(RemoveAllContainersForPlayer)] as Action<Plugin, BasePlayer>;
        RemoveAllContainersForPlugin = apiDict[nameof(RemoveAllContainersForPlugin)] as Action<Plugin>;
        FindPlayerItems = apiDict[nameof(FindPlayerItems)] as Action<BasePlayer, Dictionary<string, object>, List<Item>>;
        SumPlayerItems = apiDict[nameof(SumPlayerItems)] as Func<BasePlayer, Dictionary<string, object>, int>;
        TakePlayerItems = apiDict[nameof(TakePlayerItems)] as Func<BasePlayer, Dictionary<string, object>, int, List<Item>, int>;
        FindPlayerAmmo = apiDict[nameof(FindPlayerAmmo)] as Action<BasePlayer, AmmoTypes, List<Item>>;
    }
}

// (Helper method) When ItemRetriever's API is not available, you'll need a method to find items in the player inventory.
// If you only need to find items by id (don't need to check skin, blueprint, etc.), then you can use a vanilla method.
private int SumContainerItems(ItemContainer container, int itemId, ulong skinId = 0)
{
    var sum = 0;

    foreach (var item in container.itemList)
    {
        if (item.info.itemid != itemId)
            continue;

        if (skinId != 0 && item.skin != skinId)
            continue;

        sum += item.amount;
    }

    return sum;
}

// (Helper method) Create or update your item query. Reuses the dictionary to reduce garbage generation.
// Recommended to also use an object cache for item ids and skin ids to avoid generating garbage when assigning them to the item query dictionary.
private Dictionary<string, object> SetupItemQuery(int itemId, ulong skinId = 0)
{
    if (_itemQuery == null)
    {
        _itemQuery = new Dictionary<string, object>();
    }

    _itemQuery.Clear();
    _itemQuery["ItemId"] = itemId;

    if (skinId != 0)
    {
        _itemQuery["SkinId"] = skinId;
    }

    return _itemQuery;
}

// (Helper method) Example business logic.
private int GetPlayerEpicScrapAmount(BasePlayer player)
{
    var itemId = -932201673;
    var skinId = 1234567890uL;

    // If ItemRetriever is available, call it, else simply find items in the player inventory.
    return _itemRetrieverApi?.SumPlayerItems?.Invoke(player, SetupItemQuery(itemId, skinId))
        ?? SumContainerItems(player.inventory.containerMain, itemId, skinId)
            + SumContainerItems(player.inventory.containerBelt, itemId, skinId);
}
```

#### API_AddSupplier

```cs
void API_AddSupplier(Plugin plugin, Dictionary<string, object> spec)
```

Registers a plugin as an Item Supplier. A Supplier is basically a set of hooks that may be called by Item Retriever in order to find, sum or take items for players.

Supported fields (all optional):

- `"Priority"` -- Determines the priority of the Supplier with respect to other suppliers. Lower numbers are higher priority. Negative numbers are processed before the player inventory is searched. If two Suppliers have equal priority, they will be processed alphabetically according to their plugin name. Default priority is `0`.
- `"FindPlayerItems"`: `Action<BasePlayer, Dictionary<string, object>, List<Item>>` -- This will be called when a plugin or a vanilla function wants to obtain a list of specific items for a specific player. If you are implementing a plugin which gives players free items that are created on-demand, do not implement this function, since there is no guarantee that the requester will use the items, so implementing this would likely result in items being leaked.
- `"SumPlayerItems"`: `Func<BasePlayer, Dictionary<string, object>, int>` -- This will be called when a plugin or a vanilla function wants to know if a specific player has sufficient quantity of a specific item.
- `"TakePlayerItems"`: `Func<BasePlayer, Dictionary<string, object>, int, List<Item>, int>` -- This will be called when a plugin or a vanilla function has already determined that a specific player has sufficient quantity of a specific item, and now wants to take those items.
- `"TakePlayerItemsV2"`: `Func<BasePlayer, Dictionary<string, object>, int, List<Item>, ItemCraftTask, int>` -- Like `TakePlayerItems`, but passes the `ItemCraftTask` argument. If the `ItemCraftTask` argument is non-null, then the items are being requested for a craft task. This is useful information for plugins that generate items, such as Virtual Items, as it allows them to simply return the ingredient amount rather than actually generate/collect the ingredients so that the ingredients won't be refunded if the craft task is canceled.
- `"FindPlayerAmmo"`: `Action<BasePlayer, AmmoTypes, List<Item>>` -- This will be called when a plugin or a vanilla function wants to locate all ammo items matching a specific type for a specific player.
- `"SerializeForNetwork"`: `Action<BasePlayer, List<ProtoBuf.Item>>` -- This will be called when a plugin or a vanilla function wants to send a snapshot of the player's inventory to them. When you implement this hook and add items to the provided list, those items will be included in the snapshot sent to the player, causing that player's game client to think it has those items, even though they are not visible in the inventory.

Example to supply unlimited wood:

```cs
private const int WoodItemId = -151838493;
private const int WoodAmount = 1000000;

[PluginReference]
private readonly Plugin ItemRetriever;

private void OnServerInitialized()
{
    AddSupplier();
}

private void OnPluginLoaded(Plugin plugin)
{
    if (plugin.Name == nameof(ItemRetriever))
    {
        AddSupplier();
    }
}

private void AddSupplier()
{
    ItemRetriever?.Call("API_AddSupplier", this, new Dictionary<string, object>
    {
        ["SumPlayerItems"] = new Func<BasePlayer, Dictionary<string, object>, int>((player, rawItemQuery) =>
        {
            object itemIdObj;
            if (!rawItemQuery.TryGetValue("ItemId", out itemIdObj))
                return 0;

            var itemId = Convert.ToInt32(itemIdObj);
            if (itemId != WoodItemId)
                return 0;

            return WoodAmount;
        }),

        ["TakePlayerItems"] = new Func<BasePlayer, Dictionary<string, object>, int, List<Item>, int>((player, rawItemQuery, amount, collect) =>
        {
            object itemIdObj;
            if (!rawItemQuery.TryGetValue("ItemId", out itemIdObj))
                return 0;

            var itemId = Convert.ToInt32(itemIdObj);
            if (itemId != WoodItemId)
                return 0;

            collect?.Add(ItemManager.CreateByItemID(WoodItemId, amount));

            // Return the full amount, so ItemRetriever will stop looking for more items.
            return amount;
        }),

        ["SerializeForNetwork"] = new Action<BasePlayer, List<ProtoBuf.Item>>((player, itemList) =>
        {
            // Make the client think it has an additional wood.
            var itemData = Facepunch.Pool.Get<ProtoBuf.Item>();
            itemData.itemid = WoodItemId;
            itemData.amount = WoodAmount;
            itemList.Add(itemData);
        }),
    });
}
```

#### API_RemoveSupplier

```cs
void API_RemoveSupplier(Plugin plugin)
```

Removes the specified item supplier. Once a supplier has been removed, players will no longer be able to access that supplier's items. Existing players may temporarily have stale inventory snapshots that indicate those items are still available, but that will automatically resolve itself when the player's inventory next changes.

Note: It's not necessary to call this when your plugin unloads because Item Retriever will detect your plugin unloading and will unregister it automatically.

#### API_HasContainer

```cs
bool API_HasContainer(BasePlayer player, ItemContainer container)
```

Returns `true` if the specified player has the specified container associated with them, else returns `false`.

#### API_AddContainer

```cs
void API_AddContainer(Plugin plugin, BasePlayer player, IItemContainerEntity containerEntity, ItemContainer container, Func<Plugin, BasePlayer, ItemContainer, bool> canUseContainer = null)
```

- Adds the specified container to the specified player, under the specified plugin
- When the container entity is provided, the container association will automatically be cleaned up when that entity is destroyed, so that you don't have to call `API_RemoveContainer`
- If the `canUseContainer` delegate is provided, the plugin will call it each time it wants to use a container, to evaluate whether items may be counted or pulled from that container

#### API_RemoveContainer

```cs
void API_RemoveContainer(Plugin plugin, BasePlayer player, ItemContainer container)
```

Removes the specified container from the target player.

#### API_RemoveAllContainersForPlayer

```cs
void API_RemoveAllContainersForPlayer(Plugin plugin, BasePlayer player, ItemContainer container)
```

Removes all containers registered by the specified plugin for the target player.

#### API_RemoveAllContainersForPlugin

```cs
void API_RemoveAllContainersForPlugin(Plugin plugin)
```

Removes all containers registered by the specified plugin. Note: Plugins **don't** need to call this when they unload because Item Retriever already watches for plugin unload events in order to automatically unregister their containers.

#### API_FindPlayerItems

```cs
void API_FindPlayerItems(BasePlayer player, Dictionary<string, object> itemQuery, List<Item> collect)
```

Searches the player inventory and extra containers, adding all items to the `collect` list for which the `itemQuery` matches.

#### API_SumPlayerItems

```cs
int API_SumPlayerItems(BasePlayer player, Dictionary<string, object> itemQuery)
```

Searches the player inventory and extra containers, returning the sum of all items for which the `itemQuery` matches.

#### API_TakePlayerItems

```cs
int API_TakePlayerItems(BasePlayer player, Dictionary<string, object> itemQuery, int amount, List<Item> collect)
```

Searches the player inventory and extra containers, taking `amount` of items for which the `itemQuery` matches, optionally adding those items to the `collect` list if non-null. 

#### API_FindPlayerAmmo

```cs
void API_FindPlayerAmmo(BasePlayer player, AmmoTypes ammoType, List<Item> collect)
```

Searches the player inventory and extra containers, adding all items to the `collect` list that match the specified `ammoType`.

#### Item queries

The `API_FindPlayerItems`, `API_SumPlayerItems` and `API_TakePlayerItems` APIs all accept a `Dictionary<string, object>` item query with the following optional fields. Additionally, the `API_AddSupplier` method will provide a dictionary with the same fields when calling supplier hooks.

- `"BlueprintId"`: `int` -- Corresponds to `item.blueprintTarget`.
- `"DisplayName"`: `string` -- Corresponds to `item.name`.
- `"DataInt"`: `int` -- Corresponds to `item.instanceData.dataInt`.
- `"FlagsContain"`: `Item.Flag` -- Corresponds to `item.flags`. Items may be considered a match even if their `item.flags` bit mask contains other flags.
- `"FlagsEqual"`: `Item.Flag` -- Corresponds to `item.flags`. Items will not be considered a match if their `item.flags` bit mask contains other flags.
- `"ItemId"`: `int` -- Corresponds to `item.info.itemid`.
- `"MinCondition"`: `float` -- Corresponds to `item.conditionNormalized`.
- `"RequireEmpty"`: `bool` -- Corresponds to `item.contents.itemList.Count`. While `true`, items with contents (e.g., weapons with attachments) will not match. If an item with contents is stacked, all the items in the stack except one may be considered a match.
- `"SkinId"`: `ulong` -- Corresponds to `item.skin`.

Caution: Don't supply fields that are not required for a match. For example, if you supply SkinId `0`, then only items with SkinId `0` will be considered a match. 

If none of the fields are provided, all items will be considered a match.

Example code to abstract away dictionary access:

```cs
private struct ItemQuery
{
    public static ItemQuery Parse(Dictionary<string, object> raw)
    {
        var itemQuery = new ItemQuery();

        GetOption(raw, "BlueprintId", out itemQuery.BlueprintId);
        GetOption(raw, "DisplayName", out itemQuery.DisplayName);
        GetOption(raw, "DataInt", out itemQuery.DataInt);
        GetOption(raw, "FlagsContain", out itemQuery.FlagsContain);
        GetOption(raw, "FlagsEqual", out itemQuery.FlagsEqual);
        GetOption(raw, "ItemId", out itemQuery.ItemId);
        GetOption(raw, "MinCondition", out itemQuery.MinCondition);
        GetOption(raw, "RequireEmpty", out itemQuery.RequireEmpty);
        GetOption(raw, "SkinId", out itemQuery.SkinId);

        return itemQuery;
    }

    private static void GetOption<T>(Dictionary<string, object> dict, string key, out T result)
    {
        object value;
        result = dict.TryGetValue(key, out value) && value is T
            ? (T)value
            : default(T);
    }

    public int? BlueprintId;
    public int? DataInt;
    public string DisplayName;
    public Item.Flag? FlagsContain;
    public Item.Flag? FlagsEqual;
    public int? ItemId;
    public float MinCondition;
    public bool RequireEmpty;
    public ulong? SkinId;
}
```

## Developer Hooks

### OnIngredientsDetermine

```cs
void OnIngredientsDetermine(Dictionary<int, int> overridenIngredients, ItemBlueprint blueprint, int amount, BasePlayer player)
```

Called when the `CanCraft` and `OnIngredientsCollect` hooks are called. Other plugins can use this to alter the crafting recipe. After calling this hook, if the `overridenIngredients` dictionary is non-empty (`overridenIngredients.Count` > 0), Item Retriever will use the ingredients defined therein (for `CanCraft`, it will count the ingredients; for `OnIngredientsCollect`, it will collect the ingredients). The dictionary keys are the item ids of the ingredients, and the dictionary values are the total amounts of those ingredients required (base amount times craft amount). Multiple other plugins can use this hook at the same time, in order to apply multiple independent discounts.

How to use this hook:

- In the `CanCraft` and `OnIngredientsCollect`, return `null` when ItemRetriever is loaded.
- In the `OnIngredientsDetermine` hook, if the dictionary is non-empty, that means another plugin is using this hook and altered the ingredients, so simply apply changes to the ingredient values already present. If necessary, you can add new ingredients, but that is not recommended because plugins that already handled this hook will not get an opportunity to change the values.
- In the `OnIngredientsDetermine` hook, if the dictionary is empty, populate it with your preferred ingredients, which are ideally the vanilla blueprint ingredients with altered amounts. Make sure to provide the totals, with craft amount factored in.
