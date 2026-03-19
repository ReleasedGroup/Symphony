const elements = {
  heroPanel: document.getElementById("hero-panel"),
  alert: document.getElementById("dashboard-alert"),
  metricGrid: document.getElementById("metric-grid"),
  liveRuns: document.getElementById("live-runs"),
  issueDistribution: document.getElementById("issue-distribution"),
  activityFeed: document.getElementById("activity-feed"),
  trackedIssues: document.getElementById("tracked-issues"),
  issueDetail: document.getElementById("issue-detail"),
  instanceStatus: document.getElementById("instance-status"),
  rateLimits: document.getElementById("rate-limits")
};

const toneClasses = {
  healthy: "border-emerald-400/30 bg-emerald-400/10 text-emerald-100",
  warning: "border-amber-400/30 bg-amber-400/10 text-amber-100",
  danger: "border-rose-400/30 bg-rose-400/10 text-rose-100",
  info: "border-cyan-400/30 bg-cyan-400/10 text-cyan-100",
  neutral: "border-white/10 bg-white/5 text-slate-200"
};

const state = {
  runtime: null,
  snapshot: null,
  health: null,
  issueDetail: null,
  selectedIssue: null,
  loading: false,
  refreshQueued: false,
  autoRefresh: true,
  error: null,
  issueError: null,
  lastLoadedAt: null
};

const refreshIntervalMs = 15000;
let refreshHandle = null;
const baseDocumentTitle = document.title;

document.addEventListener("click", async event => {
  const issueButton = event.target.closest("[data-issue-identifier]");
  if (issueButton?.dataset.issueIdentifier) {
    await selectIssue(issueButton.dataset.issueIdentifier);
    return;
  }

  if (event.target.closest("[data-action='refresh']")) {
    await loadDashboard({ queueRefresh: true });
  }
});

window.addEventListener("hashchange", () => {
  const issueIdentifier = getIssueFromHash();
  if (issueIdentifier && issueIdentifier !== state.selectedIssue) {
    void selectIssue(issueIdentifier);
  }
});

void loadDashboard();
scheduleRefresh();

async function loadDashboard({ queueRefresh = false } = {}) {
  if (state.loading) {
    return;
  }

  state.loading = true;
  state.refreshQueued = queueRefresh;
  state.error = null;
  render();

  try {
    if (queueRefresh) {
      await fetch("/api/v1/refresh", { method: "POST" });
    }

    const [healthResult, runtimeResult, stateResult] = await Promise.allSettled([
      fetchHealth(),
      fetchJson("/api/v1/runtime"),
      fetchJson("/api/v1/state")
    ]);

    if (runtimeResult.status !== "fulfilled") {
      throw runtimeResult.reason;
    }

    if (stateResult.status !== "fulfilled") {
      throw stateResult.reason;
    }

    state.health = healthResult.status === "fulfilled"
      ? healthResult.value
      : {
          ok: false,
          label: "Unreachable",
          detail: healthResult.reason instanceof Error ? healthResult.reason.message : "Health probe failed."
        };
    state.runtime = runtimeResult.value;
    state.snapshot = stateResult.value;
    state.lastLoadedAt = new Date().toISOString();

    const preferredIssue = getIssueFromHash() || state.selectedIssue || collectIssueIdentifiers(state.snapshot)[0] || null;
    if (preferredIssue) {
      await selectIssue(preferredIssue, false);
    } else {
      state.selectedIssue = null;
      state.issueDetail = null;
      state.issueError = null;
    }
  } catch (error) {
    state.error = error instanceof Error ? error.message : "Dashboard data could not be loaded.";
  } finally {
    state.loading = false;
    state.refreshQueued = false;
    render();
  }
}

async function selectIssue(issueIdentifier, updateHash = true) {
  state.selectedIssue = issueIdentifier;
  state.issueError = null;

  if (updateHash) {
    history.replaceState(null, "", `#issue/${encodeURIComponent(issueIdentifier)}`);
  }

  render();

  try {
    state.issueDetail = await fetchJson(`/api/v1/${encodeURIComponent(issueIdentifier)}`);
  } catch (error) {
    state.issueDetail = null;
    state.issueError = error instanceof Error ? error.message : `Issue ${issueIdentifier} could not be loaded.`;
  }

  render();
}

