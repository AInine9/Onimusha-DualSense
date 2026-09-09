using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OnimushaDualSense;

static class Inspector
{
    internal record Entry(string key, string time, string kind, string id, string label, string category, string origin, string sample, long sequence, long frame);
    static readonly Regex Play = new(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (?<kind>Extended|Authored) haptic PLAY id=(?<id>\S+) event=(?<seq>\d+) frame=(?<frame>\d+)(?: sample=(?<sample>\S+))?", RegexOptions.Compiled);
    static readonly Regex Trigger = new(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) Trigger=(?<id>-?\d+)", RegexOptions.Compiled);
    static readonly Dictionary<uint, SoundReference> Sources = SoundCatalog.Entries.ToDictionary(e => e.Event);
    internal static Entry? Parse(string line)
    {
        var m = Play.Match(line);
        if (!m.Success)
        {
            m = Trigger.Match(line); if (!m.Success) return null;
            string id = m.Groups["id"].Value;
            return new(line, m.Groups["time"].Value, "trigger", id, id switch { "0" => "弓のトリガー抵抗", "1" => "吸収のトリガー抵抗", _ => "トリガー抵抗を解除" }, "trigger", "ゲームのトリガー通知", "", 0, 0);
        }
        string feedback = m.Groups["id"].Value, kind = m.Groups["kind"].Value, sample = m.Groups["sample"].Value;
        string category = "original", label = "ゲーム内蔵のハプティクス", origin = "元の5波形";
        if (kind == "Extended")
        {
            if (feedback.StartsWith("defense_sound_", StringComparison.Ordinal))
            {
                string defenseLabel = feedback switch
                {
                    "defense_sound_250823922" or "defense_sound_1644781178" => "弾きの接触音",
                    "defense_sound_1902560855" or "defense_sound_4244289853" or "defense_sound_3420340655" => "受け流しの終了音",
                    _ => "受け流しの接触・擦れ音"
                };
                return new(line, m.Groups["time"].Value, "extra", feedback, defenseLabel, "defense", "防御位置の実音声イベント・重ね合わせ", sample,
                    long.Parse(m.Groups["seq"].Value), long.Parse(m.Groups["frame"].Value));
            }
            var number = Regex.Match(feedback, @"^(?:ui_|parry_|cut_|issen_|deadheat_)?sound_(\d+)");
            if (number.Success && uint.TryParse(number.Groups[1].Value, out uint ev) && Sources.TryGetValue(ev, out var source))
            {
                category = source.Category;
                label = source.Family switch { "ui" => "メニュー音", "footsteps" => "足音", "attack" => "斬撃音", "guard" => "ガード音", _ => "衝突音" };
                if (feedback.StartsWith("issen_")) label = "崩し一閃の斬撃・接触音";
                if (feedback.StartsWith("deadheat_")) label = "相子剣戟の斬撃・接触音";
                if (feedback.StartsWith("parry_")) label = "受け流しの擦れ";
                if (feedback.EndsWith("_left")) label += " · 左";
                if (feedback.EndsWith("_right")) label += " · 右";
                origin = source.Source switch { "TrgPos" => "近傍の衝突音 ＋ プレイヤーの接触／防御通知", "GUI" => "実際のメニュー音声イベント", _ => "プレイヤーの音声イベント" };
            }
            else
            {
                var definition = ExtendedEffects.Definitions.FirstOrDefault(e => e.Id == feedback);
                category = definition?.Category ?? "unknown";
                label = feedback switch { "attack" => "攻撃開始", "hit" => "命中", "damage" => "被ダメージ", "guard" => "ガード", "dodge" => "回避", "perfect_dodge" => "ジャスト回避", "land" => "着地", "heal" => "体力回復", "soul" => "魂吸収", "pickup" => "アイテム取得", "lock_on" => "ロックオン", "power" => "鬼への変化", "finisher" => "鍔迫り合い／崩し", "ui_select" => "メニュー選択", "ui_decide" => "メニュー決定", "ui_cancel" => "メニュー取消", _ => feedback };
                origin = "ゲームの動作通知 → 合成波形";
            }
        }
        return new(line, m.Groups["time"].Value, kind == "Extended" ? "extra" : "original", feedback, label, category, origin, sample,
            long.Parse(m.Groups["seq"].Value), long.Parse(m.Groups["frame"].Value));
    }

    internal static string[] Tail(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool clipped = stream.Length > 524288;
        if (clipped) stream.Position = stream.Length - 524288;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (clipped) reader.ReadLine();
        return reader.ReadToEnd().Split('\n').Select(s => s.TrimEnd('\r')).ToArray();
    }

    public static void Launch() { using var child = AppHost.Launch("inspect-server"); }
    public static int Run(bool openBrowser = true)
    {
        using var mutex = new Mutex(false, @"Local\OnimushaHapticInspector", out bool created);
        string infoPath = Files.Data("inspector.json");
        if (!created)
        {
            if (openBrowser) Process.Start(new ProcessStartInfo(Files.Read(infoPath)["url"]!.GetValue<string>()) { UseShellExecute = true });
            return 0;
        }
        var config = Configuration.Read();
        float gain = config.Gain;
        var prepared = PreparedWaves.Load();
        using var samples = prepared.Samples; var extensions = prepared.Effects; SampleStore.FinishLoading();
        var previews = new Dictionary<string, object>();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string prefix = "/" + Guid.NewGuid().ToString("N") + "/", url = $"http://127.0.0.1:{port}{prefix}";
        Files.Atomic(infoPath, new { running = true, url });
        Console.WriteLine(url);
        if (openBrowser) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        double lastRequest = Files.Now;
        try
        {
            while (Files.Now - lastRequest < 120)
            {
                using var timeout = new CancellationTokenSource(1000);
                TcpClient client;
                try { client = listener.AcceptTcpClientAsync(timeout.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { continue; }
                using (client)
                {
                    try
                    {
                        client.ReceiveTimeout = client.SendTimeout = 2000;
                        using var stream = client.GetStream();
                        // Bound the request before interpreting it. This server has only read-only endpoints.
                        var head = new List<byte>();
                        while (head.Count < 16384)
                        {
                            int b = stream.ReadByte(); if (b < 0) break; head.Add((byte)b);
                            if (head.Count >= 4 && head[^4] == 13 && head[^3] == 10 && head[^2] == 13 && head[^1] == 10) break;
                        }
                        string first = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n")[0];
                        string[] parts = first.Split(' ');
                        if (parts.Length != 3 || parts[0] is not ("GET" or "POST") || !parts[1].StartsWith(prefix, StringComparison.Ordinal)) { Reply(stream, "404 Not Found", "text/plain", "Not found"); continue; }
                        string route = parts[1][prefix.Length..]; lastRequest = Files.Now;
                        if (parts[0] == "POST")
                        {
                            string headers = Encoding.ASCII.GetString(head.ToArray());
                            if (!headers.Contains("X-Inspector-Request: 1", StringComparison.OrdinalIgnoreCase) || !(route == "stop" || route.StartsWith("play?sample=", StringComparison.Ordinal)))
                            { Reply(stream, "403 Forbidden", "application/json", "{}"); continue; }
                            string selected = route == "stop" ? "" : Uri.UnescapeDataString(route[12..].Split('&')[0]);
                            bool sound = route.Split('&').Contains("audio=1");
                            string volumeText = route.Split('&').FirstOrDefault(v => v.StartsWith("volume="))?[7..] ?? "0.5";
                            if (!float.TryParse(volumeText, NumberStyles.Float, CultureInfo.InvariantCulture, out float volume) || !float.IsFinite(volume) || volume < 0 || volume > 1) { Reply(stream, "400 Bad Request", "application/json", "{}"); continue; }
                            if (selected.Length > 0 && !samples.ContainsKey(selected)) { Reply(stream, "404 Not Found", "application/json", "{}"); continue; }
                            string token = Guid.NewGuid().ToString("N");
                            if (!Files.Atomic(Files.Data("audition-request.json"), new { token, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sample = selected, audio = sound, volume }))
                            { Reply(stream, "503 Unavailable", "application/json", "{}"); continue; }
                            Mutex? companion = null;
                            if (selected.Length > 0 && !Mutex.TryOpenExisting(@"Local\OnimushaDualSenseBridge", out companion))
                                AppHost.Launch("run").Dispose();
                            else if (selected.Length > 0) companion?.Dispose();
                            Reply(stream, "200 OK", "application/json", JsonSerializer.Serialize(new { token }));
                            continue;
                        }
                        if (route == "") Reply(stream, "200 OK", "text/html; charset=utf-8", Page);
                        else if (route == "api")
                        {
                            string[] lines = Tail(Files.Data("bridge.log"));
                            int begin = Array.FindLastIndex(lines, l => l.Contains("C# companion: USB HID"));
                            var entries = lines.Select(Parse).Where(e => e != null).TakeLast(600).ToArray();
                            JsonNode? status = null; try { status = Files.Read(Files.Data("status.json")); } catch (Exception e) when (e is IOException or JsonException) { }
                            Reply(stream, "200 OK", "application/json", JsonSerializer.Serialize(new { entries, status, audition = ReadAudition(), gain, server_time = DateTimeOffset.Now, log_clipped = begin < 0 }));
                        }
                        else if (route.StartsWith("wave?sample=", StringComparison.Ordinal))
                        {
                            string id = Uri.UnescapeDataString(route[12..]);
                            if (!samples.TryGetValue(id, out var wave)) { Reply(stream, "404 Not Found", "application/json", "{}"); continue; }
                            if (!previews.TryGetValue(id, out var preview))
                            {
                                int frames = wave.Length / 2; double peak = 0, energy = 0;
                                var left = new List<double[]>(); var right = new List<double[]>();
                                for (int n = 0; n < 180; n++)
                                {
                                    double[] lo = [0, 0], hi = [0, 0];
                                    for (int f = n * frames / 180; f < (n + 1) * frames / 180; f++)
                                        for (int c = 0; c < 2; c++)
                                        {
                                            double v = Math.Clamp(wave[f * 2 + c] * gain, -.85f, .85f);
                                            lo[c] = Math.Min(lo[c], v); hi[c] = Math.Max(hi[c], v); peak = Math.Max(peak, Math.Abs(v)); energy += v * v;
                                        }
                                    left.Add([lo[0], hi[0]]); right.Add([lo[1], hi[1]]);
                                }
                                string material = "合成波形", bank = "";
                                var defenseMatch = Regex.Match(id, @"^ext:defense_sound_(\d+)#(\d+)$");
                                if (defenseMatch.Success)
                                {
                                    var manifest = Files.Read(Files.Bundled("defense_haptics.json"));
                                    var layers = manifest["events"]![defenseMatch.Groups[1].Value]!["variants"]!.AsArray()[int.Parse(defenseMatch.Groups[2].Value)]!["layers"]!.AsArray();
                                    bank = manifest["bank"]!.GetValue<string>();
                                    material = "同時再生音源 " + string.Join(" / ", layers.Select(layer => $"{layer!["source_media"]} ({layer["pitch_cents"]} cent, {layer["volume_db"]} dB)"));
                                }
                                var match = Regex.Match(id, @"^ext:(?:ui_|parry_|cut_|issen_|deadheat_)?sound_(\d+)(?:_(?:left|right))?#(\d+)$");
                                if (match.Success && Sources.TryGetValue(uint.Parse(match.Groups[1].Value), out var source))
                                {
                                    bank = source.Bank;
                                    if (extensions.SoundInfo.TryGetValue(id, out var info))
                                    {
                                        string dynamic = (info.DynamicRtpc || info.DynamicState) ? " · 動的補正は既定値" : "";
                                        string random = info.Randomized ? $" · ランダム標本 {info.Draw + 1}/{info.DrawCount}" : "";
                                        material = $"参照音源ID {info.Media} · pitch {info.PitchCents:+0.##;-0.##;0} cent · delay {info.DelayMs:0.##} ms · volume {info.VolumeDb:+0.##;-0.##;0} dB{random}{dynamic}";
                                    }
                                }
                                previews[id] = preview = new { duration_ms = frames / 48.0, peak, rms = Math.Sqrt(energy / Math.Max(1, wave.Length)), left, right, material, bank };
                            }
                            Reply(stream, "200 OK", "application/json", JsonSerializer.Serialize(preview));
                        }
                        else Reply(stream, "404 Not Found", "text/plain", "Not found");
                    }
                    catch (Exception e) when (e is IOException or SocketException or JsonException) { }
                }
            }
        }
        finally { listener.Stop(); Files.Atomic(infoPath, new { running = false, url }); }
        return 0;
    }
    static JsonNode? ReadAudition()
    {
        try { return Files.Read(Files.Data("audition-status.json")); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
    static void Reply(Stream stream, string status, string type, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        stream.Write(head); stream.Write(bytes);
    }
    const string Page = """
<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>鬼武者 · 振動インスペクター</title>
<style>
:root{color-scheme:dark;font-family:system-ui,"Yu Gothic UI",sans-serif;background:#10141b;color:#e8edf4}*{box-sizing:border-box}body{margin:0;padding:28px;max-width:1600px;margin:auto}h1{font-size:25px;margin:0 0 6px}h2{font-size:18px;margin:0 0 16px}p{color:#a7b4c5;line-height:1.65;font-size:13px;margin:8px 0}button,select,input{font:inherit;color:inherit;background:#202936;border:1px solid #3b485a;border-radius:7px;padding:9px 12px}button{cursor:pointer}button:hover{background:#344254}button.on{background:#26584e;border-color:#66cbb0}.bar{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin:18px 0}.badge{padding:6px 10px;border-radius:16px;background:#27313e;font-size:12px}.ok{color:#87e0bc}.warn{color:#ffca80}.layout{display:grid;grid-template-columns:minmax(420px,1.15fr) minmax(340px,1fr);gap:20px}.panel{background:#181f29;border:1px solid #303b49;border-radius:12px;padding:20px}.scroll{height:520px;overflow:auto}table{width:100%;border-collapse:collapse;font-size:12px}th{text-align:left;position:sticky;top:0;background:#181f29;color:#a7b4c5;padding:9px 7px}td{padding:10px 7px;border-top:1px solid #2a3441}tr[data-key]{cursor:pointer}tr[data-key]:hover,tr.selected{background:#2a3b47}.dim{color:#92a2b6}.mono{font-family:ui-monospace,Consolas,monospace;overflow-wrap:anywhere}.chips{display:flex;gap:6px;flex-wrap:wrap}.chips button{font-size:12px;padding:7px 9px}.metrics{display:grid;grid-template-columns:repeat(3,1fr);gap:10px;margin:15px 0}.metric{background:#111923;padding:12px;border-radius:8px}.metric b{display:block;font-size:21px;margin-top:5px}.metric span{font-size:11px;color:#a7b4c5}canvas{display:block;width:100%;height:160px;border-radius:8px;background:#101720;margin:12px 0}label{font-size:12px;color:#bbc8d8}#memo{width:100%;margin:12px 0}#message{min-height:22px;color:#91d6c5}#search{min-width:180px;flex:1}#markNow{border-color:#cead66}small{color:#a7b4c5}.heading{display:flex;justify-content:space-between;gap:10px}.star{color:#f3bf62}.empty{padding:40px 10px;color:#a7b4c5;text-align:center}@media(max-width:850px){body{padding:14px}.layout{grid-template-columns:1fr}.scroll{height:360px}}
</style>
<div class="heading"><div><h1>振動インスペクター</h1><p>ゲームで操作 → ここへ戻る → 再生された振動を選ぶ</p></div><span class="badge" id="connection">接続中…</span></div>
<div class="bar"><span class="badge" id="activity">状態を取得中</span><span class="badge" id="gain"></span><button id="follow" class="on">最新を追う：ON</button><button id="clear">表示をクリア</button><button id="export">記録を書き出す</button></div>
<p>この画面へ切り替えるとMODの出力は止まります。ゲームを操作してから戻って確認してください。記録対象はMODの再生開始とトリガー設定です。</p>
<div class="layout"><section class="panel"><h2>再生のタイムライン <small id="count"></small></h2>
<div class="bar"><input id="search" placeholder="名前・イベントIDで絞り込み"><label><input type="checkbox" id="hideFeet"> 足音を隠す</label><label><input type="checkbox" id="onlyFlag"> 印のみ</label></div>
<div class="scroll"><table><thead><tr><th>時刻</th><th>検出した振動</th><th>目印</th></tr></thead><tbody id="rows"></tbody></table><div class="empty" id="empty">ゲーム内で操作すると、ここに振動が表示されます。</div></div>
<div class="bar"><select id="action"><option>通常ガード</option><option>受け流し</option><option>弾き</option><option>空振り</option><option>命中</option><option>被弾</option><option>歩行</option><option>走行</option><option>メニュー</option><option>その他</option></select><button id="markNow">直前5秒の出力に目印</button></div>
<p>目印は自分で確認した動作の記録です。通常ガード・受け流し・弾きの区別を音声IDだけから断定しません。</p></section>
<section class="panel"><h2 id="title">振動を選んでください</h2><p id="origin">検出元と、実際に選ばれた振動素材を確認できます。</p><div class="mono dim" id="ident"></div>
<div class="metrics"><div class="metric"><span>波形の長さ</span><b id="duration">—</b></div><div class="metric"><span>出力ピーク</span><b id="peak">—</b></div><div class="metric"><span>平均の強さ（RMS）</span><b id="rms">—</b></div></div>
<canvas id="wave" width="900" height="220"></canvas><small id="material">左／右の波形と設定ゲインを反映した出力値。実機の振動を測定した値ではありません。</small><p class="mono" id="bank"></p>
<div class="bar"><label><input type="checkbox" id="withSound" checked> 調整後の比較音も再生</label><label>音量 <input type="range" id="soundVolume" min="0" max="100" value="50"></label></div><p>比較音はWwise経路のピッチ・遅延・音量を反映し、Windowsの既定の音声出力へ流れます。音源が登録されていない合成振動などは、振動のみの試聴です。</p><div class="chips"><button id="playSelected" disabled>▶ 選択した振動を再生</button><button id="stopPlay">■ 停止</button><button id="flag">☆ 違和感あり</button><button id="markSelected">選んだ動作名を付ける</button><button id="unmark">目印を外す</button></div><input id="memo" placeholder="例：小さい弾きなのに衝撃が長い"><button id="saveMemo">メモを保存</button><p id="message"></p>
<p>波形は再生開始時に選ばれた素材です。別の振動が優先されると途中で止まる場合があります。ゲーム標準の通常振動はこの一覧に含まれません。</p></section></div>
<script>
let auditionToken=null;let data=[],selected=null,follow=true,cut='',lastWave=null;const notes=new Map(),waveCache=new Map(),journal=new Map();const $=id=>document.getElementById(id);const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function visible(){const q=$('search').value.toLowerCase();return data.filter(e=>(!cut||e.time>cut)&&(!$('hideFeet').checked||e.category!=='footsteps')&&(!$('onlyFlag').checked||notes.has(e.key))&&(`${e.label} ${e.id} ${notes.get(e.key)?.action||''}`).toLowerCase().includes(q));}
function render(){const rows=visible().slice().reverse();$('count').textContent=`${rows.length} 件`;$('empty').hidden=rows.length>0;$('rows').innerHTML=rows.map(e=>{const n=notes.get(e.key)||{};return `<tr data-key="${esc(e.key)}" class="${selected?.key===e.key?'selected':''}"><td class="mono">${esc(e.time.slice(11))}</td><td>${esc(e.label)}<br><small>${esc(e.id)}</small></td><td><span class="star">${n.flag?'★ ':''}</span>${esc(n.action||'')}</td></tr>`}).join('');document.querySelectorAll('tr[data-key]').forEach(r=>r.onclick=()=>{follow=false;$('follow').textContent='最新を追う：OFF';$('follow').classList.remove('on');choose(data.find(e=>e.key===r.dataset.key));});}
async function choose(e){if(!e)return;selected=e;$('playSelected').disabled=!e.sample;render();$('title').textContent=e.label;$('origin').textContent=e.origin;$('ident').textContent=`${e.time} · フレーム ${e.frame} · 通知 ${e.sequence}\n${e.id}${e.sample?' → '+e.sample:''}`;$('memo').value=notes.get(e.key)?.memo||'';$('flag').textContent=notes.get(e.key)?.flag?'★ 違和感あり':'☆ 違和感あり';$('message').textContent=notes.get(e.key)?.action?'動作の目印：'+notes.get(e.key).action:'';lastWave=null;
if(!e.sample){$('duration').textContent=$('peak').textContent=$('rms').textContent='—';$('material').textContent=e.kind==='trigger'?'トリガー設定の切り替えです。':'旧ログのため使用素材の番号は未記録です。更新後の操作を確認してください。';$('bank').textContent='';draw(null);return;}
try{let w=waveCache.get(e.sample);if(!w){const response=await fetch('wave?sample='+encodeURIComponent(e.sample));if(!response.ok)throw Error('波形なし');w=await response.json();waveCache.set(e.sample,w);}if(selected?.key!==e.key)return;lastWave=w;$('duration').textContent=Math.round(w.duration_ms)+' ms';$('peak').textContent=Math.round(w.peak*100)+'%';$('rms').textContent=Math.round(w.rms*100)+'%';$('material').textContent=w.material;$('bank').textContent=w.bank;draw(w);}catch{if(selected?.key===e.key){$('material').textContent='この素材の波形を読み込めませんでした。';draw(null);}}}
function draw(w){const c=$('wave'),x=c.getContext('2d');x.clearRect(0,0,c.width,c.height);x.font='15px sans-serif';for(let side=0;side<2;side++){const y=side?162:60;x.fillStyle='#9caec0';x.fillText(side?'右':'左',12,y+5);x.strokeStyle='#334255';x.beginPath();x.moveTo(42,y);x.lineTo(880,y);x.stroke();if(!w)continue;const a=side?w.right:w.left;x.strokeStyle=side?'#86aaff':'#70d5b5';x.lineWidth=3;for(let i=0;i<a.length;i++){const px=44+i*830/a.length;x.beginPath();x.moveTo(px,y-a[i][0]*48);x.lineTo(px,y-a[i][1]*48);x.stroke();}}}
function note(update){if(!selected)return;notes.set(selected.key,{...(notes.get(selected.key)||{}),...update});render();$('flag').textContent=notes.get(selected.key).flag?'★ 違和感あり':'☆ 違和感あり';$('message').textContent='記録に追加しました。「記録を書き出す」で保存できます。';}
async function audition(stop){if(!stop&&!selected?.sample)return;follow=false;$('follow').textContent='最新を追う：OFF';$('follow').classList.remove('on');try{const r=await fetch(stop?'stop':'play?sample='+encodeURIComponent(selected.sample)+'&audio='+($('withSound').checked?'1':'0')+'&volume='+(Number($('soundVolume').value)/100),{method:'POST',headers:{'X-Inspector-Request':'1'}});if(!r.ok)throw Error();auditionToken=(await r.json()).token;$('message').textContent=stop?'停止を要求しました。':'DualSenseへ再生を要求しました。';}catch{$('message').textContent='再生要求を送れませんでした。接続を確認してください。'}}
$('playSelected').onclick=()=>audition(false);$('stopPlay').onclick=()=>audition(true);
$('flag').onclick=()=>note({flag:!notes.get(selected?.key)?.flag});$('saveMemo').onclick=()=>note({memo:$('memo').value});$('markSelected').onclick=()=>note({action:$('action').value});$('unmark').onclick=()=>{if(selected){notes.delete(selected.key);$('memo').value='';$('flag').textContent='☆ 違和感あり';render();}};
$('markNow').onclick=()=>{const latest=data.filter(e=>e.kind!=='trigger').at(-1);if(!latest)return;const end=Date.parse(latest.time.replace(' ','T'));let count=0;for(const e of data){if(e.kind!=='trigger'&&Date.parse(e.time.replace(' ','T'))>=end-5000){notes.set(e.key,{...(notes.get(e.key)||{}),action:$('action').value});count++;}}render();$('message').textContent=`最後の出力を基準に ${count} 件へ「${$('action').value}」を付けました。`;};
$('follow').onclick=()=>{follow=!follow;$('follow').classList.toggle('on',follow);$('follow').textContent='最新を追う：'+(follow?'ON':'OFF');if(follow)choose(visible().at(-1));};$('clear').onclick=()=>{cut=data.at(-1)?.time||'';render();};for(const id of ['search','hideFeet','onlyFlag'])$(id).oninput=render;
$('export').onclick=()=>{const records=data.filter(e=>!cut||e.time>cut).map(e=>({...e,note:notes.get(e.key)||null,waveform:waveCache.get(e.sample)||null}));const blob=new Blob([JSON.stringify({format:1,created:new Date().toISOString(),scope:'MOD playback starts; action labels are user annotations',records},null,2)],{type:'application/json'});const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download='onimusha-haptics-'+new Date().toISOString().replace(/[:.]/g,'-')+'.json';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);$('message').textContent=`${records.length} 件を書き出しました。`;};
async function poll(){try{const response=await fetch('api');if(!response.ok)throw Error();const j=await response.json();if(auditionToken&&j.audition?.token===auditionToken){const labels={waiting:'再生準備中…',playing:'DualSenseで再生中',finished:'再生が完了しました。',stopped:'停止しました。',game_focused:'ゲームへ戻ったため試聴を停止しました。',timeout:'再生を開始できませんでした。ゲーム／接続を確認してください。',failed:'再生できませんでした。'};$('message').textContent=(labels[j.audition.state]||j.audition.state)+(j.audition.message?' '+j.audition.message:'')+(j.audition.state==='playing'?(j.audition.audio?'（効果音あり）':'（振動のみ）'):'');}for(const e of j.entries||[])journal.set(e.key,e);if(journal.size>5000){for(const k of journal.keys()){if(journal.size<=5000)break;if(!notes.has(k))journal.delete(k);}}data=[...journal.values()];$('connection').textContent='接続中';$('connection').className='badge ok';$('activity').textContent=j.status?.running?(j.status.active?'ゲーム操作中':'待機中 · ゲームへ戻ると再開'):'MODは停止中';$('gain').textContent=`強さ ${j.gain}`;render();if(follow){const newest=visible().at(-1);if(newest&&newest.key!==selected?.key)choose(newest);}}catch{$('connection').textContent='接続が切れました · ツールを起動し直してください';$('connection').className='badge warn';}setTimeout(poll,400);}draw(null);poll();
</script></html>
""";
}
