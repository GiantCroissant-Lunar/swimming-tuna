import { ensureDataFiles, getProjectRoot, loadConfig } from "./lib/config.mjs";
import { AdapterRouter } from "./lib/adapters.mjs";
import { appendEvent, loadTasks, saveTasks } from "./lib/store.mjs";
import { runSwarm } from "./lib/orchestrator.mjs";

function parseArgs(argv) {
  const out = {
    command: argv[0] ?? "run",
    options: {}
  };

  for (let i = 1; i < argv.length; i += 1) {
    const token = argv[i];
    if (!token.startsWith("--")) {
      continue;
    }

    const key = token.slice(2);
    const next = argv[i + 1];
    if (!next || next.startsWith("--")) {
      out.options[key] = true;
      continue;
    }

    out.options[key] = next;
    i += 1;
  }

  return out;
}

function summarizeTasks(tasks) {
  const counts = new Map();
  for (const task of tasks) {
    counts.set(task.status, (counts.get(task.status) ?? 0) + 1);
  }

  return [...counts.entries()]
    .sort((a, b) => a[0].localeCompare(b[0]))
    .map(([status, count]) => `${status}: ${count}`)
    .join(", ");
}

function printUsage() {
  console.log([
    "Usage:",
    "  node src/index.mjs init [--config <path>]",
    "  node src/index.mjs status [--config <path>]",
    "  node src/index.mjs run [--config <path>] [--task <title>] [--desc <description>] [--max <n>]"
  ].join("\n"));
}

async function main() {
  const { command, options } = parseArgs(process.argv.slice(2));

  if (command === "help" || options.help) {
    printUsage();
    return;
  }

  const { config, configPath, tasksPath, eventsPath } = loadConfig(options.config);
  ensureDataFiles(tasksPath, eventsPath);

  const eventWriter = (event) => appendEvent(eventsPath, {
    ...event,
    source: "swimming-tuna-mvp",
    projectRoot: getProjectRoot()
  });

  const router = new AdapterRouter(config, eventWriter);
  await router.init();

  if (command === "init") {
    console.log(`Initialized data files.`);
    console.log(`Config: ${configPath}`);
    console.log(`Tasks: ${tasksPath}`);
    console.log(`Events: ${eventsPath}`);
    return;
  }

  const tasks = loadTasks(tasksPath);

  if (command === "status") {
    console.log(`Config: ${configPath}`);
    console.log(`Tasks summary: ${summarizeTasks(tasks) || "none"}`);
    console.log("Adapter availability:");
    for (const row of router.getAvailabilityRows()) {
      console.log(`- ${row.adapterId}: ${row.available ? "available" : "unavailable"} (${row.reason})`);
    }
    return;
  }

  if (command !== "run") {
    printUsage();
    process.exitCode = 1;
    return;
  }

  const maxTasksPerRun = Number(options.max || config.runtime?.maxTasksPerRun || 1);
  const enqueue = options.task
    ? { title: String(options.task), description: String(options.desc ?? "") }
    : null;

  const result = await runSwarm({
    tasks,
    router,
    eventWriter,
    maxTasksPerRun,
    enqueue
  });

  saveTasks(tasksPath, tasks);

  console.log(`Processed: ${result.processed} task(s)`);
  console.log(`Pending remaining: ${result.pendingTotal}`);
  console.log(`Tasks file: ${tasksPath}`);
  console.log(`Events file: ${eventsPath}`);
}

main().catch((error) => {
  console.error(error instanceof Error ? error.stack : String(error));
  process.exitCode = 1;
});