function render() {
  updateDocumentTitle();
  elements.heroPanel.innerHTML = renderHeroPanel();
  elements.alert.innerHTML = renderAlert();
  elements.metricGrid.innerHTML = renderMetricCards();
  elements.liveRuns.innerHTML = renderLiveRuns();
  elements.issueDistribution.innerHTML = renderIssueDistribution();
  elements.activityFeed.innerHTML = renderActivityFeed();
  elements.trackedIssues.innerHTML = renderTrackedIssues();
  elements.issueDetail.innerHTML = renderIssueDetail();
  elements.instanceStatus.innerHTML = renderInstanceStatus();
  elements.rateLimits.innerHTML = renderRateLimits();

  const autoRefreshToggle = document.getElementById("auto-refresh");
  if (autoRefreshToggle) {
    autoRefreshToggle.checked = state.autoRefresh;
    autoRefreshToggle.onchange = event => {
      state.autoRefresh = event.target.checked;
      scheduleRefresh();
      render();
    };
  }
}

function renderHeroPanel() {
  const workflow = state.runtime?.workflow;
  const summary = state.snapshot
    ? `${formatNumber(state.snapshot.counts.running)} active, ${formatNumber(state.snapshot.counts.retrying)} retrying, ${formatNumber(state.snapshot.counts.tracked)} tracked`
    : "Waiting for orchestration telemetry";
  const refreshLabel = state.refreshQueued ? "Queuing tick..." : state.loading ? "Syncing..." : "Refresh now";

  return `
    <div class="panel-body px-6 py-6 sm:px-8 sm:py-8">
      <div class="grid gap-8 lg:grid-cols-[minmax(0,1.25fr)_minmax(0,0.75fr)] lg:items-start">
        <div>
          <div class="section-kicker">Symphony Instance</div>
          <div class="mt-3 flex flex-wrap items-center gap-3">
            <h1 class="font-display text-4xl font-semibold tracking-tight text-white text-glow sm:text-5xl">Control Room</h1>
            <span class="status-chip ${getHealthTone()}">${escapeHtml(state.health?.label || (state.loading ? "Syncing" : "Unknown"))}</span>
          </div>
          <p class="mt-4 max-w-2xl text-base leading-7 text-slate-300 sm:text-lg">
            Live orchestration visibility across health, workload, activity, leases, and Codex spend for the current Symphony host.
          </p>
          <div class="mt-6 flex flex-wrap gap-3 text-sm text-slate-300">
            <span class="glass-badge">${escapeHtml(`v${state.runtime?.application?.version || "unknown"}`)}</span>
            <span class="glass-badge">${escapeHtml(state.runtime?.orchestration?.instanceId || "instance auto-id")}</span>
            <span class="glass-badge">${escapeHtml(summary)}</span>
            <span class="glass-badge">${escapeHtml(workflow?.sourcePath || "workflow unavailable")}</span>
          </div>
        </div>

        <div class="panel rounded-[24px] border-white/12 bg-white/[0.045]">
          <div class="panel-body p-5 sm:p-6">
            <div class="flex items-start justify-between gap-4">
              <div>
                <div class="section-kicker">Current Pulse</div>
                <p class="mt-3 font-display text-2xl font-semibold text-white">${escapeHtml(summary)}</p>
              </div>
              <button
                type="button"
                data-action="refresh"
                class="inline-flex items-center rounded-full border border-cyan-300/25 bg-cyan-300/10 px-4 py-2 text-sm font-medium text-cyan-100 transition hover:border-cyan-200/40 hover:bg-cyan-300/15 disabled:cursor-not-allowed disabled:opacity-60"
                ${state.loading ? "disabled" : ""}>
                ${escapeHtml(refreshLabel)}
              </button>
            </div>

            <div class="mt-6 grid gap-4 sm:grid-cols-2">
              <div class="rounded-2xl border border-white/10 bg-slate-950/55 p-4">
                <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Workflow</div>
                <div class="mt-2 text-sm text-slate-200">${escapeHtml(workflow?.tracker?.owner || "owner")}/${escapeHtml(workflow?.tracker?.repo || "repo")}</div>
                <div class="mt-1 text-sm text-slate-400">Poll every ${formatDurationFromMilliseconds(workflow?.polling?.intervalMs || state.runtime?.runtimeDefaults?.polling?.intervalMs || 0)}</div>
              </div>
              <label class="flex cursor-pointer items-center justify-between rounded-2xl border border-white/10 bg-slate-950/55 p-4 text-sm text-slate-200">
                <span>
                  <span class="block text-xs uppercase tracking-[0.22em] text-slate-400">Auto refresh</span>
                  <span class="mt-1 block text-sm text-slate-200">Refresh every 15 seconds</span>
                </span>
                <input id="auto-refresh" type="checkbox" class="h-5 w-5 rounded border-white/20 bg-slate-900 text-cyan-300 focus:ring-cyan-300/40">
              </label>
            </div>

            <div class="mt-4 flex flex-wrap gap-3 text-xs text-slate-400">
              <span>Updated ${escapeHtml(formatRelativeTime(state.snapshot?.generated_at || state.lastLoadedAt))}</span>
              <span>Workflow loaded ${escapeHtml(formatRelativeTime(workflow?.loadedAtUtc))}</span>
            </div>
          </div>
        </div>
      </div>
    </div>`;
}

