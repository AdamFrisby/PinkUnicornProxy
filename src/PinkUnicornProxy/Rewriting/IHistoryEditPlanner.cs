namespace PinkUnicornProxy.Rewriting;

internal interface IHistoryEditPlanner
{
    Task<HistoryEditPlannerResult> CreatePlanAsync(
        HistoryEditRequest request,
        CancellationToken cancellationToken);
}
