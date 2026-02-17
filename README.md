# Codex Sessions Viewer

`~/.codex/sessions` 配下の `.jsonl` を一覧・詳細表示するローカル Viewer です。

## 使い方

```bash
python3 viewer.py
```

起動後、ブラウザで以下を開きます。

```text
http://127.0.0.1:8765
```

### Windows からワンクリック起動

`launch_viewer.bat` を実行すると、Windows 側の現在フォルダを WSL パスに変換し、WSL 側で `python3 viewer.py` を起動して、既定ブラウザで `http://127.0.0.1:8765` を自動で開きます。

停止する場合は `stop_viewer.bat` を実行してください。

`launch_viewer.bat` は起動待ちをしてからブラウザを開きます。起動失敗時は `/tmp/codex-sessions-viewer.log` の末尾を表示します。

`launch_viewer.bat` / `stop_viewer.bat` は `docker-desktop` 系を除外して利用可能な distro を自動選択します。固定したい場合は `WSL_DISTRO` を設定してください（例: `Ubuntu`）。

## オプション

デフォルト以外のセッションディレクトリを使う場合は `SESSIONS_DIR` を設定します。

```bash
SESSIONS_DIR=/path/to/sessions python3 viewer.py
```

待ち受けアドレスを変更する場合は `HOST` を設定します。

```bash
HOST=0.0.0.0 python3 viewer.py
```

## 画面機能

- 左ペイン: セッション一覧（最新順）
- 左上 filter: パスと最初のユーザー入力で絞り込み
- 検索は一部一致（部分一致）。セッション内の先頭メッセージ群も対象
- `AND/OR` 切替:
  - `AND`: スペース区切りキーワードをすべて含む
  - `OR`: スペース区切りキーワードのどれかを含む
- 右ペイン: 選択セッションのイベント時系列表示
  - `message`（user / assistant / developer）
  - `user` は薄青背景、`AGENTS.md` や `environment_context` などの実行コンテキストはグレー背景で表示
  - `function_call` / `function_output`
  - `agent_update`

## 補足

- 大量ログ対策で一覧最大 `300` 件、イベント最大 `2000` 件に制限しています。
- Viewer はローカル専用 (`127.0.0.1`) で待ち受けます。