function renderAlert() {
  if (!state.error) {
    return "";
  }

  return `
    <div class="panel border-rose-400/25 bg-rose-500/10">
      <div class="panel-body flex items-start gap-3 px-5 py-4 text-sm text-rose-50">
        <span class="mt-0.5 inline-flex h-6 w-6 items-center justify-center rounded-full bg-rose-500/20 font-display text-base">!</span>
        <div>
          <div class="font-medium">Dashboard refresh failed</div>
          <div class="mt-1 text-rose-100/80">${escapeHtml(state.error)}</div>
        </div>
      </div>
    </div>`;
}

function renderMetricCards() {
  const snapshot = state.snapshot;
  const runtime = state.runtime;
  const maxConcurrent = runtime?.workflow?.agent?.maxConcurrentAgents || runtime?.runtimeDefaults?.agent?.maxConcurrentAgents || 0;
  const utilization = maxConcurrent > 0 && snapshot ? Math.round((snapshot.counts.running / maxConcurrent) * 100) : 0;
  const metrics = [
    ["Running agents", formatNumber(snapshot?.counts.running || 0), maxConcurrent ? `${utilization}% of ${maxConcurrent} slots occupied` : "No capacity configured"],
    ["Retry queue", formatNumber(snapshot?.counts.retrying || 0), snapshot?.retrying?.length ? `Next retry ${formatRelativeTime(snapshot.retrying[0].due_at)}` : "No delayed work scheduled"],
    ["Tracked issues", formatNumber(snapshot?.counts.tracked || 0), snapshot?.tracked?.by_state?.length ? `${snapshot.tracked.by_state.length} state buckets` : "No cached issue state yet"],
    ["Total tokens", formatNumber(snapshot?.codex_totals?.total_tokens || 0), `${formatNumber(snapshot?.codex_totals?.input_tokens || 0)} in / ${formatNumber(snapshot?.codex_totals?.output_tokens || 0)} out`],
    ["Codex runtime", formatSeconds(snapshot?.codex_totals?.seconds_running || 0), state.health?.detail || "No health detail available"],
    ["Lease state", formatNumber(activeLeaseCount(snapshot)), activeLeaseCount(snapshot) ? "Coordination lease rows are present" : "No active lease rows were found"]
  ];

  return metrics.map(([label, value, detail]) => `
    <article class="metric-card">
      <div class="panel-body">
        <div class="metric-label">${escapeHtml(label)}</div>
        <div class="metric-value">${escapeHtml(value)}</div>
        <div class="metric-detail">${escapeHtml(detail)}</div>
      </div>
    </article>`).join("");
}

function renderLiveRuns() {
  const running = state.snapshot?.running || [];
  const retrying = state.snapshot?.retrying || [];
  const maxTurns = state.runtime?.workflow?.agent?.maxTurns || 0;

  return `
    <div class="panel-body p-6">
      <div class="flex items-center justify-between gap-4">
        <div>
          <div class="section-kicker">Live workload</div>
          <h2 class="section-title">Runs in flight</h2>
        </div>
        <span class="glass-badge">${escapeHtml(`${running.length} active / ${retrying.length} queued`)}</span>
      </div>

      <div class="mt-6 space-y-4">
        ${running.length ? running.map(run => renderRunningCard(run, maxTurns)).join("") : renderEmptyState("No agents are running.", "As soon as the worker dispatches an eligible issue, it will appear here with turns, session, and token totals.")}
      </div>

      <div class="mt-8">
        <div class="flex items-center justify-between gap-4">
          <h3 class="text-sm font-semibold uppercase tracking-[0.24em] text-slate-300">Retry queue</h3>
          <span class="text-xs text-slate-400">${escapeHtml(retrying.length ? "Ordered by due time" : "Idle")}</span>
        </div>
        <div class="mt-4 space-y-3">
          ${retrying.length ? retrying.map(renderRetryRow).join("") : renderCompactEmpty("No retry backlog")}
        </div>
      </div>
    </div>`;
}

