# 開発とビルド

Windows x64 / .NET 10 SDKを使用します。以下はソースのルートで実行します。

```powershell
dotnet publish OnimushaDualSense -c Release -o publish --configfile NuGet.Config
./tools/package-release.ps1 -PublishDirectory publish -OutputDirectory release
```

`release` が配布用です。ゲーム音源、ログ、バックアップ、開発ツールを含めず、Setup前の状態でZIPにしてください。`UseAppHost=false` により本体はDLL形式です。PortAudioのみネイティブDLLを同梱しています。

インスペクターとテストを含む開発版:

```powershell
dotnet publish OnimushaDualSense -c Release -p:DeveloperTools=true -o publish-dev --configfile NuGet.Config
./tools/package-release.ps1 -PublishDirectory publish-dev -OutputDirectory development -Developer
dotnet development/bin/OnimushaDualSense.dll test
python -m pip install lupa==2.8
python dev/test_lua.py
```

開発版も `Setup.cmd` で音源を準備できます。その後 `test` を再実行すると実音源・記録済みイベントも検証します。`verify-prepared` は生成WAV全件と再生分岐を確認します。インスペクターは `Inspect-Haptics.cmd` で起動します。これらは配布版にはコンパイルされません。

通常起動は生成済みWAVを読み、音源キャッシュは64MiBです。`gain` は出力時に一度だけ適用します。強制再生成は `dotnet bin/OnimushaDualSense.dll prepare-waves --force` です。

`run --seconds 3` は接続と終了処理の確認用です。新しいゲーム通知があると出力するので、無音確認はゲーム終了中に行ってください。ライセンスは `distribution/LICENSE`、第三者告知は `distribution/THIRD_PARTY_NOTICES.txt` を参照してください。
