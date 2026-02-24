using Riok.Mapperly.Abstractions;
using SwarmAssistant.Runtime.Dto;
using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime;

[Mapper]
public static partial class TaskSnapshotMapper
{
    [MapProperty(nameof(TaskSnapshot.Status), nameof(TaskSnapshotDto.Status), Use = nameof(StatusToString))]
    public static partial TaskSnapshotDto ToDto(TaskSnapshot snapshot);

    [MapProperty(nameof(TaskSnapshot.Status), nameof(TaskSummaryDto.Status), Use = nameof(StatusToString))]
    [MapperIgnoreSource(nameof(TaskSnapshot.Description))]
    [MapperIgnoreSource(nameof(TaskSnapshot.CreatedAt))]
    [MapperIgnoreSource(nameof(TaskSnapshot.PlanningOutput))]
    [MapperIgnoreSource(nameof(TaskSnapshot.BuildOutput))]
    [MapperIgnoreSource(nameof(TaskSnapshot.ReviewOutput))]
    [MapperIgnoreSource(nameof(TaskSnapshot.Summary))]
    [MapperIgnoreSource(nameof(TaskSnapshot.ParentTaskId))]
    [MapperIgnoreSource(nameof(TaskSnapshot.ChildTaskIds))]
    [MapperIgnoreSource(nameof(TaskSnapshot.RunId))]
    public static partial TaskSummaryDto ToSummaryDto(TaskSnapshot snapshot);

    public static partial RunDto ToDto(RunEntry run);

    public static partial TaskExecutionEventDto ToDto(TaskExecutionEvent evt);

    private static string StatusToString(SwarmAssistant.Contracts.Tasks.TaskStatus status) =>
        status.ToString().ToLowerInvariant();
}