function renderRunningCard(run, maxTurns) {
  const progress = maxTurns > 0 ? Math.min(run.turn_count / maxTurns, 1) : 0;
  const width = `${Math.max(progress * 100, run.turn_count > 0 ? 8 : 0)}%`;

  return `
    <button type="button" data-issue-identifier="${escapeHtml(run.issue_identifier)}" class="issue-row ${run.issue_identifier === state.selectedIssue ? "issue-row-selected" : ""}">
      <div class="min-w-0 flex-1">
        <div class="flex flex-wrap items-center gap-2">
          <span class="status-chip ${toneClasses.info}">Running</span>
          <span class="text-sm font-semibold text-white">${escapeHtml(run.issue_identifier)}</span>
          <span class="truncate text-sm text-slate-300">${escapeHtml(run.title || "Untitled issue")}</span>
        </div>
        <div class="mt-3 grid gap-3 sm:grid-cols-2">
          <div>
            <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Latest event</div>
            <div class="mt-1 text-sm text-slate-200">${escapeHtml(run.last_event || "waiting")}</div>
            <div class="mt-1 text-sm text-slate-400">${escapeHtml(run.last_message || "No message recorded")}</div>
          </div>
          <div>
            <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Session and state</div>
            <div class="mt-1 text-sm text-slate-200">${escapeHtml(run.session_id || "Session not started")} in ${escapeHtml(run.state || "Unknown")}</div>
            <div class="mt-1 text-sm text-slate-400">Started ${escapeHtml(formatRelativeTime(run.started_at))}</div>
          </div>
        </div>
        <div class="mt-4">
          <div class="flex items-center justify-between text-xs uppercase tracking-[0.22em] text-slate-400">
            <span>Turn progress</span>
            <span>${escapeHtml(maxTurns ? `${run.turn_count}/${maxTurns}` : `${run.turn_count} turns`)}</span>
          </div>
          <div class="mt-2 h-2.5 overflow-hidden rounded-full bg-white/8">
            <div class="h-full rounded-full bg-gradient-to-r from-cyan-300 via-emerald-300 to-orange-300" style="width: ${width};"></div>
          </div>
        </div>
      </div>

      <div class="shrink-0 text-right">
        <div class="font-display text-2xl font-semibold text-white">${escapeHtml(formatNumber(run.tokens?.total_tokens || 0))}</div>
        <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Tokens</div>
      </div>
    </button>`;
}

function renderRetryRow(retry) {
  return `
    <button type="button" data-issue-identifier="${escapeHtml(retry.issue_identifier)}" class="issue-row ${retry.issue_identifier === state.selectedIssue ? "issue-row-selected" : ""}">
      <div class="min-w-0 flex-1">
        <div class="flex flex-wrap items-center gap-2">
          <span class="status-chip ${toneClasses.warning}">Retry</span>
          <span class="text-sm font-semibold text-white">${escapeHtml(retry.issue_identifier)}</span>
          <span class="truncate text-sm text-slate-300">${escapeHtml(retry.title || "Tracked issue")}</span>
        </div>
        <div class="mt-2 text-sm text-slate-300">${escapeHtml(retry.error || "Retry waiting for its due time")}</div>
      </div>
      <div class="shrink-0 text-right text-sm text-slate-300">
        <div>Attempt ${escapeHtml(String(retry.attempt || 0))}</div>
        <div class="mt-1 text-xs uppercase tracking-[0.22em] text-slate-400">${escapeHtml(formatRelativeTime(retry.due_at))}</div>
      </div>
    </button>`;
}

function renderIssueDistribution() {
  const groups = state.snapshot?.tracked?.by_state || [];
  const total = groups.reduce((sum, group) => sum + group.count, 0);

  return `
    <div class="panel-body p-6">
      <div class="section-kicker">Issue portfolio</div>
      <h2 class="section-title">State distribution</h2>
      <p class="mt-3 text-sm leading-6 text-slate-300">Cached issue state is summarized directly from the durable tracker cache, so the portfolio view survives host restarts.</p>
      <div class="mt-6 space-y-4">
        ${groups.length
          ? groups.map(group => `
              <div class="rounded-3xl border border-white/8 bg-white/[0.035] p-4">
                <div class="flex items-center justify-between gap-3">
                  <div class="text-sm font-medium text-white">${escapeHtml(group.state)}</div>
                  <div class="text-sm text-slate-300">${escapeHtml(formatNumber(group.count))}</div>
                </div>
                <div class="mt-3 h-2.5 overflow-hidden rounded-full bg-white/8">
                  <div class="h-full rounded-full bg-gradient-to-r from-cyan-300 via-emerald-300 to-orange-300" style="width: ${Math.max(total ? (group.count / total) * 100 : 0, 6)}%"></div>
                </div>
              </div>`).join("")
          : renderEmptyState("No tracked issues yet.", "Once the GitHub tracker is polled, issue counts by state will appear here.")}
      </div>
    </div>`;
}

