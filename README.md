<p align="left">
  <a href="README_en.md"><img src="https://img.shields.io/badge/English Mode-blue.svg" alt="English"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/日本語 モード-red.svg" alt="日本語"></a>
</p>

# Codex Sessions Viewer

CodexCLI(VS Code拡張機能 Codex含む)の履歴 を一覧・詳細表示して、検索することができるローカル Viewer です。覚えておきたい内容にラベルを貼り付けて、あとから検索することもできます。

- 本ツールは 日本語 / English / 简体中文 / 繁體中文 に対応しています。
- ご意見、ご要望はご遠慮なく issue に投稿ください。

## 画面構成

### メイン画面

![image](/image/00001.jpg)

### ラベル管理画面

![image](/image/00002.jpg)

### ショートカットキーリスト画面

![image](/image/00003.jpg)

⭐ このプロジェクトが役に立ったら、Starしてもらえると嬉しいです！

👀 更新を追いたい方はWatchもぜひ！

## ディレクトリ構成

```text
.
├─ image
│  ├─ 00001.jpg
│  ├─ 00002.jpg
│  └─ 00003.jpg
└─ src
   ├─ .cache
   │  └─ label-store.json
   ├─ .vscode
   │  ├─ launch.json
   │  └─ tasks.json
   ├─ Components
   │  ├─ App.razor
   │  ├─ Routes.razor
   │  ├─ _Imports.razor
   │  ├─ Layout
   │  │  ├─ MainLayout.razor
   │  │  ├─ MainLayout.razor.css
   │  │  ├─ ReconnectModal.razor
   │  │  ├─ ReconnectModal.razor.css
   │  │  └─ ReconnectModal.razor.js
   │  └─ Pages
   │     ├─ Error.razor
   │     ├─ Home.razor
   │     ├─ Labels.razor
   │     └─ NotFound.razor
   ├─ Models
   │  └─ ViewerDtos.cs
   ├─ Properties
   │  └─ launchSettings.json
   ├─ Services
   │  ├─ LabelStore.cs
   │  └─ ViewerService.cs
   ├─ wwwroot
   │  ├─ app.css
   │  ├─ css
   │  │  ├─ labels.css
   │  │  └─ viewer.css
   │  ├─ icons
   │  │  └─ codex-sessions-viewer.svg
   │  └─ js
   │     ├─ labels.js
   │     └─ viewer.js
   ├─ appsettings.Development.json
   ├─ appsettings.json
   ├─ CodexSessionsViewer.csproj
   ├─ CodexSessionsViewer.sln
   └─ Program.cs
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

- ヘッダー
  - 左上にプロダクトアイコン付きの `Codex Sessions Viewer` タイトルを表示
  - 右上に言語切替 (`日本語` / `English` / `简体中文` / `繁體中文`)
  - `ラベル管理` / `メタ表示` / `ショートカット` / モバイル時の一覧表示切替を配置
  - `メタ表示` は既定で非表示。`session root` / `path` / `cwd` / `time` / `source` / `events` / `raw lines` を確認可能
- 左ペイン: セッション一覧
  - セッションプレビュー、`source` ラベル（`CLI` / `VS Code`）、セッションラベル、`cwd` を表示
  - 一覧上部に `sessions: filtered/total` の件数を表示
  - `新しい順` / `古い順` / `最終更新日時順` のタブで並び順を切り替え可能
  - `Clear` で左ペインの検索条件とフィルタ条件を初期化
  - `フィルタを表示` / `フィルタを隠す` で検索・フィルタエリアを折りたたみ可能
  - 縦表示時はヘッダー右上の「一覧を隠す / 一覧を表示」で左ペイン全体を切り替え可能
- 左ペインの検索・フィルタ
  - `cwd` / `開始日` / `終了日` / `イベント開始日時` / `イベント終了日時` / キーワード / `source` / セッションラベル / イベントラベルで絞り込み
  - イベント日時の時刻欄は、対応する日付を入れると有効化
  - `message` だけでなく、`function_call.arguments` / `function_output.output` / `agent_update.message` も検索対象
  - キーワード欄ではダブルクォートで囲んだ語句を 1 つのフレーズとして扱える
    - 例: `"Working Space"` を 1 語として検索
  - `cwd` / 日時 / `source` / ラベル条件は常に AND 条件
  - `AND/OR` 切替はキーワード欄のみに適用
    - `AND`: スペース区切りキーワードをすべて含む
    - `OR`: スペース区切りキーワードのどれかを含む
- 右ペイン: 選択セッションのイベント時系列表示
  - 初回詳細読み込み時はローディング表示、手動 `Refresh` 時は詳細更新中オーバーレイを表示
  - 詳細ツールバーは `表示` / `操作` / `検索` / `範囲選択` のフラット構成
  - `詳細操作を表示` / `詳細操作を隠す` で `操作` / `検索` / `範囲選択` セクションをまとめて切り替え可能
  - セッション未選択時は表示系・検索系・範囲選択系の操作を無効化
- 右ペインの表示・操作
  - 表示条件: 「ユーザー指示のみ表示」 / 「AIレスポンスのみ表示」 / 「各入力と最終応答のみ」 / 「表示順を逆にする」 / `label`
  - `Refresh` で選択中セッションだけを再取得
  - `Clear` で右ペイン全体の状態をリセット
    - 表示フィルタ
    - 詳細キーワード入力、`フィルター` / `検索` 状態
    - 選択モード、選択済みメッセージ
    - 起点選択モード、起点、起点以前 / 以降表示
    - 開いているラベルピッカー
  - 「セッション再開コマンドコピー」で `codex resume <セッションID>` をコピー
  - 「表示中メッセージコピー」で現在表示中の `message` をまとめてコピー
  - セッションラベル表示と「セッションにラベル追加」
  - イベントごとのラベル表示 / 追加 / 削除
  - 各 `message` イベントに個別「コピー」ボタンを表示
- 右ペインの検索・選択
  - 詳細キーワードは `フィルター` と `検索` を分離
    - `フィルター`: キーワードを含むイベントだけを表示
    - `検索`: 一致箇所をハイライトし、`前へ` / `次へ` で移動
    - ヒット件数を `current / total` で表示
    - `検索をクリア`: 入力欄、フィルター、検索状態をまとめて解除
  - 詳細キーワードは AND / OR ではなく、入力文字列そのままの部分一致
  - 検索対象は `message` / `function_call` / `function_output` / `agent_update`
  - 検索欄で `Enter` を押すと検索を実行し、そのままフォーカスを外して `N` / `P` で移動可能
  - `イベント開始日時` / `イベント終了日時` で、右ペインに表示するイベント時系列を絞り込み可能
  - 右ペインのイベント日時フィルタも `date + time` の分割入力で、時刻欄は日付入力後に有効化
  - 「選択モード」で `message` ごとにチェックを付けて、「選択コピー」でまとめてコピー可能
    - フィルター適用中でも、すでに選択済みの `message` は保持
  - 「起点選択モード」で単一の `message` を選び、「起点以降のみ表示」 / 「起点以前のみ表示」で絞り込み可能
- イベント表示
  - `message`（`user` / `assistant` / `developer`）
  - `user` は薄青背景、`AGENTS.md` や `environment_context` などの実行コンテキストはグレー背景
  - `function_call` / `function_output`
  - `agent_update`
- ラベル管理
  - 右上の「ラベル管理」ボタンから別ウィンドウで開く
  - メイン画面と同じ言語設定を共有
  - セッションラベル / イベントラベルを共通管理
  - ラベル色は `#hex` / `rgb(...)` / `oklch(...)` を直接入力、または色プリセットから選択可能
  - ラベル追加系 UI でも色付きのまま候補を確認可能

