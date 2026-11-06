# CLAUDE.md — タイムラプス縦動画メーカー

何時間ものスマホ録画（創作の様子）を、SNS 用の縦長タイムラプス（1080×1920 / 30fps / H.264）にする Windows アプリ。
WPF / .NET 10（`net10.0-windows`）。利用者は一般の人なので、画面と説明はすべて日本語で、やさしい言葉にする。

## ファイル

| ファイル | 中身 |
|---|---|
| `src/Ffmpeg.cs` | 変換ロジック（エンコーダー検出・間引き・冒頭移動・HDR・縦長化・BGM・文字入れ・プレビュー）。WPF に依存しない |
| `src/FontCatalog.cs` | フォント一覧と、libass に渡すフォントファイル・ファミリー名の解決（WPF） |
| `src/MainWindow.xaml(.cs)` | 画面。上の手順のボタン（① 動画 ▶ ② 冒頭 ▶ ③ BGM ▶ ④ テキスト ▶ ⑤ 作成）で画面を切り替える（`ShowStep`）。左に 9:16 の画（① は元の動画の最初、③④⑤ は出来上がりのプレビュー）、右に設定のカード。投稿先の上限は `Platforms`（2026年10月時点の値） |
| `src/SettingsWindow.xaml(.cs)` | 「設定」の小さな画面（画面の色・保存先・作成が終わったとき）と、テーマを切り替える `ApplyTheme` |
| `src/IntroPanel.xaml(.cs)` | ② 冒頭（元の動画・秒数⇄範囲の連動・移す／コピー・フェード・2 本のバー）と、覚える内容 `IntroChoice`。「長さ・投稿先」のカードと「戻る／次へ」は MainWindow.xaml の `IntroParts` に書き、MainWindow のコンストラクターで `LengthContent` / `FooterContent` へ移す（XAML で作った UserControl の中身に名前付きの部品を書くと MC3093 になるため。名前は MainWindow のもの） |
| `src/ColorPanel.xaml(.cs)` | ④ テキストの「自由に選ぶ（RGB）」のパレット。文字の色とフチ・帯の色（"RRGGBB"）を、見本の色・R/G/B（バーと数値）・#RRGGBB で選ぶ。MainWindow は色の欄で「自由に選ぶ」を選んだときだけ出し、`CaptionColors()` で変換・見本・まとめに使う |
| `src/RangeBar.cs` | ② の横長のバー（目盛り・選んだ範囲・今の位置）。ドラッグで範囲を選ぶ・範囲ごとずらす・両端の調整。どう動かしたかを知らせるだけで、長さは IntroPanel が決める |
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
- 画面を変えたら、WinCheck の「画面の配置」（手順ごとに、標準 1480×720 / 最小 1240×650 / 最大化 1920×1030 で切れている部品がない・左の画が 9:16・大事な部品が見えている）が通ることと、スクリーンショット（`step0`〜`step4`）の見た目を確かめてから main に入れる。
  切れているかは `LayoutInformation.GetLayoutClip` で見るので、狭いときに長くなる欄は `ScrollViewer` に入れる（中は切れたことにならない）。
  論理ツリーの部品に加えて、ボタンの文字（テンプレートの中の ContentPresenter）も見る。手順のボタンの ✓ は、ボタンの文字に足すと幅が最初の表示のままで文字が切れた（原因は未特定。同じ見た目のボタンを後から足して文字を変えると広がる）ので、文字は変えず、ボタンの右の余白に重ねた TextBlock（`StepMarks`）で出している。
- `Ffmpeg.cs` は Linux でも試せる: net10.0 のコンソールで `<Compile Include="…/src/Ffmpeg.cs" />` し、出力フォルダに `ffmpeg.exe` / `ffprobe.exe` という名前で Linux の ffmpeg / ffprobe へのリンクを置く（libass 入りの ffmpeg なら文字入れも試せる）。

## リリース（配布）

- この環境ではタグの push と、リモートのブランチ削除が拒否される（403）。回避しない。
- Release はユーザーが GitHub の画面で作る（Releases → Draft a new release → タグに `vX.Y.Z`（小文字の v）→ Publish release）。
  作ってもらうのは、こちらが main に入れて「入れました」と伝えたあと（先に作ると、前の版の中身になる。v0.4.0 はこれで v0.3.0 と同じ中身になった）。
  公開されると Actions がビルドして `TimelapseShorts-win64.zip` を Release に付ける（数分）。