function renderActivityFeed() {
  const activity = state.snapshot?.activity || [];
  return `
    <div class="panel-body p-6">
      <div class="flex items-center justify-between gap-4">
        <div>
          <div class="section-kicker">Recent activity</div>
          <h2 class="section-title">Event stream</h2>
        </div>
        <span class="glass-badge">${escapeHtml(formatNumber(activity.length))} events</span>
      </div>
      <div class="mt-6 space-y-3">
        ${activity.length ? activity.map(renderActivityEntry).join("") : renderEmptyState("No activity logged yet.", "When dispatch, retries, turns, or terminal events are recorded, they will stream into this feed.")}
      </div>
    </div>`;
}

function renderActivityEntry(entry) {
  const tone = getEventTone(entry);
  return `
    <div class="rounded-3xl border ${tone} bg-white/[0.035] p-4">
      <div class="flex flex-wrap items-center gap-2">
        <span class="status-chip ${tone}">${escapeHtml(entry.event)}</span>
        ${entry.issue_identifier ? `<button type="button" data-issue-identifier="${escapeHtml(entry.issue_identifier)}" class="text-sm font-semibold text-white hover:text-cyan-100">${escapeHtml(entry.issue_identifier)}</button>` : ""}
        <span class="text-xs uppercase tracking-[0.22em] text-slate-400">${escapeHtml(formatRelativeTime(entry.at))}</span>
      </div>
      <p class="mt-3 text-sm leading-6 text-slate-300">${escapeHtml(entry.message || "No message")}</p>
      <div class="mt-3 flex flex-wrap gap-3 text-xs text-slate-400">
        ${entry.session_id ? `<span>Session ${escapeHtml(entry.session_id)}</span>` : ""}
        ${entry.level ? `<span>${escapeHtml(entry.level)}</span>` : ""}
      </div>
    </div>`;
}

function renderTrackedIssues() {
  const issues = state.snapshot?.tracked?.recently_updated || [];
  return `
    <div class="panel-body p-6">
      <div class="flex items-center justify-between gap-4">
        <div>
          <div class="section-kicker">Tracked issues</div>
          <h2 class="section-title">Recently updated</h2>
        </div>
        <span class="glass-badge">${escapeHtml(formatNumber(state.snapshot?.tracked?.total || 0))} total</span>
      </div>
      <div class="mt-6 space-y-3">
        ${issues.length ? issues.map(renderTrackedIssue).join("") : renderEmptyState("No tracked issues available.", "This section fills from the durable issue cache once the first tracker sync succeeds.")}
      </div>
    </div>`;
}

function renderTrackedIssue(issue) {
  return `
    <button type="button" data-issue-identifier="${escapeHtml(issue.issue_identifier)}" class="issue-row ${issue.issue_identifier === state.selectedIssue ? "issue-row-selected" : ""}">
      <div class="min-w-0 flex-1">
        <div class="flex flex-wrap items-center gap-2">
          <span class="status-chip ${getIssueStatusTone(issue.status)}">${escapeHtml(issue.status || "tracked")}</span>
          <span class="text-sm font-semibold text-white">${escapeHtml(issue.issue_identifier)}</span>
        </div>
        <div class="mt-2 text-sm text-slate-200">${escapeHtml(issue.title || "Untitled issue")}</div>
        <div class="mt-3 flex flex-wrap gap-2 text-xs text-slate-400">
          <span>${escapeHtml(issue.state || "Unknown state")}</span>
          ${issue.milestone ? `<span>Milestone ${escapeHtml(issue.milestone)}</span>` : ""}
          <span>Updated ${escapeHtml(formatRelativeTime(issue.updated_at))}</span>
        </div>
      </div>
      <div class="shrink-0 self-center text-xs uppercase tracking-[0.22em] text-slate-400">Open</div>
    </button>`;
}

