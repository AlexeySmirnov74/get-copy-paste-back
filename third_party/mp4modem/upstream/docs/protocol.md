# MP4Modem Pack / Transport V2 技術仕様

## 1. 状態

本書はMP4Modem 0.1.0の実装仕様を記録する。Transport V2は実験仕様であり、互換性を維持
すべきフィールドと、今後変更可能な運用値を区別する。

規定語「必須」「禁止」「推奨」は、それぞれ相互運用に必要な要件、受理してはならない条件、
実装上の推奨を表す。

## 2. 用語

- **Pack**: 可視データ映像を収録したMP4ファイル。通常の拡張子`.mp4`を使う。
- **Payload Bundle**: Packへ格納する決定的archive。推奨拡張子は`.m4pack`。
- **Transport**: Payload Bundleをframe列へ変換し、復元する方式。
- **profile**: modulation、tile size、FEC、encoder bitrateなどの組合せ。
- **cover**: Packの先頭または末尾に置く、人が見るための静止画frame。

## 3. 標準Pack

既定profile `v2-4basis-bpsk-16` は次を使用する。旧CLI名
`v2-basis-4bpsk-16`はprofile ID `0x81`への入力aliasとしてのみ受理する。

| 項目 | 値 |
|---|---|
| container | MP4 |
| video codec | H.264 High Profile |
| decoded pixel format | YUV420p |
| size | 1280×720 |
| frame rate | 30fps |
| color metadata | BT.709、TV range |
| audio | なし |
| encoder GOP | 30 |
| B frames | 2 |
| reference frames | 3 |
| encoder target bitrate | 12Mbps |

デコーダーはcontainer brand、metadata、track順、B/P/I frame構成へ依存してはならない。
最初のvideo trackをdecoded YUV420p frame列として扱う。

## 4. Payload Bundle v1

Payload Bundleは、4-byte magic `M4PK`に決定的ZIP byte列を連結する。

```text
offset  size  value
0       4     ASCII "M4PK"
4       ...   ZIP archive
```

ZIPには`manifest.json`を1個と、`files/`以下のpayloadを格納する。encoderは全entryを
`ZIP_STORED`（method 0）でpathのUnicode code point辞書順に格納し、ZIP timestampを
`1980-01-01 00:00:00`へ固定する。pathは`/`区切り、UTF-8とする。既存試験Bundleを読むため、
decoderはmethod 8（DEFLATE）もdecode-onlyで受理する。

manifestはUTF-8 JSONであり、keyを辞書順、余分な空白なし、末尾LF付きでserializeする。

```json
{
  "createdAt": "1980-01-01T00:00:00Z",
  "files": [
    {
      "mime": "text/plain",
      "path": "files/example.txt",
      "sha256": "...",
      "size": 7
    }
  ],
  "format": "mp4modem.payload-bundle",
  "title": "Example",
  "version": 1
}
```

`createdAt`を省略したencoderは`1980-01-01T00:00:00Z`を使う。同一の入力、title、
`createdAt`からbyte-identicalなPayload Bundleを生成しなければならない。

decoderは次を拒否する。

- 絶対path、`..`、Windows drive path、symlink
- 空component、`.`、control文字、末尾のdot/space、Windows予約名
- Unicode NFC正規化後の重複、または大文字小文字だけが異なるpath衝突
- 重複ZIP entry、directory entry、非canonical path
- 240文字を超えるpath
- 1,000を超えるpayload file
- 20MiBを超えるPayload Bundle
- 100MiBを超える展開後合計size
- 200倍を超えるZIP entry圧縮率
- manifestとのpath、size、SHA-256不一致
- 既存の非空展開先

encoderは入力pathとtitleをUnicode NFCへ正規化する。portable衝突の比較はNFC適用後の
Unicode lowercaseで行う。Windows予約名は各path componentの最初のdotより前をASCII
case-insensitiveに比較し、`CON`、`PRN`、`AUX`、`NUL`、`COM1`〜`COM9`、`LPT1`〜`LPT9`
を禁止する。

## 5. Transport V2 frame layout

### 5.1 block grid

1280×720のY planeを16×16 pixelのblockへ分割する。gridは80列×45行、合計3,600 blockで
ある。block indexはrow-majorの`row * 80 + column`とする。U/V planeは全pixel 128とする。

各blockは4bitを表し、次の4つのbasisへBPSK符号を割り当てる。

```text
Y(x,y) = clip(
  128
  + s0 * 40
  + s1 * 24 * horizontal(x)
  + s2 * 24 * vertical(y)
  + s3 * 24 * diagonal(x,y),
  16,
  240
)
```

