# SECS

Core runtime. Implements all Contracts interfaces.

## Key files

- World/World.cs                : Tick loop, Parallel.ForEach over Dressers
- Components/ComponentStore.cs  : Thread-safe ConcurrentDictionary store
- Drawers/DrawerBase.cs         : Abstract base, override RequiredComponents and OnExecute
- Dressers/Dresser.cs           : Fluent builder with .Add chaining
- Events/EventBus.cs            : Simple pub/sub

## Depends on

- Contracts

## Rules for agents

- Do not modify Contracts interfaces from here
- Run dotnet test SECS.Tests after every change