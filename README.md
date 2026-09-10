# VisualSummary (mmd2pdf)

会話の要約などを [Mermaid](https://mermaid.js.org/) 記法で書いたテキストから図を描き、PDF にして開く Windows 用のコマンドラインツールです。

- exe 単体で動作（.NET Framework 4.x と Microsoft Edge は Windows 10/11 に標準搭載）
- mermaid.js を exe に埋め込んでいるのでオフラインで動作
- ファイル・標準入力のどちらからでも入力可能
- Markdown 内の複数の ```` ```mermaid ```` ブロックは 1 図 1 ページの PDF に
- ページサイズは図の大きさに自動で合わせる

## ビルド

`build.bat` を実行すると `mmd2pdf.exe` ができます。SDK のインストールは不要です（Windows 標準の `csc.exe` を使用）。初回は mermaid.min.js を jsDelivr から取得します。

## 使い方

```bat
rem ファイルから
mmd2pdf.exe samples\02_support_sequence.mmd

rem 標準入力から（出力先を指定し、既存ファイルは上書き）
type input.mmd | mmd2pdf.exe -o "C:\output\summary.pdf" --overwrite
```

exe に `.mmd` ファイルをドラッグ＆ドロップしても使えます。

| オプション | 説明 |
|---|---|
| `-o`, `--output` | 出力 PDF のパス。省略時は入力と同じ場所・同じ名前の `.pdf`（標準入力の場合はカレントフォルダの `diagram.pdf`） |
| `-y`, `--overwrite` | 出力 PDF が既にあれば上書きする（指定しない場合はエラー） |
| `-t`, `--theme` | `default` / `neutral` / `dark` / `forest` / `base` |
| `-e`, `--encoding` | メッセージをリダイレクトで受け取る場合の文字コード `utf8` / `sjis`（省略時はシステム既定。日本語 Windows では Shift_JIS） |
| `--no-open` | 生成後に PDF を開かない |

入力ファイルを省略すると標準入力から読み込みます（`-` を指定しても同じ）。文字コードは UTF-8 / UTF-16 / Shift_JIS を自動判別します。

### 終了コード

| コード | 意味 |
|---|---|
| 0 | 成功（Mermaid の記法エラーの場合も、エラー内容を記した PDF を出力して 0 を返す） |
| 1 | エラー（入力なし、出力先が既に存在、PDF 生成失敗など） |
| 2 | 引数の誤り（不明なオプション、未対応のテーマ・文字コードなど） |

## 注意点

- ER 図のエンティティ名に日本語は直接使えません。英語名にして、定義ブロックで別名を付けてください（例: `CUSTOMER["顧客"] { ... }`）。`samples/08_data_er.mmd` を参照。
- Windows PowerShell 5.1 からパイプで渡すと日本語が `?` に化けます。先に `$OutputEncoding = [Text.Encoding]::UTF8` を実行してください。

## サンプル

`samples/` にフローチャート、シーケンス図、マインドマップ、ガントチャート、状態遷移図、タイムライン、円グラフ、ER 図のサンプルがあります。

## サードパーティ

exe には [mermaid](https://github.com/mermaid-js/mermaid)（MIT License, Copyright (c) 2014 - 2022 Knut Sveidqvist）を埋め込んでいます。
