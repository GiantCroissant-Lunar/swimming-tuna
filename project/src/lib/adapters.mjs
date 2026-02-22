import { spawn } from "node:child_process";

function applyTemplate(text, vars) {
  return String(text).replace(/\{\{\s*([a-zA-Z0-9_]+)\s*\}\}/g, (_, key) => String(vars[key] ?? ""));
}

function renderArgs(args, vars) {
  return (args ?? []).map((arg) => applyTemplate(arg, vars));
}

function execCommand(command, args, timeoutMs) {
  return new Promise((resolve) => {
    const child = spawn(command, args, { stdio: ["ignore", "pipe", "pipe"] });

    let stdout = "";
    let stderr = "";

    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString();
    });

    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString();
    });

    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill("SIGKILL");
    }, timeoutMs);

    child.on("error", (error) => {
      clearTimeout(timer);
      resolve({ ok: false, stdout, stderr: `${stderr}\n${error.message}`.trim(), code: -1, timedOut: false });
    });

    child.on("close", (code) => {
      clearTimeout(timer);
      resolve({ ok: code === 0 && !timedOut, stdout, stderr, code: code ?? -1, timedOut });
    });
  });
}

async function probeAdapter(adapter, timeoutMs) {
  if (adapter.type === "internal") {
    return { available: true, reason: "internal" };
  }

  if (!adapter.probe?.command) {
    return { available: false, reason: "missing probe command" };
  }

  const result = await execCommand(adapter.probe.command, adapter.probe.args ?? [], Math.min(timeoutMs, 10000));
  if (result.ok) {
    return { available: true, reason: "ok" };
  }

  const reason = result.timedOut
    ? "probe timeout"
    : (result.stderr || result.stdout || `probe failed (${result.code})`).trim();

  return { available: false, reason };
}

function internalEchoResponse(role, vars) {
  const title = vars.task_title || "Untitled";
  if (role === "planner") {
    return `Plan for task \"${title}\":\n1. Clarify assumptions and constraints.\n2. Build the smallest working slice first.\n3. Validate behavior with a runnable check.\n4. Capture follow-up tasks and risks.`;
  }

  if (role === "reviewer") {
    return `Review for task \"${title}\":\n- Check behavior regressions and edge cases.\n- Verify logging and failure handling.\n- Add tests for the core happy path and one failure path.`;
  }

  if (role === "finalizer") {
    return `Final summary for task \"${title}\":\n- Planning, build, and review outputs were captured.\n- Next action: run targeted tests and refine adapter commands for your installed CLIs.`;
  }

  return `Execution notes for task \"${title}\":\n- Implemented core flow with durable state updates and event logging.`;
}

async function executeAdapter(adapter, role, prompt, context, timeoutMs) {
  const vars = {
    role,
    prompt,
    task_id: context.task.id,
    task_title: context.task.title,
    task_description: context.task.description ?? ""
  };

  if (adapter.type === "internal") {
    return {
      ok: true,
      adapterId: adapter.id,
      output: internalEchoResponse(role, vars),
      meta: { internal: true }
    };
  }

  if (!adapter.execute?.command) {
    return { ok: false, adapterId: adapter.id, error: "missing execute command" };
  }

  const args = renderArgs(adapter.execute.args ?? [], vars);
  const result = await execCommand(adapter.execute.command, args, timeoutMs);

  if (!result.ok) {
    const err = result.timedOut
      ? "execution timeout"
      : (result.stderr || result.stdout || `exit code ${result.code}`).trim();
    return { ok: false, adapterId: adapter.id, error: err };
  }

  const output = (result.stdout || result.stderr || "").trim();
  if (!output) {
    return { ok: false, adapterId: adapter.id, error: "empty output" };
  }

  const rejectSnippets = adapter.execute?.rejectOutputSubstrings ?? [];
  const normalized = output.toLowerCase();
  const rejectHit = rejectSnippets.find((snippet) => normalized.includes(String(snippet).toLowerCase()));
  if (rejectHit) {
    return { ok: false, adapterId: adapter.id, error: `rejected output match: ${rejectHit}` };
  }

  return {
    ok: true,
    adapterId: adapter.id,
    output,
    meta: { internal: false }
  };
}

export class AdapterRouter {
  constructor(config, eventWriter) {
    this.config = config;
    this.eventWriter = eventWriter;
    this.timeoutMs = config.runtime?.timeoutMs ?? 120000;
    this.adapters = new Map((config.adapters ?? []).map((adapter) => [adapter.id, adapter]));
    this.availability = new Map();
  }

  async init() {
    for (const adapter of this.adapters.values()) {
      const status = await probeAdapter(adapter, this.timeoutMs);
      this.availability.set(adapter.id, status);
      this.eventWriter({
        type: "adapter.probe",
        adapterId: adapter.id,
        available: status.available,
        reason: status.reason,
        at: new Date().toISOString()
      });
    }
  }

  getAvailabilityRows() {
    return [...this.availability.entries()].map(([id, status]) => ({
      adapterId: id,
      available: status.available,
      reason: status.reason
    }));
  }

  async executeRole(role, prompt, context) {
    const order = this.config.roles?.[role] ?? [];
    const errors = [];

    for (const adapterId of order) {
      const adapter = this.adapters.get(adapterId);
      if (!adapter) {
        errors.push(`${adapterId}: adapter not configured`);
        continue;
      }

      const availability = this.availability.get(adapterId);
      if (!availability?.available) {
        errors.push(`${adapterId}: unavailable (${availability?.reason ?? "not probed"})`);
        continue;
      }

      this.eventWriter({
        type: "adapter.execute.start",
        adapterId,
        role,
        taskId: context.task.id,
        at: new Date().toISOString()
      });

      const result = await executeAdapter(adapter, role, prompt, context, this.timeoutMs);
      if (result.ok) {
        this.eventWriter({
          type: "adapter.execute.success",
          adapterId,
          role,
          taskId: context.task.id,
          at: new Date().toISOString()
        });
        return result;
      }

      errors.push(`${adapterId}: ${result.error}`);
      this.eventWriter({
        type: "adapter.execute.failure",
        adapterId,
        role,
        taskId: context.task.id,
        error: result.error,
        at: new Date().toISOString()
      });
    }

    return {
      ok: false,
      error: `No adapter succeeded for role ${role}. ${errors.join(" | ")}`
    };
  }
}