function renderIssueDetail() {
  if (!state.selectedIssue) {
    return `
      <div class="panel-body p-6">
        <div class="section-kicker">Issue detail</div>
        <h2 class="section-title">Select an issue</h2>
        <p class="mt-4 text-sm leading-6 text-slate-300">Pick any running, retrying, or recently updated issue to inspect its workspace, retries, tokens, and latest events.</p>
      </div>`;
  }

  const detail = state.issueDetail;
  const tracked = detail?.tracked;
  const running = detail?.running;
  const retry = detail?.retry;
  const recentEvents = detail?.recent_events || [];

  return `
    <div class="panel-body p-6">
      <div class="flex items-start justify-between gap-4">
        <div>
          <div class="section-kicker">Issue detail</div>
          <h2 class="section-title">${escapeHtml(state.selectedIssue)}</h2>
        </div>
        <span class="status-chip ${getIssueStatusTone(detail?.status || "tracked")}">${escapeHtml(detail?.status || "tracked")}</span>
      </div>
      ${state.issueError ? `<div class="mt-4 rounded-2xl border border-rose-400/20 bg-rose-500/10 p-4 text-sm text-rose-100">${escapeHtml(state.issueError)}</div>` : ""}
      <div class="mt-5 rounded-3xl border border-white/10 bg-white/[0.035] p-5">
        <div class="text-sm font-semibold text-white">${escapeHtml(tracked?.title || "Tracked issue")}</div>
        <div class="mt-3 grid gap-3 text-sm text-slate-300 sm:grid-cols-2">
          <div><div class="text-xs uppercase tracking-[0.22em] text-slate-400">Tracker state</div><div class="mt-1">${escapeHtml(tracked?.cache_state || "Unknown")}</div></div>
          <div><div class="text-xs uppercase tracking-[0.22em] text-slate-400">Milestone</div><div class="mt-1">${escapeHtml(tracked?.milestone || "None")}</div></div>
          <div><div class="text-xs uppercase tracking-[0.22em] text-slate-400">Workspace</div><div class="mt-1 break-all">${escapeHtml(detail?.workspace?.path || "Not prepared")}</div></div>
          <div><div class="text-xs uppercase tracking-[0.22em] text-slate-400">Updated</div><div class="mt-1">${escapeHtml(formatRelativeTime(tracked?.updated_at))}</div></div>
        </div>
        ${tracked?.url ? `<a href="${escapeAttribute(tracked.url)}" target="_blank" rel="noreferrer" class="mt-4 inline-flex text-sm font-medium text-cyan-100 hover:text-cyan-50">Open issue in GitHub</a>` : ""}
      </div>

      <div class="mt-5 grid gap-4 sm:grid-cols-2">
        <div class="rounded-3xl border border-white/10 bg-slate-950/55 p-4">
          <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Live run</div>
          <div class="mt-2 text-sm text-slate-200">${escapeHtml(running?.last_event || retry?.error || "No active run")}</div>
          <div class="mt-3 text-xs text-slate-400">${escapeHtml(running ? `${running.turn_count} turns, ${formatNumber(running.tokens?.total_tokens || 0)} tokens` : "Waiting for dispatch")}</div>
        </div>
        <div class="rounded-3xl border border-white/10 bg-slate-950/55 p-4">
          <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Retry state</div>
          <div class="mt-2 text-sm text-slate-200">${escapeHtml(retry ? `Attempt ${retry.attempt}` : "No retry scheduled")}</div>
          <div class="mt-3 text-xs text-slate-400">${escapeHtml(retry ? `Due ${formatRelativeTime(retry.due_at)}` : "Queue is clear")}</div>
        </div>
      </div>

      <div class="mt-6">
        <div class="flex items-center justify-between gap-4">
          <h3 class="text-sm font-semibold uppercase tracking-[0.24em] text-slate-300">Recent issue events</h3>
          <span class="text-xs text-slate-400">${escapeHtml(formatNumber(recentEvents.length))} rows</span>
        </div>
        <div class="mt-4 space-y-3">
          ${recentEvents.length ? recentEvents.map(entry => `
              <div class="rounded-2xl border border-white/8 bg-white/[0.035] p-4">
                <div class="flex flex-wrap items-center gap-2">
                  <span class="status-chip ${getEventTone(entry)}">${escapeHtml(entry.event)}</span>
                  <span class="text-xs uppercase tracking-[0.22em] text-slate-400">${escapeHtml(formatRelativeTime(entry.at))}</span>
                </div>
                <div class="mt-3 text-sm text-slate-300">${escapeHtml(entry.message || "No message")}</div>
              </div>`).join("") : renderCompactEmpty("No issue events captured yet")}
        </div>
      </div>
    </div>`;
}

