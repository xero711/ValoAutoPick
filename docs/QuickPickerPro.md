# QuickPickerPro

QuickPickerPro は Valo Auto Pick を基にした Windows 向けアプリです。マップ別にエージェントを設定し、マッチを検知するとマップ画像と設定エージェントを表示します。自動ピック監視中は、選択画面の準備が整うと選択・確定を試みます。試合中もマップを表示し、ロビー復帰後は待機表示に戻ります。

## 使い方

1. VALORANT を起動し、QuickPickerPro で Riot アカウントにログインします。
2. 通常使用するエージェントを選び、「マップ別エージェント設定」で必要なマップを設定します。未設定のマップでは通常の設定を使います。
3. 「監視を開始」を押します。味方が設定したエージェントをすでに選んだ場合は、ゲーム内で手動選択してください。
4. 自動インキューを使用する場合は、ゲーム内でモードを選び「自動インキュー開始」を押します。

設定は `%LOCALAPPDATA%\ValoAutoPick\quickpickerpro-settings.json` に保存します。初回は旧アプリの設定を読み込みます。認証データやローカルの設定ファイルはリポジトリに含まれません。

## ビルド

Windows x64、.NET 9 SDK、Microsoft Edge WebView2 Runtime が必要です。PowerShell から以下を実行すると、テスト後に自己完結型の配布ファイルと ZIP を `dist` に作成します。

```powershell
.\build.ps1
```

ソースの自動テストは `dotnet run --project tests/ValoAutoPick.Tests.csproj -c Release`、UI テストは `dotnet run --project tests/UiMotion/UiMotion.csproj -c Release` で実行できます。

## マップ画像

対応マップの画像は表示時の通信を避けるためアプリに同梱しています。画像は [valorant-api.com](https://valorant-api.com/) が配信する VALORANT のマップ画像を使用しています。
