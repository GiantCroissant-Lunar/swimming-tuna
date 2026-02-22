import { createTask, getPendingTasks, updateTask } from "./store.mjs";

function stepPrompt(role, task, artifacts) {
  if (role === "planner") {
    return [
      "You are the planner agent in a swarm.",
      `Task title: ${task.title}`,
      `Task description: ${task.description || "(none)"}`,
      "Return a concise step-by-step implementation plan with risk checks."
    ].join("\n");
  }

  if (role === "builder") {
    return [
      "You are the builder agent in a swarm.",
      `Task title: ${task.title}`,
      `Task description: ${task.description || "(none)"}`,
      `Plan:\n${artifacts.plan?.output || "(no plan)"}`,
      "Return implementation notes and concrete execution steps."
    ].join("\n");
  }

  if (role === "reviewer") {
    return [
      "You are the reviewer agent in a swarm.",
      `Task title: ${task.title}`,
      `Plan:\n${artifacts.plan?.output || "(none)"}`,
      `Build output:\n${artifacts.build?.output || "(none)"}`,
      "Find defects, risks, and missing tests."
    ].join("\n");
  }

  return [
    "You are the finalizer agent in a swarm.",
    `Task title: ${task.title}`,
    `Plan:\n${artifacts.plan?.output || "(none)"}`,
    `Build output:\n${artifacts.build?.output || "(none)"}`,
    `Review output:\n${artifacts.review?.output || "(none)"}`,
    "Produce final summary with next actions."
  ].join("\n");
}

function artifactFromResult(step, result) {
  return {
    step,
    adapterId: result.adapterId,
    output: result.output,
    at: new Date().toISOString()
  };
}

async function runTask(task, router, eventWriter) {
  updateTask(task, { status: "planning", error: null });

  const plan = await router.executeRole("planner", stepPrompt("planner", task, task.artifacts), { task });
  if (!plan.ok) {
    updateTask(task, { status: "blocked", error: plan.error });
    eventWriter({ type: "task.blocked", taskId: task.id, reason: plan.error, at: new Date().toISOString() });
    return;
  }
  task.artifacts.plan = artifactFromResult("planner", plan);

  updateTask(task, { status: "building" });
  const build = await router.executeRole("builder", stepPrompt("builder", task, task.artifacts), { task });
  if (!build.ok) {
    updateTask(task, { status: "blocked", error: build.error });
    eventWriter({ type: "task.blocked", taskId: task.id, reason: build.error, at: new Date().toISOString() });
    return;
  }
  task.artifacts.build = artifactFromResult("builder", build);

  updateTask(task, { status: "reviewing" });
  const review = await router.executeRole("reviewer", stepPrompt("reviewer", task, task.artifacts), { task });
  if (!review.ok) {
    updateTask(task, { status: "blocked", error: review.error });
    eventWriter({ type: "task.blocked", taskId: task.id, reason: review.error, at: new Date().toISOString() });
    return;
  }
  task.artifacts.review = artifactFromResult("reviewer", review);

  updateTask(task, { status: "finalizing" });
  const final = await router.executeRole("finalizer", stepPrompt("finalizer", task, task.artifacts), { task });
  if (!final.ok) {
    updateTask(task, { status: "blocked", error: final.error });
    eventWriter({ type: "task.blocked", taskId: task.id, reason: final.error, at: new Date().toISOString() });
    return;
  }

  task.artifacts.final = artifactFromResult("finalizer", final);
  updateTask(task, { status: "done", result: task.artifacts.final.output, error: null });
  eventWriter({ type: "task.done", taskId: task.id, at: new Date().toISOString() });
}

export async function runSwarm({
  tasks,
  router,
  eventWriter,
  maxTasksPerRun,
  enqueue
}) {
  if (enqueue?.title) {
    const task = createTask(tasks, enqueue);
    eventWriter({
      type: "task.created",
      taskId: task.id,
      title: task.title,
      at: new Date().toISOString()
    });
  }

  const pending = getPendingTasks(tasks).slice(0, maxTasksPerRun);

  for (const task of pending) {
    eventWriter({ type: "task.start", taskId: task.id, at: new Date().toISOString() });
    await runTask(task, router, eventWriter);
  }

  return {
    processed: pending.length,
    pendingTotal: getPendingTasks(tasks).length
  };
}
