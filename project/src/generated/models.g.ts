/**
 * Payload for submitting a new agent-to-agent task.
 */
export interface A2ATaskSubmitRequest {
    /**
     * Full task description.
     */
    description?: null | string;
    /**
     * Arbitrary caller-supplied metadata attached to the task.
     */
    metadata?: { [key: string]: any } | null;
    /**
     * Optional run identifier to associate this task with an existing run.
     */
    runId?: null | string;
    /**
     * Optional caller-supplied task identifier. Auto-generated if omitted.
     */
    taskId?: null | string;
    /**
     * Short human-readable task title.
     */
    title: string;
    [property: string]: any;
}

/**
 * Acknowledgement returned after an A2A task is accepted.
 */
export interface A2ATaskSubmitResponse {
    /**
     * Always `accepted` on a successful submission.
     */
    status: Status;
    /**
     * Relative URL to poll for task status.
     */
    statusUrl: string;
    /**
     * Assigned task identifier.
     */
    taskId: string;
    [property: string]: any;
}

/**
 * Always `accepted` on a successful submission.
 */
export enum Status {
    Accepted = "accepted",
}

/**
 * A2A agent capability descriptor served at `/.well-known/agent-card.json`.
 */
export interface AgentCard {
    /**
     * List of capability tags advertised by this agent.
     */
    capabilities: string[];
    description?: null | string;
    /**
     * Named endpoint URLs exposed by this agent.
     */
    endpoints: { [key: string]: string };
    name:      string;
    /**
     * Protocol identifier (e.g. `a2a`).
     */
    protocol: string;
    /**
     * Isolation level: 0=BareCli, 1=OsSandboxed, 2=Container
     */
    sandboxLevel?:        number;
    sandboxRequirements?: null | SandboxRequirements;
    version:              string;
    [property: string]: any;
}

export interface SandboxRequirements {
    needsGpuAccess?: boolean;
    needsKeychain?:  boolean;
    needsNetwork?:   string[];
    needsOAuth?:     boolean;
    [property: string]: any;
}

/**
 * Standardised error response body returned on 4xx/5xx responses.
 */
export interface ErrorEnvelope {
    /**
     * Action identifier associated with the error, when applicable.
     */
    actionId?: null | string;
    /**
     * Human-readable error message.
     */
    error: string;
    /**
     * Machine-readable reason code.
     */
    reasonCode?: null | string;
    /**
     * Task identifier associated with the error, when applicable.
     */
    taskId?: null | string;
    [property: string]: any;
}

/**
 * Response body for the `/healthz` health check endpoint.
 */
export interface HealthResponse {
    /**
     * Always `true` when the runtime is reachable.
     */
    ok: boolean;
    [property: string]: any;
}

/**
 * Response body for `GET /memory/tasks` containing a list of task snapshots.
 */
export interface MemoryTaskListResponse {
    items: TaskSnapshot[];
    /**
     * Backend that served the results (`arcadedb` or `registry`).
     */
    source: Source;
    [property: string]: any;
}

/**
 * Full snapshot of a swarm task including all role outputs.
 */
export interface TaskSnapshot {
    /**
     * Output produced by the builder role.
     */
    buildOutput?: null | string;
    /**
     * Identifiers of child sub-tasks spawned by this task.
     */
    childTaskIds?: string[] | null;
    /**
     * ISO 8601 timestamp when the task was created.
     */
    createdAt: Date;
    /**
     * Full task description.
     */
    description: string;
    /**
     * Error message when the task has failed.
     */
    error?: null | string;
    /**
     * Identifier of the parent task, if this is a sub-task.
     */
    parentTaskId?: null | string;
    /**
     * Output produced by the planner role.
     */
    planningOutput?: null | string;
    /**
     * Output produced by the reviewer role.
     */
    reviewOutput?: null | string;
    /**
     * Run identifier this task belongs to.
     */
    runId?: null | string;
    status: TaskState;
    /**
     * Final summary when the task is completed.
     */
    summary?: null | string;
    /**
     * Unique task identifier.
     */
    taskId: string;
    /**
     * Short human-readable task title.
     */
    title: string;
    /**
     * ISO 8601 timestamp of the last status update.
     */
    updatedAt: Date;
    [property: string]: any;
}

/**
 * Lifecycle state of a swarm task.
 */
export enum TaskState {
    Blocked = "blocked",
    Building = "building",
    Done = "done",
    Planning = "planning",
    Queued = "queued",
    Reviewing = "reviewing",
}

/**
 * Backend that served the results (`arcadedb` or `registry`).
 */
