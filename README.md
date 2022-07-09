## Features

- Allows players to build, craft, reload and more using items from external containers
- Supports building, upgrading, crafting, repairing, reloading, car keys, door keys, vending machine purchases, vendor purchases, and tech tree research
- Supports many other plugins that call vanilla functions to count and take items
- API allows other plugins to add and remove containers for players
- Demonstration commands allow adding and removing specific containers or backpack containers

## How it works

When a plugin registers a container with a player, the player will immediately be able to use all items available inside of it. Vanilla UIs that display resource counts, such as the crafting menu, will reflect the total amount of items the player has across their inventory and registered containers.

## Incompatible plugins

Any plugin which reduces the player inventory space to less than 24 is not compatible.

## Permissions

- `itemsourcer.admin` -- Allows using the demonstration commands documented below.

## Commands

- `source.add` -- Adds the container you are looking at.
- `source.remove` -- Removes the container you are looking at.
- `source.backpack.add` -- Adds your backpack container.
- `source.backpack.remove` -- Removes your backpack container.

## Developer API

#### API_AddContainer

```cs
API_AddContainer(Plugin plugin, BasePlayer player, IItemContainerEntity containerEntity, ItemContainer container, Func<Plugin, BasePlayer, ItemContainer, bool> canUseContainer = null)
```

- Adds the specified container to the specified player, under the specified plugin
- When the container entity is provided, the container association will automatically be cleaned up when that entity is destroyed
- If the `canUseContainer` delegate is provided, the plugin will call it each time it wants to potentially use a container, to evaluate whether items may be pulled from that container

#### API_RemoveContainer

```cs
void API_RemoveContainer(Plugin plugin, BasePlayer player, ItemContainer container)
```

Removes a container that was added to a player by calling `API_AddContainer` with the same arguments.

#### API_RemoveContainers

```cs
void API_RemoveContainers(Plugin plugin, BasePlayer player)
```

Removes all containers registered by the specified plugin for the specified player.

```cs
void API_RemoveContainers(Plugin plugin)
```

Removes all containers registered by the specified plugin.

#### API_FindItems

```cs
void API_FindItems(BasePlayer player, List<Item> collect, int itemId, ulong skinId = 0)
```

- Finds all items matching `itemId` and `skinId`, by searching the player inventory and extra containers, then adds them to `collect`
- If `skinId` is `0`, any skin will be considered a match

#### API_SumPlayerItems

```cs
int API_SumPlayerItems(BasePlayer player, int itemId, ulong skinId = 0)
```

- Sums all items matching `itemId` and `skinId`, by searching the player inventory and extra containers
- If `skinId` is `0`, any skin will be considered a match

#### API_TakePlayerItems

```cs
int API_TakePlayerItems(BasePlayer player, List<Item> collect, int itemId, int amount, ulong skinId = 0)
```

- Takes `amount` of items matching `itemId` and `skinId` from the player inventory and extra containers, optionally adding them to the list if provided
- If `skinId` is `0`, any skin will be considered a match

#### API_FindPlayerAmmo

```cs
void API_FindPlayerAmmo(BasePlayer player, List<Item> collect, AmmoTypes ammoType)
```

- Finds all ammo of `ammoType`, by searching the player inventory and extra containers, then adds them to `collect`
