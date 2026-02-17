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

## オプション

デフォルト以外のセッションディレクトリを使う場合は `SESSIONS_DIR` を設定します。

```bash
SESSIONS_DIR=/path/to/sessions python3 viewer.py
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
