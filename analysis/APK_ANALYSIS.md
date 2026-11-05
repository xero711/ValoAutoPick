# ValBuddy 2.3.0 静的解析メモ

対象: `C:\project\valo_autopick_App\ValBuddy-2.3.0.apk`

- サイズ: 127,991,639 bytes
- SHA-256: `a2c56d4b7f222f390fb11893920c5725cbc643da428c271ac3cc7581f0c3e70e`
- `assets/app.config` にname=ValBuddy、version=2.3.0、android.package=com.valbuddy.app、versionCode=64、Expo SDK=58.0.0。
- React Native / Expoのネイティブライブラリ、6個のDEXファイル、`assets/index.android.bundle` を確認。
- bundleのサイズは5,559,608 bytes。先頭のHermes bytecodeマジックとversion=99を確認。平文JavaScriptではない。

## APK内で確認できた通信先・識別子

Hermes bundleからASCIIの可読文字列を抽出した。文字列は近接・重複圧縮された状態で並んでおり、制御フローの復元ではない。

- `https://auth.riotgames.com/authorize` と、`client_id=play-valorant-web-prod`、redirect=`https://playvalorant.com/opt_in`、response_type=`token id_token`、scope=`account openid` のログインURL。
- `https://auth.riotgames.com/userinfo`
- `https://entitlements.auth.riotgames.com/api/token/v1`
- `https://riot-geo.pas.si.riotgames.com/pas/v1/product/valorant`
- `https://riot-geo.pas.si.riotgames.com/pas/v1/service/chat`
- `https://glz-` と `.a.pvp.net` に関するURL断片。
- `/pregame/v1/players/`、`/pregame/v1/matches/`、`/select/`、`/lock/`。
- `postMatchmakingJoin`、`postMatchmakingLeave`、`postPartyLeave` と `/matchmaking/join`、`/matchmaking/leave` の文字列。Hermes HBC v99 の関連関数を静的に逆コンパイルすると、パーティー画面のメイン操作はキュー中なら `leaveQueue.mutate()`、それ以外なら現在のキューを記録して `startQueue.mutate()` を呼ぶ切替式だった。Windows版が使うPOSTのパスは、APK内の文字列と同梱調査資料のParty API定義の両方と一致する。元APKで、試合後に自動で再参加する制御は確認できなかった。Windows版のオートインキューは今回追加した新機能。
- `lockAgent`、`pregameSnapshot`、`pregameAgentStates`、`MatchID` 等。
- `expo-secure-store` の設定も確認。ただしAPK自体の秘密情報の保存方法や安全性を全面監査したわけではない。

## 判断と実装

ログインで得たトークン、アカウントの地域、試合前状態を用いる通信方式が採用されていると判断した。公開されているAPI調査プロジェクトのスキーマと照合し、Windows版をC#で新規実装した。APKのプログラムコード、画像、アプリ名のブランド表示を移植したものではない。

別PC対応のため、Windows版の既定接続はRiot Webログイン。ログインはWebView2 InPrivate内で行い、Riotのページ自身にユーザーが入力する。受信トークンはメモリで保持。試合前プレイヤー取得→マッチ情報取得→設定中エージェントの選択→確定→再読込、の順に実装。

毎試合の自動監視、重複抑止、キャンセル、上限付き再試行はWindows版で新たに実装した。代替エージェント候補は設けず、設定したエージェントが他プレイヤーに選択済みの場合は手動選択を案内する。自動インキューは8秒間隔で状態を確認し、リーダーかつ参加可能なロビーに限って選択中のキューへ参加する。停止時は進行中の検索に離脱要求を送り、マッチ成立後は自動で承諾・辞退しない。抽出文字列だけから、元のAPKに継続的な自動ピックや試合後の自動再参加機能があると断定することはできない。

エージェント選択画面はロール別に見出しを付けて表示し、メイン画面では設定中エージェントの立ち絵を大きく表示する。停止中・開始中のボタン表示とサインイン上部の説明文は、画面スモーク画像で確認した。

## 限界

APKは実行しておらず、Android上の通信を捕捉していない。Hermesの逆コンパイルはキュー画面の関連関数に限定し、全関数の制御フロー解析は行っていない。Windows版の実アカウント認証完了・実キュー参加・実試合・長時間セッション更新は未検証。

照合資料: [techchrism/valorant-api-docs](https://github.com/techchrism/valorant-api-docs)、[Riot Geo](https://valapidocs.techchrism.me/endpoint/riot-geo)、[Cookie Reauth](https://valapidocs.techchrism.me/endpoint/cookie-reauth)、[Valorant-API](https://valorant-api.com/)。
