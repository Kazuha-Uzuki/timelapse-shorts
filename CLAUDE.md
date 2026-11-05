# CLAUDE.md — タイムラプス縦動画メーカー

何時間ものスマホ録画（創作の様子）を、SNS 用の縦長タイムラプス（1080×1920 / 30fps / H.264）にする Windows アプリ。
WPF / .NET 10（`net10.0-windows`）。利用者は一般の人なので、画面と説明はすべて日本語で、やさしい言葉にする。

## ファイル

| ファイル | 中身 |
|---|---|
| `src/Ffmpeg.cs` | 変換ロジック（エンコーダー検出・間引き・冒頭移動・HDR・縦長化・BGM・文字入れ・プレビュー）。WPF に依存しない |
| `src/FontCatalog.cs` | フォント一覧と、libass に渡すフォントファイル・ファミリー名の解決（WPF） |
| `src/MainWindow.xaml(.cs)` | 画面。投稿先の上限は `Platforms`（2026年10月時点の値） |
| `src/SettingsWindow.xaml(.cs)` | 「設定」の小さな画面（画面の色・保存先・作成が終わったとき）と、テーマを切り替える `ApplyTheme` |
| `src/SleepWindow.xaml(.cs)` | 「終わったら PC をスリープ」の 60 秒のお知らせ（true を返したら呼んだ側が `SetSuspendState`） |
| `src/AppSettings.cs` | 前回の設定（`%LOCALAPPDATA%\TimelapseShorts\settings.json`）。閉じるときと設定画面を閉じたときに書く |
| `tests/WinCheck` | CI の Windows で、画面と変換を実際に動かして確かめるテスト |
| `.github/workflows/build.yml` | exe ビルド、LGPL 版 FFmpeg と Google Fonts の同梱、WinCheck、zip 化、Release への添付 |
| `使い方.txt` | 利用者向けの説明。zip には `README.txt` として入る（日本語のファイル名は解凍ソフトで文字化けするため） |

## ビルドと確認

- WPF は手元（Linux のコンテナ）ではビルドできない（NuGet に出られない）。ビルドと画面の確認は GitHub Actions で行う。
- 流れ: feature ブランチに push → 手動実行で確認 → main に fast-forward で入れて push。
- 手動実行（画面のスクリーンショット付き）:
  `gh api -X POST repos/Kazuha-Uzuki/timelapse-shorts/actions/workflows/build.yml/dispatches -f ref=<ブランチ> -f 'inputs[screenshots]=true'`
- 結果は `gh api repos/Kazuha-Uzuki/timelapse-shorts/actions/jobs/<job id>/logs` で読む。WinCheck は `OK  ` / `NG  ` の行を出し、最後に「すべて OK」か「NG が N 件」を出す。
- `screenshots=true` のときだけ、画面の PNG を `SHOT <名前> <Base64>` の行（4000 文字ずつ）でログに出す。
  ログの行頭に BOM（EF BB BF）が混ざることがあるので取り除いてから、`SHOT <名前> ` を外してつなげ、`base64 -d` する。
- 画面を変えたら、WinCheck の「画面の配置」（標準 1480×720 / 最小 1240×650 / 最大化 1920×1030 で切れている部品がない・プレビュー枠が 9:16）が通ることと、スクリーンショットの見た目を確かめてから main に入れる。
- `Ffmpeg.cs` は Linux でも試せる: net10.0 のコンソールで `<Compile Include="…/src/Ffmpeg.cs" />` し、出力フォルダに `ffmpeg.exe` / `ffprobe.exe` という名前で Linux の ffmpeg / ffprobe へのリンクを置く（libass 入りの ffmpeg なら文字入れも試せる）。

## リリース（配布）

- この環境ではタグの push と、リモートのブランチ削除が拒否される（403）。回避しない。
- Release はユーザーが GitHub の画面で作る（Releases → Draft a new release → タグに `vX.Y.Z`（小文字の v）→ Publish release）。
  公開されると Actions がビルドして `TimelapseShorts-win64.zip` を Release に付ける（数分）。
- zip の取得: GraphQL が使えないので `gh release download` は使えない。
  `gh api repos/Kazuha-Uzuki/timelapse-shorts/releases/tags/<tag> --jq '.assets[]|"\(.id) \(.name)"'` で id を調べ、
  `gh api repos/Kazuha-Uzuki/timelapse-shorts/releases/assets/<id> -H "Accept: application/octet-stream" > TimelapseShorts-win64.zip`。
  Actions の artifact はダウンロードできない。