- zip の取得: GraphQL が使えないので `gh release download` は使えない。
  `gh api repos/Kazuha-Uzuki/timelapse-shorts/releases/tags/<tag> --jq '.assets[]|"\(.id) \(.name)"'` で id を調べ、
  `gh api repos/Kazuha-Uzuki/timelapse-shorts/releases/assets/<id> -H "Accept: application/octet-stream" > TimelapseShorts-win64.zip`。
  Actions の artifact はダウンロードできない。

## 設計の要点（直すときの注意）

- 間引き: `settb=AVTB,setpts=(PTS-STARTPTS)/倍率,fps=30:eof_action=pass` と `-frames:v`（総尺ぴったりのコマ数）。拾う間隔がキーフレーム間隔以上なら `-skip_frame nokey`（高速モード）。
- 冒頭（`Ffmpeg.Intro`）: 元の動画の [Start, End] 秒を、出来上がりの最初の Seconds 秒にする（倍速 (End−Start)/Seconds。1 未満はゆっくり）。
  本編は、移すなら [0, Start] と [End, 終わり]、コピーなら全部。本編の倍速 = 本編に使う元の長さ ÷（総尺 − Seconds）（`MainFactor`、1 未満なら等速）。
  「数値で（最後の N 秒）」は `Intro.Last` で、移すなら今までと同じ結果（全体と同じ倍速）。出来上がりの t 秒目が元の何秒目かは `SourceTime`
  （静止画のプレビューもこれを使う）。変換は部分ごとに別の入力で読み（`AddPart`。その時刻を含むファイルから、要るぶんだけ `-t` で）、
  冒頭は `trim=end_frame` でコマ数ちょうどに切って、`concat` でつなぐ。元の動画はどこも1回しか読まない（コピーなら冒頭の部分だけ2回）。
  完成品のフェード（`IntroFades`）は、つなぐ前の冒頭の側だけにコマ数で `fade` をかける（`fade=t=out` はそのあとのコマを全部黒にするので、
  つないだあとにはかけない）。文字はそのあとで全体に描くのでフェードしない。10 ビット（HDR）のままかかる。静止画のプレビューには出ない。
  WinCheck は、明るさが時刻で決まる動画（`geq=lum='16+T*7'`）で、各コマの明るさから元の秒を読んで `SourceTime` と比べる。
- ② 冒頭: 冒頭は本編と同じ速さ（`IntroPanel.Speed`。移すなら 元の長さ÷総尺、コピーなら 元の長さ÷(総尺−秒数)）。範囲の長さ = 秒数 × 速さ
  （`SourceLength`、逆は `SecondsFor`）。秒数は 0.1 秒刻み・1 秒〜`MaxSeconds`。バーで端を動かすと秒数が決まり（`EditRange`。動かさない端はそのまま）、
  秒数を入れたり倍速が変わったり（移す／コピー・総尺）したら、範囲の終わりはそのままで始まりを動かす（`RangeFromEnd`）。範囲ごとずらすのは `MoveRange`。
  範囲は動画ごとなので覚えない（動画を入れ替えたら「最後の ○ 秒」。`SetSource`）。オンオフ・秒数・移す／コピー・フェードは覚える。
  動画は `MediaElement` で再生し、開けない（HEVC の拡張機能が無いなど）か HDR なら、バーの位置の画を `RenderSourceFrameAsync` で描く
  （描いている途中に動かしたら、終わってから最後の位置だけを描く）。複数ファイルは、その時刻を含むファイルを開き、終わったら次へ。
- 複数ファイルは concat の連結リストに `duration` を書く（無いと途中へ移動できず遅い）。
  連結リストの途中への `-ss` は、音の無い動画で時刻が半端だと ffmpeg の丸めのせいで失敗し、先頭から読み直して遅くなる。
  そのため完成品の部分とプレビューは、`Locate` でその時刻を含むファイルから読む。
