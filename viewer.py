#!/usr/bin/env python3
import json
import os
import re
import urllib.parse
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Optional

DEFAULT_SESSIONS_DIR = Path.home() / '.codex' / 'sessions'
WINDOWS_USERS_DIR = Path('/mnt/c/Users')
HOST = os.getenv('HOST', '127.0.0.1')
PORT = 8765
MAX_LIST = 300
MAX_EVENTS = 2000
SEARCH_TEXT_LIMIT = 50000
_CACHED_SESSIONS_DIR = None
_CACHED_SESSION_ROOTS = None


def is_wsl() -> bool:
    if os.getenv('WSL_DISTRO_NAME'):
        return True
    try:
        return 'microsoft' in Path('/proc/version').read_text(encoding='utf-8').lower()
    except Exception:
        return False


def windows_path_to_wsl(path_str: str) -> Optional[Path]:
    m = re.match(r'^([A-Za-z]):[\\/](.*)$', path_str)
    if not m:
        return None
    drive = m.group(1).lower()
    rest = m.group(2).replace('\\', '/').lstrip('/')
    return Path('/mnt') / drive / rest


def normalize_sessions_dir(path_str: str) -> Path:
    p = Path(path_str).expanduser()
    converted = windows_path_to_wsl(path_str)
    if converted is not None and not p.exists():
        return converted
    return p


def discover_wsl_windows_sessions_dirs():
    candidates = []
    users = []
    for key in ('WIN_USERNAME', 'USERNAME'):
        value = os.getenv(key, '').strip()
        if value:
            users.append(value)
    for user in users:
        candidates.append(WINDOWS_USERS_DIR / user / '.codex' / 'sessions')
    if WINDOWS_USERS_DIR.exists():
        for user_dir in sorted(WINDOWS_USERS_DIR.iterdir(), key=lambda p: p.name.lower()):
            if user_dir.is_dir():
                candidates.append(user_dir / '.codex' / 'sessions')
    seen = set()
    ordered = []
    for candidate in candidates:
        key = str(candidate)
        if key not in seen:
            seen.add(key)
            ordered.append(candidate)
    return ordered


def _unique_paths(paths):
    seen = set()
    ordered = []
    for path in paths:
        key = str(path)
        if key not in seen:
            seen.add(key)
            ordered.append(path)
    return ordered


def get_session_roots():
    global _CACHED_SESSION_ROOTS
    if _CACHED_SESSION_ROOTS is not None:
        return _CACHED_SESSION_ROOTS

    raw = os.getenv('SESSIONS_DIR')
    if raw:
        _CACHED_SESSION_ROOTS = [normalize_sessions_dir(raw)]
        return _CACHED_SESSION_ROOTS

    candidates = [DEFAULT_SESSIONS_DIR]
    if is_wsl():
        candidates.extend(discover_wsl_windows_sessions_dirs())
    candidates = _unique_paths(candidates)

    existing = [p for p in candidates if p.exists()]
    _CACHED_SESSION_ROOTS = existing if existing else candidates
    return _CACHED_SESSION_ROOTS


def get_sessions_dir() -> Path:
    global _CACHED_SESSIONS_DIR
    if _CACHED_SESSIONS_DIR is not None:
        return _CACHED_SESSIONS_DIR

    roots = get_session_roots()
    _CACHED_SESSIONS_DIR = roots[0] if roots else DEFAULT_SESSIONS_DIR
    return _CACHED_SESSIONS_DIR


def iter_session_files(root: Path):
    if not root.exists():
        return []
    return sorted(root.rglob('*.jsonl'), key=lambda p: p.stat().st_mtime, reverse=True)


def iter_all_session_files(roots):
    files = []
    for root in roots:
        files.extend(iter_session_files(root))
    unique = {}
    for path in files:
        unique[str(path)] = path
    return sorted(unique.values(), key=lambda p: p.stat().st_mtime, reverse=True)


def classify_source(raw_source: str, originator: str) -> str:
    source = (raw_source or '').strip().lower()
    if source in ('cli', 'vscode'):
        return source
    origin = (originator or '').strip().lower()
    if 'vscode' in source or 'vscode' in origin:
        return 'vscode'
    if 'cli' in source or 'cli' in origin:
        return 'cli'
    return 'cli'


def to_relative_path(path: Path) -> str:
    for root in get_session_roots():
        try:
            return str(path.relative_to(root))
        except Exception:
            pass
    return str(path)


