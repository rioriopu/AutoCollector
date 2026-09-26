# AutoCollector を配置先へ同期する。
#
# **DLL を 1 本だけ写さない。**
# AutoCollector.dll だけを手で写していたため、共有先の EstellUtils.dll が
# 10 時間以上古いままになっていた（2026-09-26 実測）。
# 自作 UI ライブラリが古いと、画面の不具合が直したはずの形で残り続ける。
#
# ビルドは csproj の OutputPath により C:\DevPlugins\AutoCollector\ へ出る
# （Debug 構成のときだけ。Release では出ない）。
# ここはそれを共有先へ丸ごと写す。

param(
    [string]$Source = 'C:\DevPlugins\AutoCollector',
    [string]$Target = '\\rio-pc\DevPlugins\AutoCollector'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) {
    Write-Error "配置元がありません: $Source（-c Debug でビルドしましたか）"
}

if (-not (Test-Path $Target)) {
    Write-Error "配置先がありません: $Target"
}

# 中身を丸ごと合わせる。消えたファイルは残さない。
# /MIR は配置先を配置元と同じにする（余分なファイルを消す）。
# /NJH /NJS /NP /NDL は出力を静かにする。
robocopy $Source $Target /MIR /NJH /NJS /NP /NDL | Out-Null
$code = $LASTEXITCODE

# **robocopy の終了コードは 0 が「何もしなかった」。**
# 1 は「写した」で正常。8 以上が失敗。
# そのまま成否に使うと、写したのに失敗と読む。
if ($code -ge 8) {
    Write-Error "同期に失敗しました（robocopy 終了コード $code）"
}

# このあとの照合で成否を決めるので、ここで終了コードを戻しておく。
$global:LASTEXITCODE = 0

# **写したことを確かめる。** 写したつもりを残さない。
$mismatch = @()

Get-ChildItem -Path $Source -File | ForEach-Object {
    $dst = Join-Path $Target $_.Name
    if (-not (Test-Path $dst)) {
        $mismatch += "$($_.Name): 配置先に無い"
        return
    }

    $a = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    $b = (Get-FileHash $dst -Algorithm SHA256).Hash

    if ($a -ne $b) {
        $mismatch += "$($_.Name): 中身が違う"
    }
}

if ($mismatch.Count -gt 0) {
    $mismatch | ForEach-Object { Write-Host "NG  $_" }
    Write-Error '同期後の照合で食い違いがありました'
}

Get-ChildItem -Path $Source -File |
    Sort-Object Name |
    ForEach-Object {
        $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 12)
        Write-Host ("OK  {0,-32} {1}" -f $_.Name, $h)
    }

Write-Host ''
Write-Host "同期しました: $Source → $Target"
