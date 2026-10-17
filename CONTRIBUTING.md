# 開発の進め方

## ブランチ

| ブランチ | 役割 | 作成元 | マージ先 |
|---|---|---|---|
| `main` | リリース済みのコードだけ。マージされるたびに自動でリリースされる | — | — |
| `develop` | 次のリリースに向けた開発の本線（既定ブランチ） | — | `main` |
| `feature/<内容>` | 機能追加 | `develop` | `develop` |
| `fix/<内容>` | 不具合修正 | `develop` | `develop` |
| `chore/<内容>` / `docs/<内容>` | ビルド・CI・ドキュメントなど | `develop` | `develop` |
| `hotfix/<内容>` | リリース済みの緊急修正 | `main` | `main`（その後 `develop` へ戻す） |

`main` と `develop` へは直接 push できません。変更はすべてプルリクエスト（PR）で入れます。

## マージ方法

- **作業ブランチ → `develop`: Squash merge**
  PR を1コミットにまとめます。PR のタイトルがそのままコミットメッセージになるので、内容が分かるタイトルにしてください。
- **`develop` → `main`: Merge commit（Squash しない）**
  Squash すると `main` と `develop` の履歴がつながらなくなり、次回以降のリリースで毎回コンフリクトが起きます。
- **`main` → `develop`（hotfix の取り込み）: Merge commit**

## 通常の開発

```
git switch develop
git pull
git switch -c feature/tray-icon
# 変更してコミット
git push -u origin feature/tray-icon
```

GitHub で `develop` 向けの PR を作り、CI（`build` と `branch-policy`）が通ったら Squash merge します。

## リリース

1. `develop` から `chore/release-vX.Y.Z` ブランチを作り、`Directory.Build.props` の `<Version>` を上げて `develop` へ PR → Squash merge
2. `develop` → `main` の PR を作成（タイトル例: `Release vX.Y.Z`）。CI がバージョンが未リリースかを確認します
3. Merge commit でマージすると、GitHub Actions が `vX.Y.Z` タグを作り、zip を Releases に公開します

## 緊急修正（hotfix）

1. `main` から `hotfix/<内容>` を作成し、修正と `<Version>` のパッチ番号上げ（例: 1.2.0 → 1.2.1）を行う
2. `main` へ PR → Merge commit でマージ（自動でリリースされます）
3. `main` → `develop` の PR を作り、Merge commit で取り込む

## バージョン番号

[セマンティック バージョニング](https://semver.org/lang/ja/) に従います。

- メジャー: プリセットファイルの互換性がなくなる変更
- マイナー: 機能追加
- パッチ: 不具合修正のみ

## 動作確認

テストの前に環境変数 `DESKPRESET_HOME` をテスト用フォルダに設定してください。普段使っているプリセット（`%APPDATA%\DeskPreset`）を上書きせずに済みます。

```
$env:DESKPRESET_HOME = "$env:TEMP\deskpreset-test"
dotnet run --project src/DeskPreset.App
```