## ショートカットキー

入力欄にカーソルがある間は、ショートカットは実行されません。`Esc` でショートカット一覧やラベルピッカーを閉じるか、検索入力からカーソルを外せます。

主要なボタンやトグルには、対応するショートカットキーをツールチップでも表示します。

| キー        | 動作                                                                           |
| ----------- | ------------------------------------------------------------------------------ |
| `F5`        | 表示中の一覧またはセッション詳細を更新                                         |
| `Shift + F` | 左ペインのフィルタ表示を切り替え                                               |
| `Shift + L` | 左ペインの `Clear` を実行                                                      |
| `/`         | 検索入力欄にフォーカス                                                         |
| `N`         | 詳細検索の次のヒットへ移動                                                     |
| `P`         | 詳細検索の前のヒットへ移動                                                     |
| `M`         | `path / cwd / time` のメタ表示を切り替え                                       |
| `[`         | 前のセッションを開く                                                           |
| `]`         | 次のセッションを開く                                                           |
| `1`         | 「ユーザー指示のみ表示」を切り替え                                             |
| `2`         | 「AIレスポンスのみ表示」を切り替え                                             |
| `3`         | 「各入力と最終応答のみ」を切り替え                                             |
| `4`         | 「表示順を逆にする」を切り替え                                                 |
| `Shift + D` | 右ペインの表示条件と操作状態をクリア                                           |
| `Shift + T` | 詳細操作の表示と非表示を切り替え                                               |
| `Shift + R` | セッション再開コマンドをコピー                                                 |
| `Shift + C` | 表示中メッセージをコピー                                                       |
| `Shift + S` | 選択モードの開始と終了を切り替え                                               |
| `Shift + X` | 選択中メッセージをコピー                                                       |
| `Shift + G` | 起点選択モードの開始と終了を切り替え                                           |
| `Shift + H` | 起点を解除                                                                     |
| `,`         | 起点以前のみ表示                                                               |
| `.`         | 起点以降のみ表示                                                               |
| `Esc`       | ショートカット一覧やラベル追加ポップアップを閉じ、検索入力欄からカーソルを外す |

## 補足

- 検索インデックスは `.cache/search_index.sqlite3` に保存され、変更のあったセッションだけ差分更新します。
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

| 記号 | 意味    |
| ---- | ------- |
| `#`  | Winキー |
| `^`  | Ctrl    |
| `!`  | Alt     |
| `+`  | Shift   |

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
