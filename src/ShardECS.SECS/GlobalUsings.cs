// ═══════════════════════════════════════════════════════════════════════════════
//  GLOBAL USINGS — re-exported for end-user convenience.
//
//  Add a reference to ShardECS.SECS and these namespaces are available everywhere
//  in your project without any explicit using statements.
//
//  If you prefer explicit usings, delete this file — everything still works.
// ═══════════════════════════════════════════════════════════════════════════════

// Runtime entry point
global using ShardECS.SECS;

// Contracts (IComponent, IEntity, IEvent, IDrawer...)
global using ShardECS.Contracts.Components;
global using ShardECS.Contracts.Entities;
global using ShardECS.Contracts.Events;

// Core building blocks
global using ShardECS.SECS.Components;          // BaseComponent, ComponentTypeId<T>
global using ShardECS.SECS.Drawers;             // DrawerBase
global using ShardECS.SECS.Entities;            // EntityFactory
global using ShardECS.SECS.Events;              // EventBus, ComponentTracker<T>, TriggeredSystem
global using ShardECS.SECS.Buffers;             // IMerger<T>, ComponentBuffer<T>
global using ShardECS.SECS.Systems;             // IdentityComponent, IdentitySystem, IIdentityTag

// Standard component library
global using ShardECS.SECS.Components.Standard;

// Default drawers + factories + bootstrap
global using ShardECS.SECS.DefaultGame;
