import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const PROJECT_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

export function getProjectRoot() {
  return PROJECT_ROOT;
}

export function loadConfig(configPathArg) {
  const configPath = configPathArg
    ? path.resolve(configPathArg)
    : path.join(PROJECT_ROOT, "config", "swarm.config.json");

  const raw = fs.readFileSync(configPath, "utf8");
  const config = JSON.parse(raw);

  if (!config.roles || !config.adapters) {
    throw new Error("Invalid config: roles and adapters are required");
  }

  return {
    config,
    configPath,
    tasksPath: path.resolve(PROJECT_ROOT, config.paths?.tasks ?? "data/tasks.json"),
    eventsPath: path.resolve(PROJECT_ROOT, config.paths?.events ?? "data/events.jsonl")
  };
}

export function ensureDataFiles(tasksPath, eventsPath) {
  fs.mkdirSync(path.dirname(tasksPath), { recursive: true });
  fs.mkdirSync(path.dirname(eventsPath), { recursive: true });
  if (!fs.existsSync(tasksPath)) {
    fs.writeFileSync(tasksPath, "[]\n", "utf8");
  }
  if (!fs.existsSync(eventsPath)) {
    fs.writeFileSync(eventsPath, "", "utf8");
  }
}
