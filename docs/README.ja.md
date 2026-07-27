# Vouch

[English](../README.md) · [中文](README.zh-CN.md) · [Русский](README.ru.md) · **日本語**

デスクトップ向けのモダンでクロスプラットフォームな **Steam 認証アプリ** —
メンテナンスが終了した
[Steam Desktop Authenticator](https://github.com/Jessecar96/SteamDesktopAuthenticator)
をゼロから再実装したもので、Avalonia 上に構築されたクリーンなマルチアカウント UI を備えています。

複数の Steam アカウントの管理、ログインコードの生成、取引・マーケットの確認の承認、
取引オファーの確認と応答、インベントリの閲覧と転送、サインインの承認、
アカウントのログイン中デバイスの管理、認証アプリのリンク・リンク解除まで、
すべてを 1 つのアプリから行えます。

- **技術スタック:** Avalonia 12 (Fluent) · .NET 10 · CommunityToolkit.Mvvm · ログインは SteamKit2 + その他は Steam への直接 HTTP 通信
- **プラットフォーム:** Windows (メイン)、Linux、macOS
- **言語:** English · 简体中文 · Русский · 日本語 (リアルタイム切り替え)

## スクリーンショット

<p align="center">
  <img src="screenshot/s1.png" width="46%">
  <img src="screenshot/s2.png" width="46%">
  <img src="screenshot/s3.png" width="46%">
  <img src="screenshot/s4.png" width="46%">
</p>

---

## ⚠️ セキュリティ — まずはこれを読んでください

**あなたの `.maFile` はアカウントそのものです。** これには 2FA シード (`shared_secret`)、
モバイル確認キー (`identity_secret`)、失効コード、有効なセッショントークン、そして
本アプリでは Steam の **パスワード** が含まれています。復号された maFile を入手した者は、
アカウントを完全に乗っ取ることができます。

**Vouch は必ず公式リポジトリからのみダウンロードしてください:**

> ### https://github.com/Monodesu/Vouch

それ以外のビルド —「ミラー」、再アップロード、フォークのリリースなど — は
**maFile を盗むためにバックドアが仕込まれている可能性があります**。上記のリンク以外から
入手した場合は、悪意のあるものだと考えてください。Vouch はあなたのシークレットやパスワードを、
Steam 自身のサーバー以外のどこにも一切送信しません。

**ファイルを保存時に暗号化してください:** **Settings → Encrypt maFiles on disk** をオンにして
パスキーを設定します。そうすると、すべての maFile (パスワードを含む) はディスク上で **Argon2id** (鍵導出) と **AES-256-GCM** で暗号化されます —— 誤ったパスキーや改ざんされたファイルは検出され、誤って復号されることはありません。起動ごとに一度パスキーを入力します。暗号化は保存時のファイルを保護しますが、改ざんされたビルドを
実行してしまうことからは **保護できません**。だからこそ、公式リポジトリからのみダウンロードすることが
同じくらい重要なのです。

---

## 機能

### アカウント
- **検索**、**複数選択**、**ドラッグでの並べ替え** (グループをまたいでも可) に対応したマルチアカウントサイドバー
- **グループ**: アカウントを折りたたみ可能なグループに整理 (右クリック → 移動 /
  新規グループ)、残りは既定のグループにまとめられます — 並び順と折りたたみ状態は保持されます
- カウントダウンリング付きのリアルタイム **TOTP ログインコード**、ワンクリックでコピー
- ユーザー名 / パスワードのコピー、保存済みパスワードのインライン編集、アカウントごとの自由記述の **メモ** (maFile に保存)
- アバター、ペルソナ名、Steam の **レベル / BAN** (VAC · ゲーム · 取引)
- 一目でわかる **セッション状態** — サインイン済み / 期限切れ / 未サインイン — をサイドバーの帯、アバターのリング、ラベルで表示

### サインイン
- 保存済みパスワードでサインイン (欠落 / 誤りの場合のみ入力を求めます)
- 有効なセッションがまだ存在する場合は **再サインイン** を表示
- メールの Steam Guard コードは専用ダイアログで処理
- 選択範囲に対する **一括サインイン / 情報更新**
- **Vouch からのサインイン承認** — 別の場所で開始された (コードを入力していない) ログインを、モバイルアプリのように **承認または拒否** できます
- **QR サインイン** — クリップボードまたは全画面キャプチャから読み取った Steam ログイン QR を承認

### デバイス
- **ログイン中デバイス**: アカウントのアクティブなログインセッションを一覧表示し、そのいずれからも **サインアウト** できます
- 単一アカウントまたは選択範囲全体に対する **すべてのデバイスからサインアウト**、その後に Vouch へ再サインインするオプションあり

### 確認とオファー
- 取引・マーケットの **確認**: 承認 / 拒否を個別または一括で
- サインイン済みの全アカウントをバックグラウンドで巡回し、新しい確認や受信した取引オファーが
  あれば **システム通知** を表示 (重複排除、Settings で切り替え可能)
- **取引オファー**: 有効なオファーを表示し、アイテムの **画像**、双方の
  アバター / 名前 / SteamID、相手のレベル・登録日・フレンド関係を含む詳細ダイアログを開いて、
  **承認 / 辞退 / キャンセル**
- 既読 / すべて既読にするための **通知** リスト

### インベントリ
- **インベントリビューア**: アカウントのアイテムをアプリ内で閲覧 — 実際にアイテムを保有しているゲームのみ (Steam 自身のドロップダウンのように)、アイコンと数量付き
- **転送**: アカウントの取引可能なアイテムを、設定済みの取引リンクへ送信 — **すべてのアイテム** または **手動で選んだ** 選択範囲 (画像付き)、ゲームごとに 1 オファー。プリセットゲーム (CS2, TF2, Dota 2, Rust, Steam) に加えて **カスタム appid**。オファーをモバイルで自動確認

### 認証アプリの管理
- **認証アプリの追加** ウィザード — **電話番号なし** で動作 (Steam が finalize コードをメールで送信)、
  失効コードは別のダイアログで表示され再確認されます。追加が完了すると、その新しいアカウントに自動的にサインインします
- 選択範囲に対して、各アカウントの保存済み失効コードを使って Steam 上の
  **認証アプリを無効化** (一括)、その後アプリから削除します

### CS2 設定の同期
- あるアカウントの **CS2 設定 / キーバインド / ビデオ設定** を他のアカウント
  (すべて、グループ、または手動で選択) にコピー、自動 **バックアップ** とワンクリック **復元** 付き

### アプリ
- 保存時に暗号化される maFile (パスキーは 1 つ、起動ごとに一度入力)
- ライト / ダークテーマ、トレイアイコン + トレイに最小化、最小化状態で起動
- チェック間隔の設定、クリップボードの自動クリア、任意の Web API キー
- GitHub Releases に対するアプリ内 **アップデートチェック**

---

## ダウンロード

[Releases](https://github.com/Monodesu/Vouch/releases) から最新の
**`Vouch-vX.Y.Z-win-x64.exe`** を入手してください。これは単一ファイル
(フレームワーク依存) で、**[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)**
のインストールが必要です。実行するだけ — インストーラーは不要です。

## ソースからのビルド

**.NET 10 SDK** が必要です。

```bash
git clone https://github.com/Monodesu/Vouch.git
cd Vouch

dotnet run --project Vouch.App        # run
dotnet test Vouch.Core.Tests          # tests
```

単一ファイルの exe を自分でビルドする:

```bash
dotnet publish Vouch.App/Vouch.App.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish
# (delete publish/*.pdb; use --self-contained true for a no-runtime-needed build)
```

## プロジェクト構成

| パス | 内容 |
|------|-----------|
| `Vouch.App/` | Avalonia デスクトップアプリ — ビュー、ビューモデル、ダイアログ、アセット |
| `Vouch.Core/` | Steam ロジック: TOTP、確認、オファー、インベントリ、リンク、ストレージ — UI なし |
| `Vouch.Core.Tests/` | 純粋 / パース処理向けの xUnit テスト |

## データの保存場所

すべてのデータは**ポータブル**で、exe と同じ場所に置かれます：

- `maFiles/` — アカウント（オリジナルの SDA と同じディスク上のレイアウト。暗号化を有効にするとその場で暗号化）
- `settings.json` — アプリ設定
- `cache/` — アバターキャッシュ

保存場所は `VOUCH_DATA_DIR` 環境変数で変更できます。

## アップデート

Vouch は必要に応じて (Settings → Check for updates) GitHub Releases に新しいタグがないか確認し、
リリースページへリンクします。リリースは `v*` タグがプッシュされると CI により自動生成されます。

## ライセンス

[GNU Affero General Public License v3.0](LICENSE) の下でライセンスされています。派生作品は
—**ネットワーク越しに提供されるものを含めて**— AGPL の下でオープンソースとして公開する必要があります。

Vouch は、Jesse Cardone による MIT ライセンスのオリジナル **Steam Desktop Authenticator** の
派生物です。その著作権表示は [NOTICE](NOTICE) に保持されています。

## 謝辞

- [Steam Desktop Authenticator](https://github.com/Jessecar96/SteamDesktopAuthenticator) — オリジナル。maFile フォーマットと、それが切り開いたリンク / 確認フローのために
- [Avalonia](https://avaloniaui.net/) — クロスプラットフォーム UI フレームワーク

## 使用ライブラリ

- [Avalonia](https://avaloniaui.net/) — クロスプラットフォーム UI フレームワーク（Fluent テーマ、Inter フォント）（MIT）
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM ソースジェネレーター（MIT）
- [SteamKit2](https://github.com/SteamRE/SteamKit) — Steam のログイン/認証ハンドシェイク。コミュニティ製ライブラリ（SteamRE）で、Valve とは**無関係**（LGPL-2.1）
- [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) — Argon2id 鍵導出（maFile 暗号化）（MIT）
- [xUnit](https://xunit.net/) — ユニットテスト（Apache-2.0）

認証器のコア（TOTP、maFile 形式、認証器のリンク、モバイル確認）は、Joshua Coffey / geel9 による [SteamAuth](https://github.com/geel9/SteamAuth)（MIT）の C# ネイティブ移植です。サードパーティライセンスの全文は [NOTICE](NOTICE) を参照してください。

---

> **免責事項:** Vouch は Valve と提携しておらず、その承認も受けていません。Steam は
> Valve Corporation の商標です。ご利用は自己責任でお願いします。