function renderInstanceStatus() {
  const runtime = state.runtime;
  const workflow = runtime?.workflow;
  const leases = state.snapshot?.coordination?.leases || [];
  const activeLeases = leases.filter(entry => !entry.is_expired);

  return `
    <div class="panel-body p-6">
      <div class="section-kicker">Instance health</div>
      <h2 class="section-title">Host and coordination</h2>

      <div class="mt-6 grid gap-4">
        <div class="rounded-3xl border border-white/10 bg-white/[0.035] p-5">
          <div class="flex items-center justify-between gap-4">
            <div class="text-sm font-medium text-white">Health</div>
            <span class="status-chip ${getHealthTone()}">${escapeHtml(state.health?.label || "Unknown")}</span>
          </div>
          <div class="mt-3 text-sm text-slate-300">${escapeHtml(state.health?.detail || "No health detail available.")}</div>
        </div>

        <div class="rounded-3xl border border-white/10 bg-white/[0.035] p-5">
          <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Orchestrator</div>
          <div class="mt-2 space-y-2 text-sm text-slate-300">
            <div>Version: ${escapeHtml(runtime?.application?.version || "unknown")}</div>
            <div>Instance: ${escapeHtml(runtime?.orchestration?.instanceId || "auto-generated")}</div>
            <div>Lease: ${escapeHtml(runtime?.orchestration?.leaseName || "poll-dispatch")}</div>
            <div>Lease TTL: ${escapeHtml(formatSeconds(runtime?.orchestration?.leaseTtlSeconds || 0))}</div>
            <div>HTTP port: ${escapeHtml(String(workflow?.server?.port || "configured externally"))}</div>
          </div>
        </div>

        <div class="rounded-3xl border border-white/10 bg-white/[0.035] p-5">
          <div class="text-xs uppercase tracking-[0.22em] text-slate-400">Persistence</div>
          <div class="mt-2 text-sm text-slate-300">${runtime?.persistence?.isConfigured ? "SQLite configured" : "Persistence is not configured"}</div>
          <div class="mt-4 text-xs uppercase tracking-[0.22em] text-slate-400">Lease rows</div>
          <div class="mt-3 space-y-3">
            ${leases.length ? leases.map(lease => `
                <div class="rounded-2xl border ${lease.is_expired ? toneClasses.warning : toneClasses.healthy} p-3">
                  <div class="flex items-center justify-between gap-4 text-sm">
                    <span class="font-medium text-white">${escapeHtml(lease.lease_name)}</span>
                    <span>${escapeHtml(lease.is_expired ? "Expired" : "Active")}</span>
                  </div>
                  <div class="mt-2 text-xs text-slate-300">${escapeHtml(lease.owner_instance_id)} updated ${escapeHtml(formatRelativeTime(lease.updated_at))}</div>
                </div>`).join("") : renderCompactEmpty("No lease data")}
          </div>
          <div class="mt-3 text-xs text-slate-400">${escapeHtml(activeLeases.length ? `${activeLeases.length} active coordination lease(s)` : "Coordination is idle or has not run yet.")}</div>
        </div>
      </div>
    </div>`;
}

function renderRateLimits() {
  const rows = flattenEntries(state.snapshot?.rate_limits);
  return `
    <div class="panel-body p-6">
      <div class="section-kicker">Provider telemetry</div>
      <h2 class="section-title">Rate limits</h2>
      <p class="mt-3 text-sm leading-6 text-slate-300">Latest rate limit payload recorded from Codex app-server updates.</p>
      <div class="mt-6 space-y-3">
        ${rows.length ? rows.map(row => `
            <div class="rounded-2xl border border-white/8 bg-white/[0.035] px-4 py-3">
              <div class="text-xs uppercase tracking-[0.22em] text-slate-400">${escapeHtml(row.key)}</div>
              <div class="mt-1 break-all text-sm text-slate-200">${escapeHtml(row.value)}</div>
            </div>`).join("") : renderEmptyState("No rate-limit payload captured.", "Once Codex reports provider limits, the latest payload will be surfaced here for capacity debugging.")}
      </div>
    </div>`;
}

function renderEmptyState(title, description) {
  return `
    <div class="rounded-3xl border border-dashed border-white/10 bg-white/[0.03] p-5 text-sm text-slate-300">
      <div class="font-medium text-white">${escapeHtml(title)}</div>
      <div class="mt-2 leading-6 text-slate-400">${escapeHtml(description)}</div>
    </div>`;
}

function renderCompactEmpty(message) {
  return `<div class="rounded-2xl border border-dashed border-white/10 bg-white/[0.02] px-4 py-3 text-sm text-slate-400">${escapeHtml(message)}</div>`;
}

function updateDocumentTitle() {
  const owner = state.runtime?.workflow?.tracker?.owner;
  const repo = state.runtime?.workflow?.tracker?.repo;
  const repoLabel = [owner, repo].filter(Boolean).join("/");
  document.title = repoLabel ? `${repoLabel} | ${baseDocumentTitle}` : baseDocumentTitle;
}

function activeLeaseCount(snapshot) {
  return (snapshot?.coordination?.leases || []).filter(entry => !entry.is_expired).length;
}