def summarize_session(path: Path):
    summary = {
        'id': path.stem,
        'path': str(path),
        'relative_path': str(path),
        'mtime': datetime.fromtimestamp(path.stat().st_mtime).isoformat(),
        'session_id': '',
        'started_at': '',
        'cwd': '',
        'model': '',
        'source': 'cli',
        'first_user_text': '',
        'first_real_user_text': '',
        'search_text': '',
        'first_event_at': '',
        'last_event_at': '',
    }
    search_chunks = []
    search_len = 0
    search_limit = SEARCH_TEXT_LIMIT
    summary['relative_path'] = to_relative_path(path)
    skip_detail_parsing = False

    try:
        with path.open('r', encoding='utf-8') as f:
            for line in f:
                obj = json.loads(line)
                ts = obj.get('timestamp', '')
                if ts:
                    if not summary['first_event_at']:
                        summary['first_event_at'] = ts
                    summary['last_event_at'] = ts
                if skip_detail_parsing:
                    continue
                t = obj.get('type')
                payload = obj.get('payload', {})
                if t == 'session_meta':
                    summary['session_id'] = payload.get('id', '')
                    summary['started_at'] = payload.get('timestamp', '')
                    summary['cwd'] = payload.get('cwd', '')
                    summary['model'] = payload.get('model_provider', '')
                    summary['source'] = classify_source(payload.get('source', ''), payload.get('originator', ''))
                elif t == 'response_item':
                    if payload.get('type') == 'message':
                        chunk = extract_text_from_content(payload.get('content', []))
                        if chunk and search_len < search_limit:
                            cut = chunk.replace('\n', ' ')[:300]
                            search_chunks.append(cut)
                            search_len += len(cut)
                    if payload.get('role') == 'user' and not summary['first_user_text']:
                        content = payload.get('content', [])
                        for item in content:
                            text = item.get('text', '') if isinstance(item, dict) else ''
                            if text:
                                summary['first_user_text'] = text.strip().replace('\n', ' ')[:120]
                                break
                    if payload.get('role') == 'user' and not summary['first_real_user_text']:
                        content = payload.get('content', [])
                        raw = ''
                        for item in content:
                            text = item.get('text', '') if isinstance(item, dict) else ''
                            if text:
                                raw = text.strip()
                                break
                        if raw and classify_user_message(raw) == 'user':
                            summary['first_real_user_text'] = raw.replace('\n', ' ')[:160]
                if summary['started_at'] and summary['first_user_text']:
                    if summary['first_real_user_text'] and search_len >= search_limit:
                        skip_detail_parsing = True
    except Exception:
        pass
    if not summary['first_real_user_text']:
        summary['first_real_user_text'] = summary['first_user_text']
    summary['search_text'] = ' '.join(search_chunks)
    return summary


def extract_text_from_content(content):
    texts = []
    if isinstance(content, list):
        for item in content:
            if isinstance(item, dict):
                t = item.get('text')
                if t:
                    texts.append(t)
    elif isinstance(content, str):
        texts.append(content)
    return '\n'.join(texts).strip()


def classify_user_message(text: str) -> str:
    lower = text.lower()
    context_markers = [
        '# agents.md instructions',
        '<environment_context>',
        '<collaboration_mode>',
        '<permissions instructions>',
    ]
    for marker in context_markers:
        if marker in lower:
            return 'user_context'
    return 'user'


def load_session_events(path: Path):
    events = []
    raw_count = 0
    with path.open('r', encoding='utf-8') as f:
        for line in f:
            raw_count += 1
            obj = json.loads(line)
            t = obj.get('type')
            ts = obj.get('timestamp', '')
            payload = obj.get('payload', {})

            if t == 'response_item':
                p_type = payload.get('type')
                if p_type == 'message':
                    role = payload.get('role', 'unknown')
                    text = extract_text_from_content(payload.get('content', []))
                    if text:
                        if role == 'user':
                            role = classify_user_message(text)
                        events.append({'timestamp': ts, 'kind': 'message', 'role': role, 'text': text})
                elif p_type == 'function_call':
                    events.append({
                        'timestamp': ts,
                        'kind': 'function_call',
                        'name': payload.get('name', ''),
                        'arguments': payload.get('arguments', ''),
                    })
                elif p_type == 'function_call_output':
                    events.append({
                        'timestamp': ts,
                        'kind': 'function_output',
                        'call_id': payload.get('call_id', ''),
                        'output': payload.get('output', ''),
                    })
            elif t == 'event_msg':
                p_type = payload.get('type')
                if p_type == 'agent_message':
                    events.append({
                        'timestamp': ts,
                        'kind': 'agent_update',
                        'text': payload.get('message', ''),
                    })

            if len(events) >= MAX_EVENTS:
                break

    return {'events': events, 'raw_line_count': raw_count}


