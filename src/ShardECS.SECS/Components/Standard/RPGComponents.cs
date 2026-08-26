namespace ShardECS.SECS.Components.Standard;

public class LevelComponent : BaseComponent
{
    public int   Level;
    public float XP;
    public float XPToNext;
    public LevelComponent(int level = 1, float xp = 0f, float xpToNext = 100f)
    { Level = level; XP = xp; XPToNext = xpToNext; }
}

public class ManaComponent : BaseComponent
{
    public float Current;
    public float Max;
    public float Regen;
    public ManaComponent(float current, float max, float regen = 0f)
    { Current = current; Max = max; Regen = regen; }
}

public class StatusEffectComponent : BaseComponent
{
    public string EffectName;
    public float  Duration;
    public int    Stacks;
    public StatusEffectComponent(string effectName = "", float duration = 0f, int stacks = 1)
    { EffectName = effectName; Duration = duration; Stacks = stacks; }
}

public class QuestFlagComponent : BaseComponent
{
    public int QuestId;
    public int Stage;
    public QuestFlagComponent(int questId = 0, int stage = 0) { QuestId = questId; Stage = stage; }
}

public class DialogueComponent : BaseComponent
{
    public int TreeId;
    public int NodeId;
    public DialogueComponent(int treeId = 0, int nodeId = 0) { TreeId = treeId; NodeId = nodeId; }
}

public class LootTableComponent : BaseComponent
{
    public int   TableId;
    public float DropChance;
    public LootTableComponent(int tableId = 0, float dropChance = 1f) { TableId = tableId; DropChance = dropChance; }
}