function collectIssueIdentifiers(snapshot) {
  const identifiers = new Set();
  for (const run of snapshot?.running || []) identifiers.add(run.issue_identifier);
  for (const retry of snapshot?.retrying || []) identifiers.add(retry.issue_identifier);
  for (const issue of snapshot?.tracked?.recently_updated || []) identifiers.add(issue.issue_identifier);
  return [...identifiers];
}

function getIssueFromHash() {
  const hash = window.location.hash || "";
  return hash.startsWith("#issue/") ? decodeURIComponent(hash.slice(7)) : null;
}

async function fetchJson(url) {
  const response = await fetch(url, { cache: "no-store" });
  if (!response.ok) {
    throw new Error(`Request to ${url} failed with ${response.status}.`);
  }

  return response.json();
}

async function fetchHealth() {
  const response = await fetch("/api/v1/health", { cache: "no-store" });
  const detail = (await response.text()).trim();
  return {
    ok: response.ok,
    label: response.ok ? "Healthy" : "Degraded",
    detail: detail || (response.ok ? "Health checks passed." : `Health endpoint returned ${response.status}.`)
  };
}

function scheduleRefresh() {
  if (refreshHandle) {
    window.clearInterval(refreshHandle);
    refreshHandle = null;
  }

  if (state.autoRefresh) {
    refreshHandle = window.setInterval(() => {
      void loadDashboard();
    }, refreshIntervalMs);
  }
}

function getHealthTone() {
  if (!state.health) {
    return state.loading ? toneClasses.info : toneClasses.neutral;
  }

  return state.health.ok ? toneClasses.healthy : toneClasses.danger;
}

function getIssueStatusTone(status) {
  switch ((status || "").toLowerCase()) {
    case "running":
      return toneClasses.info;
    case "retrying":
      return toneClasses.warning;
    case "completed":
    case "succeeded":
      return toneClasses.healthy;
    case "failed":
      return toneClasses.danger;
    default:
      return toneClasses.neutral;
  }
}

function getEventTone(entry) {
  const value = `${entry?.event || ""} ${entry?.level || ""}`.toLowerCase();
  if (value.includes("fail") || value.includes("error") || value.includes("cancel")) return toneClasses.danger;
  if (value.includes("retry") || value.includes("warning")) return toneClasses.warning;
  if (value.includes("complete") || value.includes("closed") || value.includes("success")) return toneClasses.healthy;
  if (value.includes("dispatch") || value.includes("turn") || value.includes("notification")) return toneClasses.info;
  return toneClasses.neutral;
}

function flattenEntries(value, prefix = "") {
  if (value === null || value === undefined) return [];
  if (Array.isArray(value)) {
    return value.length
      ? value.flatMap((entry, index) => flattenEntries(entry, `${prefix}[${index}]`))
      : [{ key: prefix || "value", value: "[]" }];
  }

  if (typeof value === "object") {
    return Object.entries(value).flatMap(([key, nestedValue]) =>
      flattenEntries(nestedValue, prefix ? `${prefix}.${key}` : key));
  }

  return [{ key: prefix || "value", value: String(value) }];
}

function formatNumber(value) {
  return new Intl.NumberFormat().format(Number(value || 0));
}

function formatSeconds(value) {
  const seconds = Number(value || 0);
  if (seconds < 60) return `${seconds.toFixed(seconds >= 10 ? 0 : 1)}s`;
  const minutes = seconds / 60;
  if (minutes < 60) return `${minutes.toFixed(minutes >= 10 ? 0 : 1)}m`;
  const hours = minutes / 60;
  return `${hours.toFixed(hours >= 10 ? 0 : 1)}h`;
}

function formatDurationFromMilliseconds(value) {
  return formatSeconds(Number(value || 0) / 1000);
}

function formatRelativeTime(value) {
  if (!value) return "unavailable";
  const timestamp = new Date(value).getTime();
  if (Number.isNaN(timestamp)) return "unavailable";
  const diffSeconds = Math.round((timestamp - Date.now()) / 1000);
  const absoluteSeconds = Math.abs(diffSeconds);
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
  if (absoluteSeconds < 60) return formatter.format(diffSeconds, "second");
  const diffMinutes = Math.round(diffSeconds / 60);
  if (Math.abs(diffMinutes) < 60) return formatter.format(diffMinutes, "minute");
  const diffHours = Math.round(diffMinutes / 60);
  if (Math.abs(diffHours) < 48) return formatter.format(diffHours, "hour");
  return formatter.format(Math.round(diffHours / 24), "day");
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}

function escapeAttribute(value) {
  return escapeHtml(value);
}