HTML_PAGE = """<!doctype html>
<html lang=\"ja\">
<head>
<meta charset=\"utf-8\" />
<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />
<title>Codex Sessions Viewer</title>
<style>
:root {
  --bg: #f2f6fb;
  --panel: #ffffff;
  --line: #ccd8e4;
  --text: #18232f;
  --muted: #57697c;
  --accent: #0d6d77;
  --user: #1b5fd6;
  --assistant: #0f7c4f;
  --dev: #8a5a00;
  --system: #4b5563;
}
* { box-sizing: border-box; }
html, body { height: 100%; }
body {
  margin: 0;
  font-family: "Segoe UI", "Yu Gothic UI", sans-serif;
  background: radial-gradient(circle at top right, #e6f4ff 0%, var(--bg) 45%);
  color: var(--text);
  overflow: hidden;
}
header {
  padding: 14px 16px;
  border-bottom: 1px solid var(--line);
  background: rgba(255,255,255,0.9);
  backdrop-filter: blur(4px);
}
header h1 { margin: 0; font-size: 18px; }
header small { color: var(--muted); }
.container {
  display: grid;
  grid-template-columns: 360px 1fr;
  height: calc(100vh - 64px);
  overflow: hidden;
}
.left {
  border-right: 1px solid var(--line);
  background: #f9fcff;
  display: flex;
  flex-direction: column;
  min-height: 0;
}
.toolbar {
  padding: 10px;
  border-bottom: 1px solid var(--line);
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  align-items: center;
}
input, select, button {
  border: 1px solid var(--line);
  border-radius: 8px;
  padding: 8px 10px;
  font-size: 13px;
}
#cwd_q, #q { flex: 1 1 220px; }
#date_from, #date_to { flex: 1 1 185px; }
#event_date_from, #event_date_to { flex: 1 1 185px; }
#event_date_from_detail, #event_date_to_detail { flex: 0 1 185px; }
#mode { flex: 0 0 auto; }
button {
  background: var(--accent);
  color: #fff;
  cursor: pointer;
  white-space: nowrap;
}
#sessions {
  overflow: auto;
  flex: 1;
}
.session-item {
  padding: 10px 12px;
  border-bottom: 1px solid #e7eef6;
  cursor: pointer;
}
.session-item:hover { background: #eef7ff; }
.session-item.active { background: #dff0ff; }
.session-path {
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.session-cwd {
  color: #0b5f3d;
  font-weight: 700;
  background: #e8f7ef;
  border: 1px solid #bfe8cf;
  border-radius: 6px;
  padding: 2px 6px;
  display: inline-block;
  max-width: 100%;
}
.session-time {
  color: #6b4300;
  font-weight: 700;
  background: #fff3de;
  border: 1px solid #f0d3a1;
  border-radius: 6px;
  padding: 2px 6px;
  display: inline-block;
  max-width: 100%;
  font-variant-numeric: tabular-nums;
}
.session-path .ts {
  color: #0b4a52;
  font-weight: 600;
  background: #dff5f8;
  border-radius: 4px;
  padding: 0 4px;
}
.session-preview {
  margin-top: 4px;
  font-size: 12px;
  color: #34414f;
}
.session-meta-row {
  margin-top: 6px;
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.session-badge {
  display: inline-block;
  font-size: 11px;
  border-radius: 6px;
  padding: 2px 6px;
  border: 1px solid #c7d8ea;
  background: #f2f8ff;
}
.session-id {
  color: #334155;
  background: #eef2f7;
  border-color: #d4dde8;
}
.session-source {
  color: #0b3a67;
  background: #e6f1ff;
  border-color: #bdd9f7;
  font-weight: 700;
}
.session-source.source-vscode {
  color: #0f5a5a;
  background: #e5f7f7;
  border-color: #bfe8e8;
}
.session-source.source-cli {
  color: #0b3a67;
  background: #e6f1ff;
  border-color: #bdd9f7;
}
.right {
  background: var(--panel);
  display: flex;
  flex-direction: column;
  min-height: 0;
}
.meta {
  padding: 12px;
  border-bottom: 1px solid var(--line);
  font-size: 13px;
  color: var(--muted);
}
.meta code.path-code {
  color: #0b4a52;
  background: #e5f4f6;
  border: 1px solid #b8dee3;
  padding: 2px 6px;
  border-radius: 6px;
  font-weight: 700;
}
.meta code.cwd-code {
  color: #0b5f3d;
  background: #e8f7ef;
  border: 1px solid #bfe8cf;
  padding: 2px 6px;
  border-radius: 6px;
  font-weight: 700;
}
.meta .ts {
  color: #6b4300;
  font-weight: 700;
  background: #fff3de;
  border-radius: 4px;
  padding: 0 4px;
}
.meta code.time-code {
  color: #6b4300;
  background: #fff3de;
  border: 1px solid #f0d3a1;
  padding: 2px 6px;
  border-radius: 6px;
  font-weight: 700;
}
.meta code.source-code {
  border: 1px solid #bdd9f7;
  padding: 2px 6px;
  border-radius: 6px;
  font-weight: 700;
}
.meta code.source-code.source-vscode {
  color: #0f5a5a;
  background: #e5f7f7;
  border-color: #bfe8e8;
}
.meta code.source-code.source-cli {
  color: #0b3a67;
  background: #e6f1ff;
  border-color: #bdd9f7;
}
.detail-toolbar {
  padding: 10px 12px;
  border-bottom: 1px solid var(--line);
  display: flex;
  gap: 14px;
  flex-wrap: wrap;
  align-items: center;
  background: #f8fbff;
}
.detail-toolbar label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: #324255;
  user-select: none;
}
.detail-toolbar #copy_resume_command {
  background: #0f766e;
}
.detail-toolbar #copy_resume_command:disabled {
  background: #94a3b8;
  cursor: not-allowed;
}
.detail-toolbar #refresh_detail {
  background: #1d4ed8;
}
.detail-toolbar #refresh_detail:disabled {
  background: #94a3b8;
  cursor: not-allowed;
}
#events {
  padding: 14px;
  overflow: auto;
  flex: 1;
}
.ev {
  border: 1px solid var(--line);
  border-left-width: 5px;
  border-radius: 10px;
  padding: 10px;
  margin-bottom: 10px;
  background: #fbfdff;
}
.ev.user { border-left-color: var(--user); background: #e7f1ff; }
.ev.user_context { border-left-color: #7f8ea0; background: #f5f7fa; }
.ev.assistant { border-left-color: var(--assistant); background: #e8f8f0; }
.ev.developer { border-left-color: var(--dev); background: #fff4e2; }
.ev.system { border-left-color: var(--system); background: #f0f3f7; }
.ev-head {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  margin-bottom: 8px;
}
.badge {
  font-size: 11px;
  line-height: 1;
  border-radius: 999px;
  border: 1px solid transparent;
  padding: 4px 7px;
  font-weight: 700;
  letter-spacing: 0.01em;
}
.badge-kind {
  color: #1f2937;
  background: #eef2f7;
  border-color: #d4dce6;
}
.badge-time {
  color: #364152;
  background: #f8fafc;
  border-color: #dfe7f0;
  font-variant-numeric: tabular-nums;
}
.ev.user .badge-role {
  color: #0a3b96;
  background: #d7e8ff;
  border-color: #9dc3ff;
}
.ev.user_context .badge-role {
  color: #334155;
  background: #e5e9ef;
  border-color: #c7d0da;
}
.ev.assistant .badge-role {
  color: #0c5f3c;
  background: #d7f3e4;
  border-color: #99ddba;
}
.ev.developer .badge-role {
  color: #7a4b00;
  background: #ffe7bf;
  border-color: #f4c97f;
}
.ev.system .badge-role {
  color: #374151;
  background: #e3e9f0;
  border-color: #c0ccd9;
}
pre {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 13px;
  line-height: 1.5;
  overflow-wrap: anywhere;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace;
  background: rgba(255, 255, 255, 0.65);
  border: 1px solid rgba(148, 163, 184, 0.32);
  border-radius: 8px;
  padding: 10px 12px;
}
@media (max-width: 900px) {
  .container {
    grid-template-columns: 1fr;
    grid-template-rows: 40vh 1fr;
  }
}
</style>
</head>
<body>
<header>
  <h1>Codex Sessions Viewer</h1>
  <small id="root"></small>
</header>
<div class="container">
  <aside class="left">
    <div class="toolbar">
      <input id="cwd_q" placeholder="cwd (部分一致)" />
      <input id="date_from" type="date" title="セッション開始日 From" />
      <input id="date_to" type="date" title="セッション開始日 To" />
      <input id="event_date_from" type="datetime-local" title="イベント日時 From" placeholder="イベント日時 From" />
      <input id="event_date_to" type="datetime-local" title="イベント日時 To" placeholder="イベント日時 To" />
      <input id="q" placeholder="keyword filter" />
      <select id="mode">
        <option value="and">keyword AND</option>
        <option value="or">keyword OR</option>
      </select>
      <select id="source_filter">
        <option value="all">source: all</option>
        <option value="cli">source: CLI</option>
        <option value="vscode">source: VS Code</option>
      </select>
      <button id="reload">Reload</button>
      <button id="clear">Clear</button>
    </div>
    <div id="sessions"></div>
  </aside>
  <main class="right">
    <div class="meta" id="meta">セッションを選択してください</div>
    <div class="detail-toolbar">
      <label><input type="checkbox" id="only_user_instruction" /> ユーザー指示のみ表示</label>
      <label><input type="checkbox" id="only_ai_response" /> AIレスポンスのみ表示</label>
      <label><input type="checkbox" id="reverse_order" /> 表示順を逆にする</label>
      <input id="event_date_from_detail" type="datetime-local" title="イベント日時 From" />
      <input id="event_date_to_detail" type="datetime-local" title="イベント日時 To" />
      <button id="refresh_detail" disabled>Refresh</button>
      <button id="copy_resume_command" disabled>セッション再開コマンドコピー</button>
    </div>
    <div id="events"></div>
  </main>
</div>
<script>
const state = {
  sessions: [],
  filtered: [],
  activePath: null,
  activeSession: null,
  activeEvents: [],
  activeRawLineCount: 0,
};

const FILTER_STORAGE_KEY = 'codex_sessions_viewer_filters_v1';

function esc(s){
  return (s ?? '').toString().replace(/[&<>\"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',"'":'&#39;'}[c]));
}

function highlightSessionPath(s){
  const safe = esc(s);
  return safe.replace(/(\\d{4}-\\d{2}-\\d{2}T\\d{2}[-:]\\d{2}[-:]\\d{2}(?:[-:]\\d{3,6})?)/g, '<span class="ts">$1</span>');
}

function normalizeSource(source){
  const raw = (source || '').toLowerCase();
  return raw === 'vscode' ? 'vscode' : 'cli';
}

function sourceLabel(source){
  const key = normalizeSource(source);
  return key === 'vscode' ? 'VS Code' : 'CLI';
}

function normalizeSourceFilter(source){
  const raw = (source || '').toLowerCase();
  if(raw === 'all') return 'all';
  return normalizeSource(raw);
}

function fmt(ts){
  if(!ts) return '';
  const d = new Date(ts);
  return isNaN(d) ? ts : d.toLocaleString();
}

function toTimestamp(ts){
  if(!ts) return NaN;
  const d = new Date(ts);
  return d.getTime();
}

function parseOptionalDateStart(raw){
  if(!raw) return null;
  // raw is expected as YYYY-MM-DD from <input type="date">.
  const ts = toTimestamp(`${raw}T00:00:00`);
  return Number.isNaN(ts) ? null : ts;
}

function parseOptionalDateEnd(raw){
  if(!raw) return null;
  // Inclusive end-of-day for date-range filtering.
  const ts = toTimestamp(`${raw}T23:59:59.999`);
  return Number.isNaN(ts) ? null : ts;
}

function parseOptionalDateTimeStart(raw){
  if(!raw) return null;
  const ts = toTimestamp(raw);
  return Number.isNaN(ts) ? null : ts;
}

function parseOptionalDateTimeEnd(raw){
  if(!raw) return null;
  const ts = toTimestamp(raw);
  if(Number.isNaN(ts)) return null;
  // datetime-local gives YYYY-MM-DDTHH:MM (16 chars); include rest of that minute (+59s 999ms)
  if(raw.length <= 16) return ts + 59999;
  return ts;
}

function getActiveSessionId(){
  if(!state.activeSession) return '';
  return (state.activeSession.session_id || state.activeSession.id || '').toString().trim();
}

function updateCopyResumeButtonState(){
  const button = document.getElementById('copy_resume_command');
  button.disabled = !getActiveSessionId();
}

function updateRefreshDetailButtonState(){
  const button = document.getElementById('refresh_detail');
  button.disabled = !state.activePath;
}

async function copyResumeCommand(){
  const sessionId = getActiveSessionId();
  if(!sessionId) return;

  const commandText = 'codex resume ' + sessionId;
  let copied = false;
  try {
    if(navigator.clipboard && navigator.clipboard.writeText){
      await navigator.clipboard.writeText(commandText);
      copied = true;
    }
  } catch (e) {
    copied = false;
  }

  if(!copied){
    const helper = document.createElement('textarea');
    helper.value = commandText;
    helper.setAttribute('readonly', '');
    helper.style.position = 'fixed';
    helper.style.opacity = '0';
    document.body.appendChild(helper);
    helper.select();
    try {
      copied = document.execCommand('copy');
    } finally {
      document.body.removeChild(helper);
    }
  }

  if(copied){
    const button = document.getElementById('copy_resume_command');
    const original = button.textContent;
    button.textContent = 'コピーしました';
    setTimeout(() => {
      button.textContent = original;
    }, 1200);
  }
}

async function loadSessions(){
  const r = await fetch('/api/sessions?ts=' + Date.now(), { cache: 'no-store' });
  const data = await r.json();
  state.sessions = data.sessions;
  document.getElementById('root').textContent = data.root;
  applyFilter();
  if(state.activePath){
    const exists = state.sessions.some(s => s.path === state.activePath);
    if(exists){
      await openSession(state.activePath);
    } else {
      state.activePath = null;
      state.activeSession = null;
      state.activeEvents = [];
      state.activeRawLineCount = 0;
      renderSessionList();
      renderActiveSession();
    }
  }
}

function saveFilters(){
  const payload = {
    cwd_q: document.getElementById('cwd_q').value,
    date_from: document.getElementById('date_from').value,
    date_to: document.getElementById('date_to').value,
    event_date_from: document.getElementById('event_date_from').value,
    event_date_to: document.getElementById('event_date_to').value,
    q: document.getElementById('q').value,
    mode: document.getElementById('mode').value,
    source_filter: document.getElementById('source_filter').value,
  };
  try {
    localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify(payload));
  } catch (e) {
    // Ignore storage write errors.
  }
}

function restoreFilters(){
  let raw = null;
  try {
    raw = localStorage.getItem(FILTER_STORAGE_KEY);
  } catch (e) {
    raw = null;
  }
  if(!raw) return;
  try {
    const data = JSON.parse(raw);
    if(typeof data.cwd_q === 'string') document.getElementById('cwd_q').value = data.cwd_q;
    if(typeof data.date_from === 'string') document.getElementById('date_from').value = data.date_from;
    if(typeof data.date_to === 'string') document.getElementById('date_to').value = data.date_to;
    if(typeof data.event_date_from === 'string') document.getElementById('event_date_from').value = data.event_date_from;
    if(typeof data.event_date_to === 'string') document.getElementById('event_date_to').value = data.event_date_to;
    if(typeof data.q === 'string') document.getElementById('q').value = data.q;
    if(data.mode === 'and' || data.mode === 'or') document.getElementById('mode').value = data.mode;
    const source = normalizeSourceFilter(data.source_filter || 'all');
    document.getElementById('source_filter').value = source;
  } catch (e) {
    // Ignore invalid saved filters.
  }
}

function clearFilters(){
  document.getElementById('cwd_q').value = '';
  document.getElementById('date_from').value = '';
  document.getElementById('date_to').value = '';
  document.getElementById('event_date_from').value = '';
  document.getElementById('event_date_to').value = '';
  document.getElementById('q').value = '';
  document.getElementById('mode').value = 'and';
  document.getElementById('source_filter').value = 'all';
  try {
    localStorage.removeItem(FILTER_STORAGE_KEY);
  } catch (e) {
    // Ignore storage delete errors.
  }
  applyFilter();
}

function applyFilter(){
  const cwdQ = document.getElementById('cwd_q').value.toLowerCase().trim();
  const q = document.getElementById('q').value.toLowerCase().trim();
  const sourceFilter = normalizeSourceFilter(document.getElementById('source_filter').value || 'all');
  const fromRaw = document.getElementById('date_from').value;
  const toRaw = document.getElementById('date_to').value;
  const fromTs = parseOptionalDateStart(fromRaw);
  const toTs = parseOptionalDateEnd(toRaw);
  const eventFromRaw = document.getElementById('event_date_from').value;
  const eventToRaw = document.getElementById('event_date_to').value;
  const eventFromTs = parseOptionalDateTimeStart(eventFromRaw);
  const eventToTs = parseOptionalDateTimeEnd(eventToRaw);
  const mode = document.getElementById('mode').value;
  const terms = q.split(/\\s+/).filter(Boolean);
  state.filtered = state.sessions.filter(s => {
    const cwdMatched = !cwdQ || (s.cwd || '').toLowerCase().includes(cwdQ);
    const sourceMatched = sourceFilter === 'all' || normalizeSource(s.source) === sourceFilter;

    let dateMatched = true;
    if(fromTs !== null || toTs !== null){
      const sessionTs = toTimestamp(s.started_at || s.mtime);
      if(Number.isNaN(sessionTs)){
        dateMatched = false;
      } else {
        if(fromTs !== null && sessionTs < fromTs){
          dateMatched = false;
        }
        if(toTs !== null && sessionTs > toTs){
          dateMatched = false;
        }
      }
    }

    let eventDateMatched = true;
    if(eventFromTs !== null || eventToTs !== null){
      const firstTs = toTimestamp(s.first_event_at);
      const lastTs = toTimestamp(s.last_event_at);
      if(Number.isNaN(firstTs) || Number.isNaN(lastTs)){
        eventDateMatched = false;
      } else {
        if(eventFromTs !== null && lastTs < eventFromTs){
          eventDateMatched = false;
        }
        if(eventToTs !== null && firstTs > eventToTs){
          eventDateMatched = false;
        }
      }
    }

    let keywordMatched = true;
    if(terms.length > 0){
      const target = (
        s.relative_path + ' ' +
        (s.first_real_user_text || '') + ' ' +
        (s.first_user_text || '') + ' ' +
        (s.search_text || '')
      ).toLowerCase();
      if(mode === 'or'){
        keywordMatched = terms.some(t => target.includes(t));
      } else {
        keywordMatched = terms.every(t => target.includes(t));
      }
    }

    return cwdMatched && sourceMatched && dateMatched && eventDateMatched && keywordMatched;
  });
  saveFilters();
  renderSessionList();
}

function renderSessionList(){
  const box = document.getElementById('sessions');
  box.innerHTML = state.filtered.map(s => `
    <div class="session-item ${state.activePath === s.path ? 'active' : ''}" data-path="${esc(s.path)}">
      <div class="session-path">${highlightSessionPath(s.relative_path)}</div>
      <div class="session-preview">${esc(s.first_real_user_text || s.first_user_text || '(previewなし)')}</div>
      <div class="session-meta-row">
        <div class="session-badge session-time">${esc(fmt(s.started_at || s.mtime))}</div>
        <div class="session-badge session-source source-${esc(normalizeSource(s.source))}">${esc(sourceLabel(s.source))}</div>
      </div>
      <div class="session-meta-row">
        <div class="session-badge session-cwd">cwd: ${esc(s.cwd || '-')}</div>
        <div class="session-badge session-id">id: ${esc(s.session_id || s.id || '')}</div>
      </div>
    </div>
  `).join('');
  box.querySelectorAll('.session-item').forEach(el => {
    el.onclick = () => openSession(el.dataset.path);
  });
}

function getDisplayEvents(){
  let events = state.activeEvents || [];
  const showOnlyUser = document.getElementById('only_user_instruction').checked;
  const showOnlyAssistant = document.getElementById('only_ai_response').checked;
  if(showOnlyUser || showOnlyAssistant){
    events = events.filter(ev => {
      if(ev.kind !== 'message') return false;
      return (showOnlyUser && ev.role === 'user') || (showOnlyAssistant && ev.role === 'assistant');
    });
  }
  const eventFromRaw = document.getElementById('event_date_from_detail').value;
  const eventToRaw = document.getElementById('event_date_to_detail').value;
  const eventFromTs = parseOptionalDateTimeStart(eventFromRaw);
  const eventToTs = parseOptionalDateTimeEnd(eventToRaw);
  if(eventFromTs !== null || eventToTs !== null){
    events = events.filter(ev => {
      const evTs = toTimestamp(ev.timestamp);
      if(Number.isNaN(evTs)) return false;
      if(eventFromTs !== null && evTs < eventFromTs) return false;
      if(eventToTs !== null && evTs > eventToTs) return false;
      return true;
    });
  }
  if(document.getElementById('reverse_order').checked){
    events = [...events].reverse();
  }
  return events;
}

function renderActiveSession(){
  const meta = document.getElementById('meta');
  const eventsBox = document.getElementById('events');
  updateRefreshDetailButtonState();
  if(!state.activeSession){
    meta.textContent = 'セッションを選択してください';
    eventsBox.innerHTML = '';
    updateCopyResumeButtonState();
    return;
  }

  const displayEvents = getDisplayEvents();
  const source = normalizeSource(state.activeSession.source);
  meta.innerHTML =
    `path: <code class="path-code">${highlightSessionPath(state.activeSession.relative_path)}</code> | cwd: <code class="cwd-code">${esc(state.activeSession.cwd || '-')}</code> | time: <code class="time-code">${esc(fmt(state.activeSession.started_at || state.activeSession.mtime))}</code> | source: <code class="source-code source-${esc(source)}">${esc(sourceLabel(source))}</code> | events: ${displayEvents.length}/${state.activeEvents.length} | raw lines: ${state.activeRawLineCount}`;

  eventsBox.innerHTML = displayEvents.map(ev => {
    const role = ev.role || 'system';
    const roleLabel = role.replace('_', ' ');
    let body = '';
    if(ev.kind === 'message' || ev.kind === 'agent_update'){
      body = `<pre>${esc(ev.text || '')}</pre>`;
    } else if(ev.kind === 'function_call'){
      body = `<pre>name: ${esc(ev.name)}\n${esc(ev.arguments || '')}</pre>`;
    } else if(ev.kind === 'function_output'){
      body = `<pre>${esc(ev.output || '')}</pre>`;
    } else {
      body = `<pre>${esc(JSON.stringify(ev, null, 2))}</pre>`;
    }
    return `<div class="ev ${role}"><div class="ev-head"><span class="badge badge-kind">${esc(ev.kind || 'event')}</span><span class="badge badge-role">${esc(roleLabel)}</span><span class="badge badge-time">${esc(fmt(ev.timestamp))}</span></div>${body}</div>`;
  }).join('');
  updateCopyResumeButtonState();
}

async function openSession(path){
  state.activePath = path;
  renderSessionList();
  const r = await fetch('/api/session?path=' + encodeURIComponent(path) + '&ts=' + Date.now(), { cache: 'no-store' });
  const data = await r.json();
  if(data.error){
    state.activeSession = null;
    state.activeEvents = [];
    state.activeRawLineCount = 0;
    document.getElementById('meta').textContent = data.error;
    document.getElementById('events').innerHTML = '';
    updateRefreshDetailButtonState();
    updateCopyResumeButtonState();
    return;
  }
  state.activeSession = data.session;
  state.activeEvents = data.events || [];
  state.activeRawLineCount = data.raw_line_count || 0;
  renderActiveSession();
}

async function refreshActiveSession(){
  if(!state.activePath) return;
  await openSession(state.activePath);
}

document.getElementById('cwd_q').addEventListener('input', applyFilter);
document.getElementById('date_from').addEventListener('change', applyFilter);
document.getElementById('date_to').addEventListener('change', applyFilter);
document.getElementById('event_date_from').addEventListener('change', applyFilter);
document.getElementById('event_date_to').addEventListener('change', applyFilter);
document.getElementById('q').addEventListener('input', applyFilter);
document.getElementById('mode').addEventListener('change', applyFilter);
document.getElementById('source_filter').addEventListener('change', applyFilter);
document.getElementById('reload').addEventListener('click', loadSessions);
document.getElementById('clear').addEventListener('click', clearFilters);
document.getElementById('only_user_instruction').addEventListener('change', renderActiveSession);
document.getElementById('only_ai_response').addEventListener('change', renderActiveSession);
document.getElementById('reverse_order').addEventListener('change', renderActiveSession);
document.getElementById('event_date_from_detail').addEventListener('change', renderActiveSession);
document.getElementById('event_date_to_detail').addEventListener('change', renderActiveSession);
document.getElementById('refresh_detail').addEventListener('click', refreshActiveSession);
document.getElementById('copy_resume_command').addEventListener('click', copyResumeCommand);
updateCopyResumeButtonState();
updateRefreshDetailButtonState();
restoreFilters();
loadSessions();
</script>
</body>
</html>
"""


