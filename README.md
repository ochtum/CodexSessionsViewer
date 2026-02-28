<p align="left">
  <a href="README_en.md"><img src="https://img.shields.io/badge/English Mode-blue.svg" alt="English"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/日本語 モード-red.svg" alt="日本語"></a>
</p>

# Codex Sessions Viewer

`~/.codex/sessions` 配下の `.jsonl` を一覧・詳細表示するローカル Viewer です。  
WSL 実行時は、WSL 側 `~/.codex/sessions` が見つからない場合に Windows 側 `C:\Users\<user>\.codex\sessions`（`/mnt/c/Users/<user>/.codex/sessions`）も自動探索します。
WSL 側と Windows 側の両方にセッションがある場合は、両方を読み込んで一覧化します。

![image](/image/00001.jpg)

## ディレクトリ構成

```text
.
├─ viewer.py
└─ scripts
   ├─ windows
   │  ├─ launch_viewer.bat
   │  └─ stop_viewer.bat
   ├─ wsl
   │  └─ launch_viewer_wsl.sh
   └─ registry
      ├─ add_wsl_context_menu.reg
      └─ remove_wsl_context_menu.reg
```

## 起動方法

### WSL から直接起動

```bash
python3 viewer.py
```

起動後、ブラウザで以下を開きます。

```text
http://127.0.0.1:8765
```

### Windows からワンクリック起動（バッチ）

`scripts\windows\launch_viewer.bat` を実行すると、WSL 側で `python3 viewer.py` を起動し、既定ブラウザで `http://127.0.0.1:8765` を自動で開きます。

停止する場合は `scripts\windows\stop_viewer.bat` を実行してください。

`launch_viewer.bat` は起動待ちをしてからブラウザを開きます。起動失敗時は診断情報を表示します。

## レジストリスクリプト

WSL コンテキストメニューを登録する場合:

- `scripts\registry\add_wsl_context_menu.reg`

登録を解除する場合:

- `scripts\registry\remove_wsl_context_menu.reg`

## オプション

デフォルト以外のセッションディレクトリを使う場合は `SESSIONS_DIR` を設定します。  
WSL では `SESSIONS_DIR` に Windows 形式パス（例: `C:\Users\workuser\.codex\sessions`）を渡しても自動変換されます。

```bash
SESSIONS_DIR=/path/to/sessions python3 viewer.py
```

待ち受けアドレスを変更する場合は `HOST` を設定します。

```bash
HOST=0.0.0.0 python3 viewer.py
```

## 画面機能

- 左ペイン: セッション一覧（最新順）
- 一覧にセッション `source` ラベル（`CLI` / `VS Code`）を表示
- 左上 filter: パスと最初のユーザー入力で絞り込み
- 検索は一部一致（部分一致）。セッション内の先頭メッセージ群も対象
- `cwd` / 日時 / キーワードは常に AND 条件で評価
- `AND/OR` 切替はキーワード欄内のみ
  - `AND`: スペース区切りキーワードをすべて含む
  - `OR`: スペース区切りキーワードのどれかを含む
- 右ペイン: 選択セッションのイベント時系列表示
  - 詳細ヘッダーに `source` ラベル（`CLI` / `VS Code`）を表示
  - `message`（user / assistant / developer）
  - `user` は薄青背景、`AGENTS.md` や `environment_context` などの実行コンテキストはグレー背景で表示
  - `function_call` / `function_output`
  - `agent_update`

## 補足

- 大量ログ対策で一覧最大 `300` 件、イベント最大 `2000` 件に制限しています。
- Viewer はローカル専用 (`127.0.0.1`) で待ち受けます。

---

## AutoHotkey によるショートカットキー起動（Windows）

`scripts\windows\launch_viewer.bat` / `scripts\windows\stop_viewer.bat` をキーボードショートカットで起動したい場合は、AutoHotkey を利用します。

### 1. AutoHotkey のインストール

1. 公式サイトにアクセス
   [https://www.autohotkey.com/](https://www.autohotkey.com/)

2. **AutoHotkey v2** をダウンロードしてインストールします。
   ※ v1 と v2 は構文が異なるため、本手順では v2 を使用します。

インストール後、`.ahk` ファイルが実行可能になります。

### 2. ホットキー用スクリプトの作成

任意の場所（例: リポジトリ直下やドキュメントフォルダ）に
`CodexViewerHotkeys.ahk` というファイルを作成します。

中身は以下のようにします（パスは環境に合わせて変更してください）。

```ahk
#SingleInstance Force

; Win + P で起動
#p::Run "C:\path\to\CodexSessionsViewer\scripts\windows\launch_viewer.bat"

; Win + O で停止
#o::Run "C:\path\to\CodexSessionsViewer\scripts\windows\stop_viewer.bat"
```

### キー記号の意味

| 記号  | 意味    |
| --- | ----- |
| `#` | Winキー |
| `^` | Ctrl  |
| `!` | Alt   |
| `+` | Shift |

例: `^!v` は `Ctrl + Alt + V`

### 3. 動作確認

作成した `.ahk` ファイルをダブルクリックします。

タスクトレイに AutoHotkey のアイコンが表示されれば有効です。

設定したキー（例: `Win + P`）で Viewer が起動することを確認してください。

### 4. Windows 起動時に自動有効化する

1. `Win + R`
2. 以下を入力して Enter

```
shell:startup
```

3. 開いたフォルダに `CodexViewerHotkeys.ahk` を配置します。

これで Windows 起動時に自動でホットキーが有効になります。

### 5. 管理者権限が必要な場合

もしバッチが管理者権限での実行を前提としている場合は、以下のように変更します。

```ahk
#p::Run '*RunAs "C:\path\to\CodexSessionsViewer\scripts\windows\launch_viewer.bat"'
#o::Run '*RunAs "C:\path\to\CodexSessionsViewer\scripts\windows\stop_viewer.bat"'
```

### 6. 補足

- `.ahk` を編集した場合は、タスクトレイの AutoHotkey アイコンを右クリックし「Reload Script」で再読み込みできます。
- v1 と v2 が両方インストールされている場合は、v2 を既定の関連付けにしてください。

## ❗このプロジェクトは MIT ライセンスの下で提供されています。詳細は LICENSE ファイルをご覧ください。
