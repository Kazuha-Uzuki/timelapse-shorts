# タイムラプス縦動画メーカー

何時間もの動画を数分の縦長タイムラプス（1080×1920 / 30fps / H.264）にする Windows アプリ。
クラウド不要、PC上で完結します。

- 複数ファイルを名前順に連結
- 投稿先（YouTube ショート / Instagram / TikTok / X）のボタンに最大の長さを表示。総尺は自由に指定し、出力はそのコマ数ちょうどで切る
- 完成品（タイムラプスの最後の N 秒）を冒頭に移動（ON/OFF・秒数指定）。元動画はどこも1回しか読まない
- iPhone などの HDR 動画（HLG / PQ）は自動で SDR に変換
- 横向きの動画は 中央切り抜き / ぼかし背景 の2方式で縦長に
- BGM: 音楽ファイルをドラッグ＆ドロップ。短ければ繰り返し、動画の長さで切ってフェードイン/アウト（秒数指定）。AAC で出力
- テキスト: 内容・フォント・大きさ・色（縁取り付き）・位置を指定。字幕ファイル（ASS）にして libass で描く。
  フォントは同梱の Google Fonts 9 書体と、PC に入っているフォント（自分用にインストールしたものも含む）から選べる
- GPU（NVIDIA / Intel / AMD）を自動検出。無ければ Windows 標準 → CPU の順で代替
- 拾う間隔がキーフレーム間隔以上のときは、キーフレームだけ読む高速モードに自動切替
- 進捗バー・残り時間・中止

## 動作環境

- 必須: Windows 10 / 11（64ビット）。.NET と FFmpeg は同梱なのでインストール不要
- 下限の目安: Ryzen 5 3500U（4コア / 15W）・メモリ 8GB 程度のノートPC。これより遅くても動くが時間がかかる
- スペックで変わるのは処理時間だけで、出来上がる動画は同じ
- 一番重いのは元動画の読み込み（デコード）。キーフレームだけで済む長い動画は速く、4K を全フレーム読む場合が最も遅い
- 変換中のメモリ使用量は 1080p 入力で約 0.5GB（実測）

## ビルド（GitHub Actions）

`main` に push すると自動でビルドされます。

1. GitHub の **Actions** タブ → 最新の `build` を開く
2. 下の **Artifacts** にある `TimelapseShorts-win64` をダウンロード

### 配布用の Release を作る

GitHub の画面で **Releases** → **Draft a new release** → タグ欄に `v0.1.0` のような名前を入力して新規作成 → **Publish release**。
数分後、その Release に `TimelapseShorts-win64.zip` が自動で付きます。
公開リポジトリなら GitHub のアカウントがない人もダウンロードできるので、他人に配るときはこちら。

## 構成

| ファイル | 中身 |
|---|---|
| `src/Ffmpeg.cs` | 変換ロジック（エンコーダー検出・間引き・冒頭移動・HDR変換・縦長化・BGM・文字入れ） |
| `src/FontCatalog.cs` | フォントの一覧と、libass に渡すフォントファイル・ファミリー名（Windows 用の名前）の取得 |
| `src/MainWindow.xaml(.cs)` | 画面。各サービスの上限が変わったら `Platforms` の数字を書き換える |
| `tests/WinCheck` | ビルド時に Windows 上で画面を作り、同梱の ffmpeg とフォントで実際に変換して確かめるテスト |
| `.github/workflows/build.yml` | exe ビルド、LGPL版 FFmpeg と Google Fonts の同梱、テスト、zip 化 |
| `使い方.txt` | 利用者向け説明。配布 zip には `README.txt` という名前で入る（日本語のファイル名は解凍ソフトによって文字化けするため） |

## ライセンスまわりの注意

- 同梱する FFmpeg は **LGPL 版**（[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)）。GPL の libx264 は含まれないので、アプリ本体のソースを公開する義務は生じません。FFmpeg のライセンス文と入手元は zip に同梱されます。
- 同梱の Google Fonts は **SIL Open Font License**。配布 zip の `fonts\licenses` に各書体のライセンス文を入れている。ライセンス文が配られていない書体（例: M PLUS Rounded 1c）は同梱しない。
- H.264 の特許ライセンスは別の話で、GPU や Windows 標準のエンコーダーはそれぞれのメーカー・OS側でカバーされています。CPU代替の OpenH264 はソースからビルドされたものなので、Cisco の特許ライセンス対象外です。有償で大規模に配布する場合は確認してください。