class Handler(BaseHTTPRequestHandler):
    def _send_raw(self, raw, content_type, status=200):
        try:
            self.send_response(status)
            self.send_header('Content-Type', content_type)
            self.send_header('Content-Length', str(len(raw)))
            self.end_headers()
            self.wfile.write(raw)
            return True
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            return False
        except OSError as e:
            # Ignore common disconnect errors from clients closing tabs/reloading.
            if getattr(e, 'errno', None) in {32, 104}:
                return False
            raise

    def _send_json(self, data, status=200):
        raw = json.dumps(data, ensure_ascii=False).encode('utf-8')
        self._send_raw(raw, 'application/json; charset=utf-8', status)

    def _send_html(self, text, status=200):
        raw = text.encode('utf-8')
        self._send_raw(raw, 'text/html; charset=utf-8', status)

    def log_message(self, fmt, *args):
        return

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == '/':
            self._send_html(HTML_PAGE)
            return

        if parsed.path == '/api/sessions':
            roots = get_session_roots()
            files = iter_all_session_files(roots)[:MAX_LIST]
            sessions = [summarize_session(p) for p in files]
            self._send_json({'root': ' | '.join(str(x) for x in roots), 'sessions': sessions})
            return

        if parsed.path == '/api/session':
            roots = [x.resolve() for x in get_session_roots()]
            q = urllib.parse.parse_qs(parsed.query)
            raw_path = q.get('path', [''])[0]
            if not raw_path:
                self._send_json({'error': 'path is required'}, 400)
                return
            p = Path(raw_path).expanduser().resolve()
            is_under_known_root = False
            for root in roots:
                try:
                    p.relative_to(root)
                    is_under_known_root = True
                    break
                except Exception:
                    continue
            if not is_under_known_root:
                self._send_json({'error': 'path is outside sessions dir'}, 400)
                return
            if not p.exists() or not p.is_file():
                self._send_json({'error': 'session file not found'}, 404)
                return
            session = summarize_session(p)
            data = load_session_events(p)
            data['session'] = session
            self._send_json(data)
            return

        self._send_html('<h1>404</h1>', 404)


def main():
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    print(f'Viewer: http://{HOST}:{PORT}')
    for root in get_session_roots():
        print(f'Sessions dir: {root}')
    server.serve_forever()


if __name__ == '__main__':
    main()
