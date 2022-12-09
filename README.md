## Features

- Allows players to build, craft, reload and more using items from external containers
- Supports building, upgrading, crafting, repairing, reloading, door keys, vending machine purchases, vendor purchases, and tech tree research
- Supports many other plugins that call vanilla functions to count and take items
- API allows other plugins to add and remove containers for players
- Demonstration commands allow adding and removing specific containers or backpack containers

## How it works

When a plugin registers a container with a player, the player will immediately be able to use all items available inside of it. Vanilla UIs that display resource counts, such as the crafting menu, will reflect the total amount of items the player has across their inventory and registered containers.

## Incompatible plugins

Any plugin which reduces the player inventory space to less than 24 is not compatible.

## Permissions

- `itemretriever.admin` -- Allows using the demonstration commands documented below.

## Commands

- `retriever.add` -- Adds the container you are looking at to your registered containers.
- `retriever.remove` -- Removes the container you are looking at from your registered containers.
- `retriever.backpack.add` -- Adds your backpack to your registered containers.
- `retriever.backpack.remove` -- Removes your backpack from your registered containers.

## How developers should integrate with this plugin

1. When your plugin loads, determine which players should have which containers available to them, and call the `API_AddContainer` to register those containers
2. When Item Retriever reloads, run the same logic as step 1
3. When a player connects/spawns/awakens/etc. and should have containers available to them (such as their backpack container), call `API_AddContainer` to register those containers

If you want to allow other plugins to dynamically disable player access to containers that your plugin provides for players (e.g., you are a backpack plugin, and an event/arena plugin wants to disable backpack access while in the event/arena), then you should do the following.

1. Maintain internal plugin state that dictates which containers should be accessible
2. When calling `API_AddContainer`, pass the `canUseContainer` callback which should access the above state
3. Expose API methods to allow other plugins to toggle container access for a given player

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
    public Action<Plugin, BasePlayer, IItemContainerEntity, ItemContainer, Func<Plugin, BasePlayer, ItemContainer, bool>> AddContainer { get; }
    public Action<Plugin, BasePlayer, ItemContainer> RemoveContainer { get; }
    public Action<Plugin, BasePlayer> RemoveAllContainersForPlayer { get; }
    public Action<Plugin> RemoveAllContainersForPlugin { get; }
    public Action<BasePlayer, List<Item>, Func<Item, int>> FindPlayerItems { get; }
    public Func<BasePlayer, Func<Item, int>, int> SumPlayerItems { get; }
    public Func<BasePlayer, List<Item>, Func<Item, int>, int, int> TakePlayerItems { get; }
    public Action<BasePlayer, List<Item>, AmmoTypes> FindPlayerAmmo { get; }

    public ItemRetrieverApi(Dictionary<string, object> apiDict)
    {
        AddContainer = apiDict[nameof(AddContainer)] as Action<Plugin, BasePlayer, IItemContainerEntity, ItemContainer, Func<Plugin, BasePlayer, ItemContainer, bool>;
        RemoveContainer = apiDict[nameof(RemoveContainer)] as Action<Plugin, BasePlayer, ItemContainer>;
        RemoveAllContainersForPlayer = apiDict[nameof(RemoveAllContainersForPlayer)] as Action<Plugin, BasePlayer>;
        RemoveAllContainersForPlugin = apiDict[nameof(RemoveAllContainersForPlugin)] as Action<Plugin>;
        FindPlayerItems = apiDict[nameof(FindPlayerItems)] as Action<BasePlayer, List<Item>, Func<Item, int>>;
        SumPlayerItems = apiDict[nameof(SumPlayerItems)] as Func<BasePlayer, Func<Item, int>, int>;
        TakePlayerItems = apiDict[nameof(TakePlayerItems)] as Func<BasePlayer, List<Item>, Func<Item, int>, int, int>;
        FindPlayerAmmo = apiDict[nameof(FindPlayerAmmo)] as Action<BasePlayer, List<Item>, AmmoTypes>;
    }
}

// (Helper class) This abstraction allows you to reuse a delegate to avoid generating garbage.
private static class UsableItemCounter
{
    // These represent the parameters of your item query.
    private static int _itemId;
    private static ulong _skinId;

    private static Func<Item, int> _getUsableAmount = item =>
    {
        if (item.info.itemid != _itemId)
            return 0;

        if (_skinId != 0 && item.skin != _skinId)
            return 0;

        // If an item has contents (e.g., weapon attachment), don't consume the whole stack.
        if (item.contents?.itemList.Count > 0)
        {
            return Math.Max(0, item.amount - 1);
        }

        return item.amount;
    };

    // This will update the query parameters and return the cached delegate.
    public static Func<Item, bool> Get(int itemId, ulong skinId = 0)
    {
        _itemId = itemId;
        _skinId = skinId;
        return _getUsableAmount;
    }
}

// (Helper method) When ItemRetriever's API is not available, you'll need a method to find items in the player inventory.
// If you only need to find items by id (don't need to check skin, blueprint, etc.), then you can use a vanilla method.
private int SumContainerItems(ItemContainer container, Func<Item, int> getUsableAmount)
{
    var sum = 0;

    foreach (var item in container.itemList)
    {
        sum += getUsableAmount(item);
    }

    return sum;
}

// (Helper method) Example business logic.
private int GetPlayerEpicScrapAmount(BasePlayer player)
{
    var getUsableAmount = UsableItemCounter.Get(-932201673, 1234567890);

    // If ItemRetriever is available, call it, else simply find items in the player inventory.
    return _itemRetrieverApi?.SumPlayerItems?.Invoke(player, getUsableAmount)
        ?? SumContainerItems(player.inventory.containerMain, getUsableAmount)
            + SumContainerItems(player.inventory.containerBelt, getUsableAmount);
}
```

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

#### FindPlayerItems

```cs
void FindPlayerItems(BasePlayer player, List<Item> collect, Func<Item, int> getUsableAmount)
```

Searches the player inventory and extra containers, adding all items to the `collect` list for which the `getUsableAmount` delegate returns greater than `0`.

#### API_SumPlayerItems

```cs
int API_SumPlayerItems(BasePlayer player, Func<Item, int> getUsableAmount)
```

Searches the player inventory and extra containers, calling `getUsableAmount` for all items and returning the sum. This searches the player inventory and extra containers. Note: If `getUsableAmount` returns a negative value, that value is **not** added to the sum.

#### API_TakePlayerItems

```cs
int API_TakePlayerItems(BasePlayer player, List<Item> collect, Func<Item, int> getUsableAmount, int amount)
```

Searches the player inventory and extra containers, taking `amount` of items for which the `getUsableAmount` delegate returns greater than `0`, optionally adding those items to the `collect` list if non-null. This searches the player inventory and extra containers. 

#### API_FindPlayerAmmo

```cs
void API_FindPlayerAmmo(BasePlayer player, List<Item> collect, AmmoTypes ammoType)
```

Searches the player inventory and extra containers, adding all items to the `collect` list that match the specified `ammoType`.
