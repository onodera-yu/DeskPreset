# DeskPreset

Windows 11 の仮想デスクトップごとに「どのアプリを、どの位置で開くか」をプリセットとして保存し、ワンクリックで復元するツールです。

## 動作環境

Windows 11（22H2 以降）。.NET のインストールは不要です。

## インストール

1. [Releases](../../releases) から最新の `DeskPreset-vX.Y.Z-win-x64.zip` をダウンロード
2. 好きなフォルダに展開して `DeskPreset.exe` を起動
3. 初回に「Windows によって PC が保護されました」と表示された場合は、**詳細情報 → 実行** をクリック
   （コード署名をしていないため表示されます）

アンインストールはフォルダを削除するだけです。設定は `%APPDATA%\DeskPreset` に残るので、不要なら削除してください。

## 使い方

| 操作 | 内容 |
|---|---|
| 現在の配置から作成 | すべての仮想デスクトップで開いているウィンドウを、位置・サイズ・最大化状態ごと取り込む |
| ▶ 起動 (F5) / ダブルクリック | 足りないデスクトップを作成し、開いているウィンドウは再利用、無いアプリは起動して各デスクトップへ移動 |
| ＋ アプリを追加 | 起動中のウィンドウ / インストール済みアプリ / ファイル から追加 |
| ドラッグ＆ドロップ | アプリを別のデスクトップへ移動・並べ替え |
| 右クリック | テスト起動、現在の位置を取得、複製、削除 |
| ショートカット ▾ | デスクトップにショートカット作成、サインイン時に自動で開く（スタートアップ登録） |
| 書き出し / 読み込み | プリセットをファイルに保存し、別の PC で読み込む |

編集内容は自動保存されます。

## 別の PC へ持っていく

プリセットのパスは PC に依存しない形で保存されます。

- `C:\Users\<名前>\AppData\Local\...` → `%LOCALAPPDATA%\...` のように環境変数で保存
- `app-1.2.3` のようなバージョン番号付きフォルダは `app-*` として保存し、起動時に最新のものを使う
- ストアアプリはアプリ ID で起動するため、インストール場所に依存しない

移行先の PC に入っていないアプリは、起動時のログに「このPCに見つかりません」と表示されてスキップされます。

## 保存先

- `%APPDATA%\DeskPreset\presets.json` … プリセット（手で編集も可）
- `%APPDATA%\DeskPreset\last-run.log` … ショートカット / スタートアップから起動したときのログ
- 環境変数 `DESKPRESET_HOME` で保存先を変更できます

## コマンドライン

```
DeskPreset.exe --run "プリセット名"     # 画面を出さずに起動
deskpreset-cli.exe desktops              # 仮想デスクトップ一覧
deskpreset-cli.exe windows               # デスクトップごとのウィンドウ一覧
deskpreset-cli.exe capture <名前>         # 現在の配置を保存
deskpreset-cli.exe run <名前>             # プリセットを起動
```

## ビルド

.NET 10 SDK が必要です（`global.json` で指定）。

```
dotnet build                 # 開発用ビルド
./build.ps1                  # 配布用 zip（.NET 同梱の単一 exe）を dist/ に作成
```

## 開発に参加する

ブランチの使い方・PR・リリース手順は [CONTRIBUTING.md](CONTRIBUTING.md) を参照してください。
`main` へのマージがそのままリリースになります（`Directory.Build.props` のバージョンでタグと Release を自動作成）。

## 仕組みと制限

- 他プロセスのウィンドウを別デスクトップへ移すため、Windows の非公開 COM インターフェース
  （IVirtualDesktopManagerInternal など）を vtable 経由で呼び出しています。
  インターフェース ID は Windows のビルドで変わることがあり、`src/DeskPreset.Core/Interop/VirtualDesktopService.cs`
  の `Profiles` に定義しています。新しいビルドで動かなくなった場合は、`%APPDATA%\DeskPreset\interop.json` に
  プロファイルを追加すると再ビルドなしで対応できます:
  ```json
  [{ "Name": "custom", "ManagerInternal": "<IID>", "VirtualDesktop": "<IID>", "HasSwitchAndMoveForeground": true }]
  ```
- 復元できるのは「アプリの起動・デスクトップ・位置・サイズ」まで。ブラウザーのタブや開いていたファイルは各アプリの復元機能に依存します
  （エクスプローラーは開いていたフォルダを引数として保存します）。
- 管理者権限で動いているアプリは、DeskPreset を通常権限で動かしている場合は位置を変更できないことがあります。
- 同じアプリの複数ウィンドウは起動順に割り当てます。区別したい場合は「タイトルに含む文字」を設定してください。

