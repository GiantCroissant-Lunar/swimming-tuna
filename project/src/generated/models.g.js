/**
 * Always `accepted` on a successful submission.
 */
export var Status;
(function (Status) {
    Status["Accepted"] = "accepted";
})(Status || (Status = {}));
/**
 * Lifecycle state of a swarm task.
 */
export var TaskState;
(function (TaskState) {
    TaskState["Blocked"] = "blocked";
    TaskState["Building"] = "building";
    TaskState["Done"] = "done";
    TaskState["Planning"] = "planning";
    TaskState["Queued"] = "queued";
    TaskState["Reviewing"] = "reviewing";
})(TaskState || (TaskState = {}));
/**
 * Backend that served the results (`arcadedb` or `registry`).
 */
export var Source;
(function (Source) {
    Source["Arcadedb"] = "arcadedb";
    Source["Registry"] = "registry";
})(Source || (Source = {}));
/**
 * Action to perform. Supported values: `request_snapshot`, `refresh_surface`,
 * `submit_task`, `load_memory`, `approve_review`, `reject_review`, `request_rework`,
 * `pause_task`, `resume_task`, `approve_task`, `cancel_task`, `set_subtask_depth`.
 */
export var ActionID;
(function (ActionID) {
    ActionID["ApproveReview"] = "approve_review";
    ActionID["ApproveTask"] = "approve_task";
    ActionID["CancelTask"] = "cancel_task";
    ActionID["LoadMemory"] = "load_memory";
    ActionID["PauseTask"] = "pause_task";
    ActionID["RefreshSurface"] = "refresh_surface";
    ActionID["RejectReview"] = "reject_review";
    ActionID["RequestRework"] = "request_rework";
    ActionID["RequestSnapshot"] = "request_snapshot";
    ActionID["ResumeTask"] = "resume_task";
    ActionID["SetSubtaskDepth"] = "set_subtask_depth";
    ActionID["SubmitTask"] = "submit_task";
})(ActionID || (ActionID = {}));