## 設計の要点（直すときの注意）

- 間引き: `settb=AVTB,setpts=(PTS-STARTPTS)/倍率,fps=30:eof_action=pass` と `-frames:v`（総尺ぴったりのコマ数）。拾う間隔がキーフレーム間隔以上なら `-skip_frame nokey`（高速モード）。
- 冒頭移動: 完成品（最後の N 秒）と本編を別の入力で読み、`trim=end_frame` と `concat` でつなぐ。元の動画はどこも1回しか読まない。
- 複数ファイルは concat の連結リストに `duration` を書く（無いと途中へ移動できず遅い）。
  連結リストの途中への `-ss` は、音の無い動画で時刻が半端だと ffmpeg の丸めのせいで失敗し、先頭から読み直して遅くなる。
  そのため完成品の部分とプレビューは、`Locate` でその時刻を含むファイルから読む。
- プレビュー: 出来上がりの最初・真ん中・最後の1コマを、変換と同じフィルター（`FinishVideoAsync`）で 360×640 の PNG にする。終わりぎわで画が出なければ2秒前で描き直す。
- HDR（HLG / PQ）は zscale で SDR に。横向きの動画は「中央を切り抜く」か「ぼかし背景」（背景は 1/4 の解像度でぼかす）。
- 文字: ASS を作って libass で描く。フォントは作業フォルダにコピーして `fontsdir` で渡す。ファミリー名は Windows 用の名前（`GlyphTypeface.Win32FamilyNames` の en-US）を使う（typographic な名前だと別のフォントになる）。背景あり（帯）は `BorderStyle 3`。
- BGM: `-stream_loop` → `atrim` → `volume` →（上げたときだけ）`alimiter` → `afade`。音量は `volumedetect` で測る。
- エンコーダー: h264_nvenc → h264_qsv → h264_amf → h264_mf → libopenh264 の順に1コマ試し書きして選ぶ（LGPL 版 FFmpeg に libx264 は無い）。
- 同梱物: FFmpeg は BtbN の LGPL 版（安定版の最新を自動で選ぶ）。Google Fonts は google/fonts の commit `bbda7253fc363238b05d97515ca71f8ae49c4fa9` に固定した 9 書体と、その OFL。ライセンス文が配られていない書体は入れない。
- 画面: 横並びの 3 列（長さ・構成 / BGM / テキスト）と、右のプレビュー欄。プレビュー枠は 9:16 で、欄の幅は高さに合わせて変える（左の設定は 890 より狭くしない）。起動時に作業領域に収める。1280×720（150% 表示のノート PC）の画面にも収まること。
- 色（ダークモード）: .NET 10 の Fluent テーマ（`Application.ThemeMode`、試験的な機能なので csproj で WPF0001 を抑えている）。
  色は直接書かず `{DynamicResource TextFillColorSecondaryBrush}` などテーマの色を使う（プレビュー枠の黒と音量バーの緑・赤だけは固定）。
  自分で作る Style は `BasedOn="{StaticResource {x:Type …}}"` にする（付けないと Fluent の見た目にならない）。
  窓の背景は `ApplicationBackgroundBrush` を明示している（Fluent の既定は透明＝Mica）。
- 保存先: `Ffmpeg.ConvertAsync` の最後の引数 `outputDir`（null なら1本目の動画と同じフォルダ）。決まったフォルダが無ければ、変換を始める前に確かめる。
- 作成が終わったとき: 音は `MessageBeep`、フォルダは `explorer /select`。スリープは作成ボタンの横の `SleepCheck`（覚えない・1回ごとにオフ）で、
  成功したときだけ、窓を前に出して `SleepWindow`（Enter / Esc は「スリープしない」）→ `SetSuspendState(false, false, false)`。
  WinCheck では `SleepWindow` の返す値だけを確かめ、本当にスリープはさせない（CI の PC が止まる）。
- 設定画面のラジオボタンは2組あるので `GroupName` で分けている（同じパネルにあると1組として扱われる）。
- WinCheck: CI の画面は 1024×768 なので、窓をそれより大きくするために `WM_GETMINMAXINFO` と WPF の `_trackMaxWidthDeviceUnits` などを広げている。
