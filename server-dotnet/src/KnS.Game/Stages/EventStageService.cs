using KnS.Data.Repositories;

namespace KnS.Game.Stages;

/// <summary>Entry validation for the same weekly event windows the original browser displays.</summary>
public sealed class EventStageService(EventStageRepository events, TimeProvider clock)
{
    public static bool IsEventStage(long stageId) => stageId is >= 3200000 and < 3300000;

    public bool CanEnter(long stageId) => !IsEventStage(stageId)
        || events.Get(stageId)?.IsOpen(clock.GetUtcNow().ToUnixTimeSeconds()) == true;
}
