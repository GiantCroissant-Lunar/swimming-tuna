import fs from "node:fs";
import { TaskState } from "../generated/models.g.js";

/**
 * @typedef {Object} LocalTask
 * @property {string} id
 * @property {string} title
 * @property {string} description
 * @property {string} status
 * @property {string} createdAt
 * @property {string} updatedAt
 * @property {Object} artifacts
 * @property {string|null} error
 */

/**
 * @param {string} tasksPath
 * @returns {LocalTask[]}
 */
export function loadTasks(tasksPath) {
  const raw = fs.readFileSync(tasksPath, "utf8").trim();
  if (!raw) {
    return [];
  }
  return JSON.parse(raw);
}

/**
 * @param {string} tasksPath
 * @param {LocalTask[]} tasks
 */
export function saveTasks(tasksPath, tasks) {
  fs.writeFileSync(tasksPath, `${JSON.stringify(tasks, null, 2)}\n`, "utf8");
}

/**
 * @param {string} eventsPath
 * @param {Object} event
 */
export function appendEvent(eventsPath, event) {
  fs.appendFileSync(eventsPath, `${JSON.stringify(event)}\n`, "utf8");
}

/**
 * @param {LocalTask[]} tasks
 * @param {{ title: string; description?: string }} input
 * @returns {LocalTask}
 */
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

/**
 * @param {LocalTask} task
 * @param {Partial<LocalTask>} patch
 * @returns {LocalTask}
 */
export function updateTask(task, patch) {
  Object.assign(task, patch, { updatedAt: new Date().toISOString() });
  return task;
}

/**
 * Returns tasks with "pending" status (awaiting their first run).
 * @param {LocalTask[]} tasks
 * @returns {LocalTask[]}
 */
export function getPendingTasks(tasks) {
  return tasks.filter((task) => task.status === "pending");
}

export { TaskState };