各`si`はbit 0で`-1`、bit 1で`+1`である。horizontalとverticalはblock中央で`-1`から
`+1`へ切り替わり、diagonalは両者の積である。

### 5.2 reserved block

1 frameのblock用途は次の通りである。

| 用途 | block数 | byte相当 |
|---|---:|---:|
| compact tag 3 copies | 96 | 48 |
| pilot | 192 | 96 |
| data | 3,312 | 1,656 |

tag copyは各32 blockで、開始indexを`0`、`1784`、`3568`とする。これら96 blockの集合を
`H`、`[0, 3599]`から`H`を除いた昇順arrayを`E`とする。pilot index `P[i]`は次で定める。

```text
P[i] = E[floor((i + 0.5) * len(E) / 192)]  for i = 0..191
```

pilot valueは`i mod 16`、bit順はMSB-firstとする。data blockは`[0, 3599]`からtagとpilotを
除いたindexを昇順に並べる。byteはMSB-firstで4bitずつdata blockへ割り当てる。

decoderは各basisについてpilotの0群と1群の平均を求め、その中点を判定thresholdとする。
tagは3 copiesのbitごとの多数決で復元する。

### 5.3 compact tag

tagはnetwork byte orderで16 bytesである。

```text
2s magic       "M2"
B  version     2
B  profile_id  0x81
H  epoch
B  phase
B  flags
I  symbol_id
I  crc32       preceding 12 bytes
```

`flags & 0x01`が1ならcontrol frame、0ならdata frameである。現profileでは`epoch`と
`phase`は0とする。data frameの`symbol_id`は0から始まる連番である。

### 5.4 control frame

control frameはdata frame 119枚ごと、その直前へ1枚置く。96-byte control recordを
data領域へ17回反復し、余りを0でpadする。decoderは17 copiesをbit単位で多数決する。

control recordは次のfieldをnetwork byte orderで格納する。

```text
4s magic                 "M4V2"
B  transport_version     2
B  profile_id            0x81
B  flags                 0
B  reserved              0
16s package_id           Payload Bundle SHA-256の先頭16 bytes
Q  original_size
Q  encoded_size
I  data_frame_count
B  fec_symbols           64
H  data_bytes_per_frame  1652
32s bundle_sha256
I  crc32                 preceding 79 bytes
13s padding              zero
```

### 5.5 data frame

data領域1,656 bytesのうち、先頭1,652 bytesをFEC stream、末尾4 bytesを先頭部分の
CRC-32とする。最後のframeで不足するbyteは0でpadする。

CRCが一致しないframeでも、tagを読めた場合は観測した1,652 bytesをReed–Solomon decoderへ
渡す。tagを読めないframeだけを、そのframeが担当するbyte範囲のerasureとして扱う。

本仕様のCRCはIEEE CRC-32であり、初期値・最終XORを含む`zlib.crc32(data) & 0xffffffff`と
同値でなければならない。check vectorはASCII `123456789`に対する`0xcbf43926`である。

## 6. FECとinterleave

Payload Bundleを187-byte chunkへ分割し、最後のchunkを0でpadする。各chunkへbig-endian
CRC-32を付加して191 bytesとし、RS(255,191)で64 parity symbolsを追加する。Galois fieldは
GF(2^8)、primitive polynomial `0x11d`、generator `2`、first consecutive root `0`、
symbol size 8bitとする。

複数の255-byte RS blockは、全blockのcolumn 0、全blockのcolumn 1、…の順にcolumn-wise
interleaveする。block数を`B`とすると、正確なmappingは次である。

```text
interleaved[column * B + block] = rsBlocks[block][column]
```

復号時は逆変換し、RS訂正後にchunk CRCを検証して`original_size`へ切る。1 blockで訂正可能な
既知でないerror数を`e`、erasure数を`v`とすると、成功条件は`2e + v <= 64`である。

## 7. frame sequence

標準sequenceは次の通りである。

```text
45 cover frames (1.5s)
control 0
data 0..118
control 1
data 119..237
...
15 end-cover frames (0.5s)
```

cover durationはAPIで変更できる。秒からframeへの変換はPython固有のroundではなく、
`floor(seconds * 30 + 0.5)`（round-half-up）とする。cover frameはtransportとして読めなくて
よく、decoderは有効なtagを持つframeだけを収集する。

MP4 decoderはB frameを含むpacket decode順ではなく、presentation順にframeを返さなければ
ならない。重複data frameは許容し、同一`symbol_id`ではCRC-validな観測を優先し、同じCRC
状態なら最初の観測を保持する。frame dropは不足範囲をerasureとする。すべてのcontrol frameが
失われた場合、data frameが揃っていてもbootstrap情報を得られないため復号は失敗する。

