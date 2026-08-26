# Contracts

Managed by a dedicated agent. See docs/CONTRACTS_AGENT.md.

All interfaces for the ShardECS ecosystem live here.
Zero dependencies - no NuGet packages, no project references.
Every other project references Contracts. Nothing references back.

## Interface inventory

### Core ECS

- Entities/IEntity.cs             : Bare entity identity (int Id)
- Components/IComponent.cs        : Component marker
- Components/IComponentStore.cs   : Add, get, remove, query components (int-based)
- Drawers/IDrawer.cs              : System logic unit (Execute per tick)
- Dressers/IDresser.cs            : Ordered drawer group

### Events

- Events/IEvent.cs                : Event marker interface
- Events/SecsEvent.cs             : **Base class** for all ShardECS events — extend this, not IEvent directly
- Events/IEventBus.cs             : Pub/sub event bus (Publish, Subscribe)
- Events/IComponentTracker.cs     : Reactive callbacks for component mutations (OnAdded/OnRemoved/OnChanged)

### Identity / Tags

- Systems/IIdentityTag.cs         : Marker for zero-data entity classification tags

### Buffers

- Buffers/IMerger.cs              : Interface for combining delta values (Identity, Merge, Apply)
- Buffers/SecsMerger.cs           : **Base class** for all mergers — extend this, not IMerger directly
- Buffers/IBufferedStore.cs       : Typed read/write handle for buffered component writes

### Debug

- Debug/ISecsDebugger.cs          : Rate-limited debug logger (Log, LogEntity, LogOnce, channels)

### Entities

- Entities/IEntityFactory.cs      : Archetype factory interface (Create, CreateMany)
- Entities/SecsFactory.cs         : **Base class** for all factories — extend this, not IEntityFactory directly
                                    Provides a default CreateMany implementation

### Runtime

- Runtime/ISecs.cs                : Full ShardECS runtime facade — the main contract all engine
                                    bindings, test harnesses, and authored shards program against

### Shards / Store

- Shards/IShard.cs                : Shard metadata (name, version, kind, tags, source)

### Engine Bindings

- Bindings/IEngineBinding.cs      : Engine binding contract (Initialize, Tick, WrapEntity)

## Inheritance convention

| Your type is a... | Extend / implement |
|-------------------|--------------------|
| Event             | `SecsEvent`        |
| Entity factory    | `SecsFactory`      |
| Merger            | `SecsMerger<T>`    |
| Identity tag      | `IIdentityTag`     |
| Drawer            | `DrawerBase` (SECS)|
| Component         | `IComponent` or `BaseComponent` (SECS) |

## Rules

- No implementation code (except default method bodies in base classes SecsEvent/SecsFactory/SecsMerger)
- No NuGet packages
- Breaking changes require user confirmation