- プレビュー: 出来上がりの最初・真ん中・最後の1コマを、変換と同じフィルター（`FinishVideoAsync`）で 360×640 の PNG にする。終わりぎわで画が出なければ2秒前で描き直す。
- 動画のプレビュー（`RenderPreviewClipAsync`）: 変換（`ConvertInAsync`）に `Clip` を渡し、最初の数秒（完成品＋本編 2 秒）だけを 360×640・BGM 入りで作る。
  本編の入力は `-t` で必要なぶんだけ読む。BGM のフェードアウトは本物の位置のまま（なので普通は出ない）。画面の `MediaElement` で再生し、
  開けなければ「ほかのアプリで開く」。一時ファイルは `%TEMP%\timelapse-preview-*.mp4`（入れ替え・閉じるときと、起動時に 1 時間より前のものを消す）。
  WinCheck は作成した動画の最初と1コマずつ明るさを比べる。再生は GitHub の PC に音の機器が無いことがあるので「参考」行に出すだけ。
- HDR（HLG / PQ）は zscale で SDR に。横向きの動画は「中央を切り抜く」か「ぼかし背景」（背景は 1/4 の解像度でぼかす）。
- 文字: ASS を作って libass で描く。フォントは作業フォルダにコピーして `fontsdir` で渡す。ファミリー名は Windows 用の名前（`GlyphTypeface.Win32FamilyNames` の en-US）を使う（typographic な名前だと別のフォントになる）。背景あり（帯）は `BorderStyle 3`。
- BGM: `-stream_loop` → `atrim` → `volume` →（上げたときだけ）`alimiter` → `afade`。音量は `volumedetect` で測る。
- エンコーダー: h264_nvenc → h264_qsv → h264_amf → h264_mf → libopenh264 の順に1コマ試し書きして選ぶ（LGPL 版 FFmpeg に libx264 は無い）。
- 同梱物: FFmpeg は BtbN の LGPL 版（安定版の最新を自動で選ぶ。2026年11月は 9.0）。**LGPL 版には GPL のフィルター（boxblur など）が入っていない**ので、
  フィルターを足すときは LGPL 版にあるか確かめる（BtbN の linux64-lgpl 版を GitHub から取ってきて `-filters` で見られる）。
  WinCheck は、ぼかした背景・HDR・高速モード・2本つなぎも実際に作って確かめる（`CheckOtherPathsAsync`）。Google Fonts は google/fonts の commit `bbda7253fc363238b05d97515ca71f8ae49c4fa9` に固定した 9 書体と、その OFL。ライセンス文が配られていない書体は入れない。
- 画面: 横並びの 3 列（長さ・構成 / BGM / テキスト）と、右のプレビュー欄。プレビュー枠は 9:16 で、欄の幅は高さに合わせて変える（左の設定は 890 より狭くしない）。起動時に作業領域に収める。1280×720（150% 表示のノート PC）の画面にも収まること。
- 色（ダークモード）: .NET 10 の Fluent テーマ（`Application.ThemeMode`、試験的な機能なので csproj で WPF0001 を抑えている）。
  色は直接書かず `{DynamicResource TextFillColorSecondaryBrush}` などテーマの色を使う（プレビュー枠の黒と音量バーの緑・赤だけは固定）。
  自分で作る Style は `BasedOn="{StaticResource {x:Type …}}"` にする（付けないと Fluent の見た目にならない）。
  窓の背景は `ApplicationBackgroundBrush` を明示している（Fluent の既定は透明＝Mica）。
- 保存先: `Ffmpeg.ConvertAsync` の最後の引数 `outputDir`（null なら1本目の動画と同じフォルダ）。決まったフォルダが無ければ、変換を始める前に確かめる。
- 作成が終わったとき（`AfterDone`、設定画面の内容をそのとき読むので、作成中に変えても効く）: 音は `MessageBeep`、
  フォルダは `explorer /select`。スリープ（`DoneSleep`、ほかの設定と同じく覚える）は成功したときだけで、窓を前に出して
  `SleepWindow`（60 秒。Enter / Esc は「スリープしない」）→ `SetSuspendState(false, false, false)`。
  WinCheck は「作成する」を押さないので `AfterDone` は通らない。`SleepWindow` の返す値だけを確かめ、本当にスリープはさせない（CI の PC が止まる）。
- 設定画面のラジオボタンは2組あるので `GroupName` で分けている（同じパネルにあると1組として扱われる）。
- WinCheck: CI の画面は 1024×768 なので、窓をそれより大きくするために `WM_GETMINMAXINFO` と WPF の `_trackMaxWidthDeviceUnits` などを広げている。