export enum Source {
    Arcadedb = "arcadedb",
    Registry = "registry",
}

/**
 * Paginated feed of task execution events returned by replay endpoints.
 */
export interface TaskExecutionEventFeed {
    /**
     * Ordered list of events (ascending by sequence).
     */
    items: TaskExecutionEvent[];
    /**
     * Sequence number of the last returned event; pass as `cursor` in the next request to
     * continue pagination. `null` when no events were returned.
     */
    nextCursor?: number | null;
    /**
     * Run identifier, present for run-scoped feeds.
     */
    runId?: null | string;
    /**
     * Task identifier, present for task-scoped feeds.
     */
    taskId?: null | string;
    [property: string]: any;
}

/**
 * An immutable, append-only event recorded during task execution. Used for audit trails and
 * replay feeds.
 */
export interface TaskExecutionEvent {
    /**
     * Unique event identifier.
     */
    eventId: string;
    /**
     * Event type discriminator (e.g. `task.started`, `role.completed`).
     */
    eventType: string;
    /**
     * ISO 8601 timestamp when the event occurred.
     */
    occurredAt: Date;
    /**
     * JSON-encoded event payload, when present.
     */
    payload?: null | string;
    /**
     * Identifier of the swarm run this event belongs to.
     */
    runId: string;
    /**
     * Monotonically increasing sequence number scoped to the run.
     */
    runSequence: number;
    /**
     * Identifier of the task this event belongs to.
     */
    taskId: string;
    /**
     * Monotonically increasing sequence number scoped to the task.
     */
    taskSequence: number;
    [property: string]: any;
}

/**
 * Lightweight task representation used in list responses.
 */
export interface TaskSummary {
    error?:    null | string;
    status:    TaskState;
    taskId:    string;
    title:     string;
    updatedAt: Date;
    [property: string]: any;
}

/**
 * Action submitted by a UI client via the AG-UI actions gateway.
 */
export interface UIActionRequest {
    /**
     * Action to perform. Supported values: `request_snapshot`, `refresh_surface`,
     * `submit_task`, `load_memory`, `approve_review`, `reject_review`, `request_rework`,
     * `pause_task`, `resume_task`, `approve_task`, `cancel_task`, `set_subtask_depth`.
     */
    actionId: ActionID;
    /**
     * Action-specific payload. For `submit_task`, provide `title` and optional `description` /
     * `taskId` keys. For `set_subtask_depth`, provide `depth` (integer). For `load_memory`,
     * provide optional `limit` (integer).
     */
    payload?: { [key: string]: any } | null;
    /**
     * Target task identifier, required for task-scoped actions.
     */
    taskId?: null | string;
    [property: string]: any;
}

/**
 * Action to perform. Supported values: `request_snapshot`, `refresh_surface`,
 * `submit_task`, `load_memory`, `approve_review`, `reject_review`, `request_rework`,
 * `pause_task`, `resume_task`, `approve_task`, `cancel_task`, `set_subtask_depth`.
 */
export enum ActionID {
    ApproveReview = "approve_review",
    ApproveTask = "approve_task",
    CancelTask = "cancel_task",
    LoadMemory = "load_memory",
    PauseTask = "pause_task",
    RefreshSurface = "refresh_surface",
    RejectReview = "reject_review",
    RequestRework = "request_rework",
    RequestSnapshot = "request_snapshot",
    ResumeTask = "resume_task",
    SetSubtaskDepth = "set_subtask_depth",
    SubmitTask = "submit_task",
}

/**
 * Acknowledgement returned after a UI action is enqueued.
 */
export interface UIActionResponse {
    /**
     * Echoes the submitted `actionId`.
     */
    actionId: string;
    /**
     * Machine-readable outcome code (e.g. `accepted`).
     */
    reasonCode: string;
    /**
     * Target task identifier, when applicable.
     */
    taskId?: null | string;
    [property: string]: any;
}

/**
 * A single event emitted by the AG-UI event stream.
 */
export interface UIEventEnvelope {
    /**
     * ISO 8601 timestamp when the event was emitted.
     */
    at: Date;
    /**
     * Event-specific payload; shape varies by `type`.
     */
    payload: { [key: string]: any };
    /**
     * Monotonically increasing sequence number.
     */
    sequence: number;
    /**
     * Associated task identifier, when applicable.
     */
    taskId?: null | string;
    /**
     * Event type discriminator (e.g. `agui.task.snapshot`, `agui.task.status`,
     * `agui.graph.node`, `agui.graph.edge`).
     */
    type: string;
    [property: string]: any;
}
