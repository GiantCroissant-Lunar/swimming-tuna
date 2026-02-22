import fs from "node:fs";

export function loadTasks(tasksPath) {
  const raw = fs.readFileSync(tasksPath, "utf8").trim();
  if (!raw) {
    return [];
  }
  return JSON.parse(raw);
}

export function saveTasks(tasksPath, tasks) {
  fs.writeFileSync(tasksPath, `${JSON.stringify(tasks, null, 2)}\n`, "utf8");
}

export function appendEvent(eventsPath, event) {
  fs.appendFileSync(eventsPath, `${JSON.stringify(event)}\n`, "utf8");
}

export function createTask(tasks, input) {
  const now = new Date().toISOString();
  const id = `tsk-${Date.now().toString(36)}`;
  const task = {
    id,
    title: input.title,
    description: input.description ?? "",
    status: "pending",
    createdAt: now,
    updatedAt: now,
    artifacts: {},
    error: null
  };

  tasks.push(task);
  return task;
}

export function updateTask(task, patch) {
  Object.assign(task, patch, { updatedAt: new Date().toISOString() });
  return task;
}

export function getPendingTasks(tasks) {
  return tasks.filter((task) => task.status === "pending");
}