## 8. 成功条件

decoderが成功を返すには、次をすべて満たさなければならない。

```text
control constraints valid
AND Reed-Solomon decode successful
AND Payload Bundle SHA-256 match
AND manifest valid
AND every file size match
AND every file SHA-256 match
```

途中まで復元したfileを成功として保存してはならない。

## 9. optional MP4 UUID fast path

encoderは任意で、Payload Bundleをcustom top-level `uuid` boxへ格納できる。これは同一MP4の
高速decode用であり、Pack transportの必須部分ではない。再圧縮やremuxで削除され得るため、
耐性試験では使用してはならない。

公開interfaceはsource policyを明示する。`visible`を既定とし、`uuid`はUUIDだけを読む。
`cross-check`は可視transportとUUIDの両方を復号し、Payload Bundle byteが完全一致する場合だけ
成功する。不一致は曖昧な入力として拒否する。

- UUID: `fb1c8f29-648d-5c2e-9e0e-405a821c7535`
- box payload magic: `M4MU`
- layout: UUID、magic、Payload Bundle SHA-256、8-byte length、Payload Bundle

## 10. 旧実験形式との互換性

0.1.0 decoderは、改名前に生成された試験Packを検証するため、次をdecode-onlyで受理する。

| 現行 | 旧識別子 |
|---|---|
| Payload Bundle `M4PK` | `FPK1` |
| manifest `mp4modem.payload-bundle` | `framepack.bundle` |
| V1 frame `M4M1` | `FPK1` |
| V2 tag `M2` | `F2` |
| V2 control `M4V2` | `FPV2` |
| UUID / `M4MU` | `9f6af66b-7e33-41d8-9fd8-135b2f504b31` / `FPKU` |

encoderは現行識別子だけを生成する。旧名称は製品名ではなく、wire compatibilityのために
残したbyte列である。

## 11. Golden vectors

PythonとTypeScript実装は[`test-vectors/transport-v2.json`](../test-vectors/transport-v2.json)の
固定vectorをCIで共有する。MP4 byte列はH.264 encoderとcontainer実装で変化するためnormative
vectorにしない。normativeなのはPayload Bundle、FEC stream、tag、raw I420 frameである。

| vector | 値 |
|---|---|
| CRC-32(`123456789`) | `cbf43926` |
| tag (`profile=81, epoch=7, phase=2, flags=0, symbol=12345`) | `4d3202810007020000003039d8e867e3` |
| canonical 2-file Bundle | 718 bytes, SHA-256 `c118de01c259019a7235418216192efc13160ef2b71802da973e13c29af6375a` |
| 上記BundleのFEC stream | 1,020 bytes, SHA-256 `a41627d22da8338dc765be77228c96580f7fb0cea9777910839a2bc89dd728b7` |
| basis raw I420 frame | 1,382,400 bytes, SHA-256 `678a62001e87d7b9c6bdffb99f8eb65d2c0fd6b41171442e79d31c89b71a49d3` |

2-file Bundleの論理入力はtitle `Example`、固定createdAt、`a.txt`=`hello`、
`b.json`=`{"ok":true}`とする。raw frameは上記tagと、byte `i`を`(i * 37) mod 256`とした
1,656-byte payloadを使用する。

## 12. 容量と標準duration gate

Payload Bundle parserの20MiB上限と、1本の標準Packへ収まる容量を混同してはならない。既定の
1.5秒cover、0.5秒end-cover、120秒上限では、最大Payload Bundleは4,252,193 bytes、全体は
3,600 frameである。encoderはこの境界を超える入力をH.264処理前に拒否する。decoderは旧local
長尺Packを検証できるよう20MiB相当まで受理する。

## 13. Security properties

CRCとSHA-256が提供するのは偶発的・意図的な破損の検出であり、送信者認証ではない。本仕様は
署名、MAC、暗号化、アクセス制御を提供しない。Packを取得した人はPayloadを読め、誰でも
正しいhashを持つPackを生成できる。秘密情報は別layerで暗号化・認証しなければならない。

data frameは大面積が30Hzで変化し得る。光感受性に対する安全性は確認されていない。UIはPackを
自動再生またはloopせず、静止coverを標準表示し、再生前に警告する。

## 14. 非保証

本仕様は、任意のSNS、codec、bitrate、解像度変換、crop、frame補間に対する復元を保証
しない。profile採用には、元MP4の自己復元、規定transcode matrix、実サービス往復での
完全復元をそれぞれ実測する必要がある。
