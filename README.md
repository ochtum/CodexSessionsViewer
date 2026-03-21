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

## 起動方法

Releasesにある`app-framework-dependent`フォルダをダウンロード後、解凍してから中にある`CodexSessionsViewer.exe`を実行してください。

※本ツールの実行には.NET 10 SDK または.NET 10 Runtimeが必要となります。入っているか分からない、またはインストールしないことを望む場合、`app-self-contained`フォルダをダウンロードしてください。

---

※srcからビルドを行う場合、以下のようにPower Shell スクリプトを実行してください。

- 非自己完結版(.NET 10 SDK または.NET 10 Runtimeをインストール済の場合)

```
.\publish.ps1 -CleanOutput
```

- 自己完結版(.NET 10 SDK または.NET 10 Runtimeのインストール状況不明、インストールしない場合)

```
.\publish.ps1 -SelfContained -CleanOutput
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

- ラベル情報とラベル紐付けは `.cache/label-store.json` に保存されます。
- 大量ログ対策で一覧最大 `300` 件、イベント最大 `2000` 件に制限しています。
- Viewer はローカル専用で、既定では `http://127.0.0.1:8765` で待ち受けます。既定ポートが使用中の場合は、設定範囲内の次の空きポートにフォールバックします。

---

## ディレクトリ構成

```text
.
── .vscode
   │  ├─ launch.json
   │  └─ tasks.json
── src
   ├─ .cache
   │  └─ label-store.json
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

## ❗このプロジェクトは MIT ライセンスの下で提供されています。詳細は LICENSE ファイルをご覧ください。
