#!/usr/bin/env python3
import json
import os
import urllib.parse
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

DEFAULT_SESSIONS_DIR = Path.home() / '.codex' / 'sessions'
HOST = '127.0.0.1'
PORT = 8765
MAX_LIST = 300
MAX_EVENTS = 2000


def get_sessions_dir() -> Path:
    raw = os.getenv('SESSIONS_DIR')
    if raw:
        return Path(raw).expanduser()
    return DEFAULT_SESSIONS_DIR


def iter_session_files(root: Path):
    if not root.exists():
        return []
    return sorted(root.rglob('*.jsonl'), key=lambda p: p.stat().st_mtime, reverse=True)


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
        'first_user_text': '',
        'first_real_user_text': '',
        'search_text': '',
    }
    search_chunks = []
    search_len = 0
    search_limit = 2000
    try:
        summary['relative_path'] = str(path.relative_to(get_sessions_dir()))
    except Exception:
        pass

    try:
        with path.open('r', encoding='utf-8') as f:
            for line in f:
                obj = json.loads(line)
                t = obj.get('type')
                payload = obj.get('payload', {})
                if t == 'session_meta':
                    summary['session_id'] = payload.get('id', '')
                    summary['started_at'] = payload.get('timestamp', '')
                    summary['cwd'] = payload.get('cwd', '')
                    summary['model'] = payload.get('model_provider', '')
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
                        break
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
  background: #fff;
}
.ev.user { border-left-color: var(--user); background: #eaf3ff; }
.ev.user_context { border-left-color: #7f8ea0; background: #f5f7fa; }
.ev.assistant { border-left-color: var(--assistant); }
.ev.developer { border-left-color: var(--dev); }
.ev.system { border-left-color: #6b7280; }
.ev-head {
  font-size: 12px;
  color: var(--muted);
  margin-bottom: 8px;
}
pre {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 12px;
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
      <input id="date_from" type="datetime-local" />
      <input id="date_to" type="datetime-local" />
      <input id="q" placeholder="free filter" />
      <select id="mode">
        <option value="and">AND</option>
        <option value="or">OR</option>
      </select>
      <button id="reload">Reload</button>
    </div>
    <div id="sessions"></div>
  </aside>
  <main class="right">
    <div class="meta" id="meta">セッションを選択してください</div>
    <div id="events"></div>
  </main>
</div>
<script>
const state = { sessions: [], filtered: [], activePath: null };

function esc(s){
  return (s ?? '').toString().replace(/[&<>\"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',"'":'&#39;'}[c]));
}

function highlightSessionPath(s){
  const safe = esc(s);
  return safe.replace(/(\\d{4}-\\d{2}-\\d{2}T\\d{2}[-:]\\d{2}[-:]\\d{2}(?:[-:]\\d{3,6})?)/g, '<span class="ts">$1</span>');
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

async function loadSessions(){
  const r = await fetch('/api/sessions');
  const data = await r.json();
  state.sessions = data.sessions;
  document.getElementById('root').textContent = data.root;
  applyFilter();
}

function applyFilter(){
  const cwdQ = document.getElementById('cwd_q').value.toLowerCase().trim();
  const q = document.getElementById('q').value.toLowerCase().trim();
  const fromRaw = document.getElementById('date_from').value;
  const toRaw = document.getElementById('date_to').value;
  const fromTs = fromRaw ? toTimestamp(fromRaw) : null;
  const toTs = toRaw ? toTimestamp(toRaw) : null;
  const mode = document.getElementById('mode').value;
  const terms = q.split(new RegExp('\\\\s+')).filter(Boolean);
  state.filtered = state.sessions.filter(s => {
    if(cwdQ && !(s.cwd || '').toLowerCase().includes(cwdQ)){
      return false;
    }
    if(fromTs !== null || toTs !== null){
      const sessionTs = toTimestamp(s.started_at || s.mtime);
      if(Number.isNaN(sessionTs)){
        return false;
      }
      if(fromTs !== null && sessionTs < fromTs){
        return false;
      }
      if(toTs !== null && sessionTs > toTs){
        return false;
      }
    }
    if(terms.length === 0) return true;
    const target = (
      s.relative_path + ' ' +
      (s.first_real_user_text || '') + ' ' +
      (s.first_user_text || '') + ' ' +
      (s.search_text || '')
    ).toLowerCase();
    if(mode === 'or'){
      return terms.some(t => target.includes(t));
    }
    return terms.every(t => target.includes(t));
  });
  renderSessionList();
}

function renderSessionList(){
  const box = document.getElementById('sessions');
  box.innerHTML = state.filtered.map(s => `
    <div class="session-item ${state.activePath === s.path ? 'active' : ''}" data-path="${esc(s.path)}">
      <div class="session-path">${highlightSessionPath(s.relative_path)}</div>
      <div class="session-preview">${esc(s.first_real_user_text || s.first_user_text || '(previewなし)')}</div>
      <div class="session-path session-cwd">cwd: ${esc(s.cwd || '-')}</div>
      <div class="session-path session-time">${esc(fmt(s.started_at || s.mtime))}</div>
    </div>
  `).join('');
  box.querySelectorAll('.session-item').forEach(el => {
    el.onclick = () => openSession(el.dataset.path);
  });
}

async function openSession(path){
  state.activePath = path;
  renderSessionList();
  const r = await fetch('/api/session?path=' + encodeURIComponent(path));
  const data = await r.json();
  if(data.error){
    document.getElementById('meta').textContent = data.error;
    document.getElementById('events').innerHTML = '';
    return;
  }
  document.getElementById('meta').innerHTML =
    `path: <code class="path-code">${highlightSessionPath(data.session.relative_path)}</code> | cwd: <code class="cwd-code">${esc(data.session.cwd || '-')}</code> | events: ${data.events.length} | raw lines: ${data.raw_line_count}`;

  document.getElementById('events').innerHTML = data.events.map(ev => {
    const role = ev.role || 'system';
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
    return `<div class="ev ${role}"><div class="ev-head">${esc(ev.kind)} | ${esc(role)} | ${esc(fmt(ev.timestamp))}</div>${body}</div>`;
  }).join('');
}

document.getElementById('cwd_q').addEventListener('input', applyFilter);
document.getElementById('date_from').addEventListener('change', applyFilter);
document.getElementById('date_to').addEventListener('change', applyFilter);
document.getElementById('q').addEventListener('input', applyFilter);
document.getElementById('mode').addEventListener('change', applyFilter);
document.getElementById('reload').addEventListener('click', loadSessions);
loadSessions();
</script>
</body>
</html>
"""


class Handler(BaseHTTPRequestHandler):
    def _send_json(self, data, status=200):
        raw = json.dumps(data, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _send_html(self, text, status=200):
        raw = text.encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def log_message(self, fmt, *args):
        return

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == '/':
            self._send_html(HTML_PAGE)
            return

        if parsed.path == '/api/sessions':
            root = get_sessions_dir()
            files = iter_session_files(root)[:MAX_LIST]
            sessions = [summarize_session(p) for p in files]
            self._send_json({'root': str(root), 'sessions': sessions})
            return

        if parsed.path == '/api/session':
            root = get_sessions_dir().resolve()
            q = urllib.parse.parse_qs(parsed.query)
            raw_path = q.get('path', [''])[0]
            if not raw_path:
                self._send_json({'error': 'path is required'}, 400)
                return
            p = Path(raw_path).expanduser().resolve()
            try:
                p.relative_to(root)
            except Exception:
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
    print(f'Sessions dir: {get_sessions_dir()}')
    server.serve_forever()


if __name__ == '__main__':
    main()
