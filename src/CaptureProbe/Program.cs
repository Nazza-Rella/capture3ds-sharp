using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Capture3DS;

namespace CaptureProbe
{
    /// <summary>
    /// 動作確認用コンソール。接続中の 3DS キャプボを列挙し、最初の N3DSXL から
    /// 1 フレーム取り込んで PNG(上画面/下画面/1280x720レターボックス)を保存する。
    /// 使い方: CaptureProbe.exe [出力フォルダ] [フレーム数]
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
// 診断モード: FTD3(N3DSXL)列挙をフィルタせず生でダンプ。
            // numDevs==0 ならドライバ/排他オープン問題、numDevs>0 かつ filterMatch=False なら
            // Description フィルタ不一致(列挙コードの修正で対応可能)。
            if (args.Length > 0 && args[0] == "--ftd3raw")
            {
                Console.WriteLine("== FTD3 raw 列挙 ==");
                foreach (var line in Capture3DS.Ftd3.Ftd3N3dsxlDevice.DescribeRawDevices())
                    Console.WriteLine(line);
                return 0;
            }

            // 診断モード: 縦ずれ切り分け。接続後、デコードせず連続読みして各転送長を出力する。
            // 正常フレームは ≒553472 で短パケット終端され、毎回 555008 なら整列崩れ(連結読み)。
            if (args.Length > 0 && args[0] == "--ftd3sizes")
            {
                int count = args.Length > 1 && int.TryParse(args[1], out int c) ? c : 120;
                var (videoSize, captureSize, maxNonError) = Capture3DS.Ftd3.Ftd3N3dsxlDevice.DiagnosticSizes();
                Console.WriteLine("== FTD3 転送長 診断 ==");
                Console.WriteLine($"参照: videoSize={videoSize} captureSize={captureSize} maxNonError={maxNonError}");
                var ds = Capture3DSApi.ListDevices();
                if (ds.Count == 0) { Console.WriteLine("デバイスが見つかりません。"); return 1; }
                Console.WriteLine($"接続: {ds[0]}");
                using (var dev = Capture3DS.Ftd3.Ftd3N3dsxlDevice.Open(ds[0]))
                {
                    dev.Connect();
                    int nOver = 0, nIn = 0, nShort = 0, nZero = 0;
                    for (int n = 0; n < count; n++)
                    {
                        uint t = dev.ReadRawTransferSize();
                        if (t == 0) nZero++;
                        else if (t > maxNonError) nOver++;
                        else if (t >= videoSize) nIn++;
                        else nShort++;
                        if (n < 20) Console.WriteLine($"  [{n}] transferred={t}");
                    }
                    Console.WriteLine($"集計({count}回): 正常域={nIn} 過大(>maxNonError)={nOver} 過少(<videoSize)={nShort} 一過性0={nZero}");
                }
                return 0;
            }

            // 診断モード: NX と同じ高速連続ループを再現。各フレームの transferred を記録し、
            // 「最頻値から外れた=ミスアラインの疑い」フレームの PNG を保存して目視確認する。
            // 使い方: --ftd3stream <出力dir> <フレーム数>
            if (args.Length > 0 && args[0] == "--ftd3stream")
            {
                string sdir = args.Length > 1 ? args[1] : "cap_stream";
                int scount = args.Length > 2 && int.TryParse(args[2], out int sc) ? sc : 300;
                Directory.CreateDirectory(sdir);
                var (videoSize, captureSize, maxNonError) = Capture3DS.Ftd3.Ftd3N3dsxlDevice.DiagnosticSizes();
                Console.WriteLine("== FTD3 連続ストリーム診断 ==");
                Console.WriteLine($"参照: videoSize={videoSize} captureSize={captureSize} maxNonError={maxNonError}");
                var sds = Capture3DSApi.ListDevices();
                if (sds.Count == 0) { Console.WriteLine("デバイスが見つかりません。"); return 1; }
                using (var dev = Capture3DS.Ftd3.Ftd3N3dsxlDevice.Open(sds[0]))
                {
                    dev.Connect();
                    var sizes = new System.Collections.Generic.List<uint>(scount);
                    int saved = 0, nNull = 0;
                    uint modal = 520588;
                    for (int n = 0; n < scount; n++)
                    {
                        var fr = dev.ReadFrameDiagnostic(out uint t);
                        sizes.Add(t);
                        if (fr == null) { nNull++; continue; }
                        // 最頻値から外れた疑わしいフレームを最大20枚保存(乱れの実物)
                        if (t != modal && saved < 20)
                        {
                            byte[] canvas = fr.ToLetterbox720();
                            SaveRgb(Path.Combine(sdir, $"anom_{n:D4}_t{t}.png"), canvas, 1280, 720);
                            saved++;
                        }
                    }
                    // 転送長のヒストグラム
                    var hist = new System.Collections.Generic.Dictionary<uint, int>();
                    foreach (var s in sizes) hist[s] = hist.TryGetValue(s, out int v) ? v + 1 : 1;
                    Console.WriteLine($"フレーム数={scount} null(一過性/過少)={nNull} 異常PNG保存={saved}");
                    Console.WriteLine("transferred ヒストグラム(多い順):");
                    foreach (var kv in new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<uint, int>>(hist))
                        { }
                    var sorted = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<uint, int>>(hist);
                    sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                    for (int i = 0; i < sorted.Count && i < 15; i++)
                        Console.WriteLine($"  {sorted[i].Key} : {sorted[i].Value}回");
                }
                Console.WriteLine($"完了 -> {Path.GetFullPath(sdir)}");
                return 0;
            }

            // 検証モード: 実運用と同じ ReadFrame(再同期付き)を NX 相当の密ループで回し、
            // 1280x720 へ焼く処理込みで負荷をかける。整列フレームしか返らないはずなので、
            // 一定間隔で保存した PNG が縦ずれしていないことを目視確認する。
            // 使い方: --ftd3verify <出力dir> <フレーム数>
            if (args.Length > 0 && args[0] == "--ftd3verify")
            {
                string vdir = args.Length > 1 ? args[1] : "cap_verify";
                int vcount = args.Length > 2 && int.TryParse(args[2], out int vc) ? vc : 300;
                Directory.CreateDirectory(vdir);
                Console.WriteLine("== FTD3 ReadFrame 検証(再同期付き) ==");
                var vds = Capture3DSApi.ListDevices();
                if (vds.Count == 0) { Console.WriteLine("デバイスが見つかりません。"); return 1; }
                using (var dev = Capture3DSApi.Open(vds[0]))
                {
                    dev.Connect();
                    int got = 0, err = 0, saved = 0;
                    for (int n = 0; n < vcount; n++)
                    {
                        try
                        {
                            Capture3DSFrame fr = dev.ReadFrame();
                            got++;
                            byte[] canvas = fr.ToLetterbox720(); // NX と同じ焼き込み負荷
                            if (n % 30 == 0 && saved < 20)
                            {
                                SaveRgb(Path.Combine(vdir, $"verify_{n:D4}.png"), canvas, 1280, 720);
                                saved++;
                            }
                        }
                        catch { err++; }
                    }
                    Console.WriteLine($"フレーム取得={got} 例外={err} 保存={saved}（全{vcount}回）");
                }
                Console.WriteLine($"完了 -> {Path.GetFullPath(vdir)}");
                return 0;
            }

            // 診断モード(B): LL-SPA3 ウォームアップ計測。NX と同じ Open→ReadFrame を回し、
            // 各フレームの「前フレームとの差分(ノイズ閾値超えバイト数)」を出力する。
            // 先頭の静止フレーム(差分≒0)が何枚続き、何枚目から動き出すかを実測する。
            // 使い方: --llspa3warmup <フレーム数>
            if (args.Length > 0 && args[0] == "--llspa3warmup")
            {
                int wcount = args.Length > 1 && int.TryParse(args[1], out int wc) ? wc : 60;
                const int noiseThreshold = 8; // センサーノイズを無視する 1ch あたりの差分閾値
                Console.WriteLine("== LL-SPA3 ウォームアップ計測(連続フレーム差分) ==");
                var wds = Capture3DSApi.ListDevices();
                if (wds.Count == 0) { Console.WriteLine("デバイスが見つかりません。"); return 1; }
                Console.WriteLine($"接続: {wds[0]}");
                using (var dev = Capture3DSApi.Open(wds[0]))
                {
                    dev.Connect();
                    byte[] prev = null;
                    int firstMoving = -1;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int n = 0; n < wcount; n++)
                    {
                        long t0 = sw.ElapsedMilliseconds;
                        Capture3DSFrame fr = dev.ReadFrame();
                        long dt = sw.ElapsedMilliseconds - t0;
                        byte[] cur = fr.Top;
                        if (prev != null && prev.Length == cur.Length)
                        {
                            int changed = 0;
                            for (int i = 0; i < cur.Length; i++)
                            {
                                int d = cur[i] - prev[i];
                                if (d < 0) d = -d;
                                if (d > noiseThreshold) changed++;
                            }
                            double pct = 100.0 * changed / cur.Length;
                            bool moving = pct > 1.0; // 1% 超の画素が変化したら「動いている」とみなす
                            if (moving && firstMoving < 0) firstMoving = n;
                            Console.WriteLine($"  [{n,3}] 差分={changed,8} ({pct,5:F2}%) {(moving ? "動" : "静止")}  読込={dt}ms");
                        }
                        else
                        {
                            Console.WriteLine($"  [{n,3}] (初回フレーム)  読込={dt}ms");
                        }
                        prev = cur;
                    }
                    Console.WriteLine(firstMoving < 0
                        ? "結論: 計測範囲内で動き出しを検出できませんでした(静止のまま/フレーム数を増やしてください)。"
                        : $"結論: フレーム {firstMoving} 番から動き出しました。先頭 {firstMoving} 枚が静止(=破棄候補)。");
                }
                return 0;
            }

            // 診断モード: 同一プロセス内で接続→切断→再接続を繰り返す(NX の挙動を再現)。
            // 別プロセス起動だと OS がハンドルを解放するため再接続が通るが、NX は同一
            // プロセス内で Dispose→再 Open するので、ここで失敗が再現するか確認する。
            // 使い方: --llspa3reconnect <回数>
            if (args.Length > 0 && args[0] == "--llspa3reconnect")
            {
                int cycles = args.Length > 1 && int.TryParse(args[1], out int rc) ? rc : 3;
                Console.WriteLine("== LL-SPA3 同一プロセス内 再接続テスト ==");
                var rds = Capture3DSApi.ListDevices();
                if (rds.Count == 0) { Console.WriteLine("デバイスが見つかりません。"); return 1; }
                var rinfo = rds[0];
                for (int c = 0; c < cycles; c++)
                {
                    Console.WriteLine($"--- サイクル {c} ---");
                    try
                    {
                        using (var dev = Capture3DSApi.Open(rinfo))
                        {
                            dev.Connect();
                            Console.WriteLine($"  Connect OK");
                            for (int n = 0; n < 3; n++)
                            {
                                var fr = dev.ReadFrame();
                                Console.WriteLine($"  ReadFrame[{n}] OK (top={(fr != null ? fr.Top.Length : 0)})");
                            }
                        }
                        Console.WriteLine($"  Dispose OK");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  失敗: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                return 0;
            }

            // 診断モード: 再接続フラッシュ(前セッションの映像が一瞬出る)の出所を切り分ける。
            // 切断中は FPGA のフレームバッファが凍結する一方、実機の映像は進む。よって
            // 「切断遅延を十分とって再接続し、セッション2の先頭フレームがセッション1末尾と
            // 一致するか」で、デバイスが古い画を再出力しているか判定できる。
            //   一致(差分が小さい) → デバイスが前セッションの latched フレームを再出力
            //                          (出荷中のウォームアップ破棄では足りていない)
            //   不一致(差分が大きい)→ デバイスは即ライブを出している。点滅は NX 表示側の要因
            // 使い方: --llspa3reconnectflash [遅延ms=2000] [セッション2読込数=12]
            if (args.Length > 0 && args[0] == "--llspa3reconnectflash")
            {
                int delayMs = args.Length > 1 && int.TryParse(args[1], out int dm) ? dm : 2000;
                int s2count = args.Length > 2 && int.TryParse(args[2], out int s2) ? s2 : 12;
                const int noiseThreshold = 8;
                const double staleDiffPct = 1.0;   // これ未満は「前フレームと同一(=凍結/再出力)」
                Console.WriteLine("== LL-SPA3 再接続フラッシュ切り分け ==");
                Console.WriteLine($"切断遅延={delayMs}ms / セッション2読込数={s2count}");
                Console.WriteLine("※実機側で動きのある映像(ゲーム等)を表示しておいてください。");
                var fds = Capture3DSApi.ListDevices();
                if (fds.Count == 0) { Console.WriteLine("デバイスが見つかりません。"); return 1; }
                var finfo = fds[0];

                byte[] baseline; // セッション1の最後に取り込んだ Top
                Console.WriteLine("--- セッション1 ---");
                using (var dev = Capture3DSApi.Open(finfo))
                {
                    dev.Connect();
                    baseline = null;
                    for (int n = 0; n < 30; n++)
                    {
                        var fr = dev.ReadFrame();
                        baseline = fr.Top;
                    }
                    Console.WriteLine($"  基準フレーム確定 (top={baseline.Length} bytes)");
                }
                Console.WriteLine($"  切断。{delayMs}ms 待機(この間に実機映像が進む)...");
                System.Threading.Thread.Sleep(delayMs);

                Console.WriteLine("--- セッション2(再接続) ---");
                int firstLive = -1;
                using (var dev = Capture3DSApi.Open(finfo))
                {
                    dev.Connect();
                    Console.WriteLine("  Connect OK");
                    for (int n = 0; n < s2count; n++)
                    {
                        var fr = dev.ReadFrame();
                        byte[] cur = fr.Top;
                        double pct = -1;
                        if (baseline != null && baseline.Length == cur.Length)
                        {
                            int changed = 0;
                            for (int i = 0; i < cur.Length; i++)
                            {
                                int d = cur[i] - baseline[i];
                                if (d < 0) d = -d;
                                if (d > noiseThreshold) changed++;
                            }
                            pct = 100.0 * changed / cur.Length;
                        }
                        bool stale = pct >= 0 && pct < staleDiffPct;
                        if (!stale && firstLive < 0) firstLive = n;
                        Console.WriteLine($"  [s2:{n,2}] 基準との差分={pct,6:F2}%  {(stale ? "★前セッションと同一(凍結)" : "ライブ")}");
                    }
                }
                Console.WriteLine();
                if (firstLive == 0)
                    Console.WriteLine("結論: セッション2の先頭から実機ライブ映像。デバイスは古い画を再出力していません → 点滅は NX 表示側が原因。");
                else if (firstLive > 0)
                    Console.WriteLine($"結論: 先頭 {firstLive} 枚が前セッションと同一(デバイスが latched フレームを再出力)。ウォームアップ破棄を {firstLive} 枚以上にする必要があります。");
                else
                    Console.WriteLine("結論: セッション2が全て前セッションと同一。実機映像が動いていないか、デバイスが古い画を出し続けています(遅延を増やすか動きのある映像で再測定)。");
                return 0;
            }

            // 診断モード: 電源OFF/ケーブル抜け時に ReadFrame がどう振る舞うかを実測する。
            // 「映像が残る」原因が、(A) デバイスが最後の画を latch して読み取りが成功し続ける
            // のか、(B) 例外/タイムアウトになるのか、(C) 黒フレームを返すのか、を切り分ける。
            // 実行中に実機の電源を切る/ケーブルを抜くと、その瞬間の挙動が行ログに出る。
            //   差分→0% が続く  → デバイスが凍結フレームを出し続ける(=タイムアウトでは消えない)
            //   例外            → 読み取り失敗で検知できる
            //   差分が大きいまま → 黒/ノイズに切り替わっている
            // 使い方: --llspa3signal [秒数=30]
            if (args.Length > 0 && args[0] == "--llspa3signal")
            {
                int seconds = args.Length > 1 && int.TryParse(args[1], out int sec) ? sec : 30;
                const int noiseThreshold = 8;
                Console.WriteLine("== LL-SPA3 信号喪失 挙動観測 ==");
                Console.WriteLine($"観測時間={seconds}秒。途中で実機の電源を切る/ケーブルを抜いてください。");
                var gds = Capture3DSApi.ListDevices();
                if (gds.Count == 0) { Console.WriteLine("デバイスが見つかりません。"); return 1; }
                var ginfo = gds[0];
                using (var dev = Capture3DSApi.Open(ginfo))
                {
                    dev.Connect();
                    Console.WriteLine("Connect OK。観測開始。");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    byte[] prev = null;
                    int n = 0;
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        var t0 = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            var fr = dev.ReadFrame();
                            t0.Stop();
                            byte[] cur = fr != null ? fr.Top : null;
                            if (cur == null)
                            {
                                Console.WriteLine($"  [{n,4}] ReadFrame=null  読込={t0.ElapsedMilliseconds}ms");
                            }
                            else
                            {
                                double pct = -1;
                                if (prev != null && prev.Length == cur.Length)
                                {
                                    int changed = 0;
                                    for (int i = 0; i < cur.Length; i++)
                                    {
                                        int d = cur[i] - prev[i];
                                        if (d < 0) d = -d;
                                        if (d > noiseThreshold) changed++;
                                    }
                                    pct = 100.0 * changed / cur.Length;
                                }
                                // 全画素がほぼ黒か(電源OFFで黒画になっていないか)も見る
                                long sum = 0;
                                for (int i = 0; i < cur.Length; i++) sum += cur[i];
                                double avg = (double)sum / cur.Length;
                                Console.WriteLine($"  [{n,4}] OK 前フレ差分={pct,6:F2}% 平均輝度={avg,6:F1} 読込={t0.ElapsedMilliseconds}ms");
                                prev = cur;
                            }
                        }
                        catch (Exception ex)
                        {
                            t0.Stop();
                            Console.WriteLine($"  [{n,4}] 例外: {ex.GetType().Name}: {ex.Message}  経過={t0.ElapsedMilliseconds}ms");
                        }
                        n++;
                    }
                }
                Console.WriteLine("観測終了。");
                return 0;
            }

            // 診断モード: N3DSXL(FTD3)の電源OFF→ON 再接続を、NX の取り込みループと同じ手順で
            // 再現しつつ生の FT ステータスと再列挙結果を時刻付きで記録する。
            // ・ReadFrame が電源OFFで何を投げるか(例外メッセージ=FT ステータス)
            // ・失敗連続→Dispose(FT_Close)後、デバイスが再列挙されるか(numDevs/serial/desc)
            // ・再接続時の FT_Create が何で落ちるか(0x3 か否か)
            // 実行中に本体の電源を切り、数秒後に入れ直すと、その前後の挙動が行ログに出る。
            // ケーブル抜き差し(復帰する)と電源OFF→ON(復帰しない)で再列挙結果を比較するのが狙い。
            // 使い方: --ftd3powercycle [秒数=40]
            if (args.Length > 0 && args[0] == "--ftd3powercycle")
            {
                int seconds = args.Length > 1 && int.TryParse(args[1], out int pcs) ? pcs : 40;
                const int reconnectAfterFailures = 2; // NX の Capture3DSCapture と同値
                Console.WriteLine("== FTD3(N3DSXL) 電源OFF→ON 再接続観測 ==");
                Console.WriteLine($"観測時間={seconds}秒。途中で本体の電源を切り、数秒後に入れ直してください。");

                var ids = Capture3DSApi.ListDevices();
                if (ids.Count == 0) { Console.WriteLine("デバイスが見つかりません。"); return 1; }
                var pinfo = ids[0];
                Console.WriteLine($"対象: {pinfo}");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                Func<string> ts = () => $"[{sw.Elapsed.TotalSeconds,6:F2}s]";

                ICapture3DSDevice device = null;
                try
                {
                    device = Capture3DSApi.Open(pinfo);
                    device.Connect();
                    Console.WriteLine($"{ts()} 初回 Connect OK");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{ts()} 初回 Connect 失敗: {ex.Message}");
                }

                int failures = 0;
                int loggedFrames = 0;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    if (device == null)
                    {
                        // 再接続経路。まず生列挙をダンプ(復帰しない原因の核心)。
                        Console.WriteLine($"{ts()} -- 再接続試行。生列挙:");
                        foreach (var line in Capture3DS.Ftd3.Ftd3N3dsxlDevice.DescribeRawDevices())
                            Console.WriteLine($"{ts()}    {line}");
                        try
                        {
                            var d = Capture3DSApi.Open(pinfo);
                            d.Connect();
                            device = d;
                            failures = 0;
                            loggedFrames = 0;
                            Console.WriteLine($"{ts()} 再接続 Connect OK ★復帰");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{ts()} 再接続 失敗: {ex.Message}");
                            System.Threading.Thread.Sleep(300);
                        }
                        continue;
                    }

                    try
                    {
                        var fr = device.ReadFrame();
                        failures = 0;
                        if (loggedFrames < 3 || loggedFrames % 60 == 0)
                            Console.WriteLine($"{ts()} ReadFrame OK (top={(fr != null ? fr.Top.Length : 0)})");
                        loggedFrames++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{ts()} ReadFrame 例外: {ex.Message} (連続{failures + 1})");
                        if (++failures >= reconnectAfterFailures)
                        {
                            try { device.Dispose(); } catch (Exception dex) { Console.WriteLine($"{ts()} Dispose 例外: {dex.Message}"); }
                            device = null;
                            Console.WriteLine($"{ts()} Dispose 完了 → 再接続へ");
                            failures = 0;
                        }
                        else
                        {
                            System.Threading.Thread.Sleep(5);
                        }
                    }
                }
                if (device != null) { try { device.Dispose(); } catch { } }
                Console.WriteLine("観測終了。");
                return 0;
            }

            // 診断モード: 再接続せず「同じハンドルのまま」読み続け、電源OFF→ON をまたいで
            // フルフレームが復帰するかを実測する。復帰すれば、信号喪失時に Dispose→再接続
            // (=ハングする DrainData を通る)を止め、ストリーム再同期だけで直せることになる。
            // 各 ReadFrameDiagnostic の transferred を時刻付きで記録(変化点のみ)。
            // 使い方: --ftd3holdread [秒数=40]
            if (args.Length > 0 && args[0] == "--ftd3holdread")
            {
                int seconds = args.Length > 1 && int.TryParse(args[1], out int hrs) ? hrs : 40;
                Console.WriteLine("== FTD3(N3DSXL) ハンドル保持・無再接続 観測 ==");
                Console.WriteLine($"観測時間={seconds}秒。途中で本体の電源を切り、数秒後に入れ直してください。");
                var hds = Capture3DSApi.ListDevices();
                if (hds.Count == 0) { Console.WriteLine("デバイスが見つかりません。"); return 1; }
                var hinfo = hds[0];
                Console.WriteLine($"対象: {hinfo}");
                using (var dev = Capture3DS.Ftd3.Ftd3N3dsxlDevice.Open(hinfo))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Func<string> ts = () => $"[{sw.Elapsed.TotalSeconds,6:F2}s]";
                    try { dev.Connect(); Console.WriteLine($"{ts()} Connect OK"); }
                    catch (Exception ex) { Console.WriteLine($"{ts()} Connect 失敗: {ex.Message}"); return 1; }

                    uint lastT = uint.MaxValue;
                    int run = 0, full = 0, tiny = 0, zero = 0, exc = 0;
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        uint t;
                        try
                        {
                            var fr = dev.ReadFrameDiagnostic(out t);
                            if (fr != null) full++;
                            else if (t == 0) zero++;
                            else tiny++;
                        }
                        catch (Exception ex)
                        {
                            t = uint.MaxValue - 1;
                            exc++;
                            Console.WriteLine($"{ts()} 例外: {ex.Message}");
                        }
                        // 転送長の変化点だけ出す(ログ洪水回避)。
                        if (t != lastT)
                        {
                            Console.WriteLine($"{ts()} transferred={t} (直前と変化)");
                            lastT = t;
                        }
                        run++;
                    }
                    Console.WriteLine($"集計: full={full} tiny={tiny} zero={zero} exc={exc} (総{run})");
                }
                Console.WriteLine("観測終了。");
                return 0;
            }

            string outDir = args.Length > 0 ? args[0] : ".";
            int frames = args.Length > 1 && int.TryParse(args[1], out int f) ? f : 1;
            Directory.CreateDirectory(outDir);

            Console.WriteLine("== Capture3DS probe ==");
            var devices = Capture3DSApi.ListDevices();
            if (devices.Count == 0)
            {
                Console.WriteLine("デバイスが見つかりません。FTD3XX.dll の配置とドライバ/接続を確認してください。");
                return 1;
            }
            for (int i = 0; i < devices.Count; i++)
                Console.WriteLine($"[{i}] {devices[i]}");

            var info = devices[0];
            Console.WriteLine($"接続: {info}");
            using (var dev = Capture3DSApi.Open(info))
            {
                dev.Connect();
                Console.WriteLine("Connect 完了。フレーム取り込み中...");

                for (int n = 0; n < frames; n++)
                {
                    Capture3DSFrame frame = dev.ReadFrame();
                    string suffix = frames > 1 ? $"_{n:D3}" : "";
                    SaveRgb(Path.Combine(outDir, $"top{suffix}.png"), frame.Top, frame.TopWidth, frame.TopHeight);
                    SaveRgb(Path.Combine(outDir, $"bottom{suffix}.png"), frame.Bottom, frame.BottomWidth, frame.BottomHeight);
                    byte[] canvas = frame.ToLetterbox720();
                    SaveRgb(Path.Combine(outDir, $"frame720{suffix}.png"), canvas, 1280, 720);
                    Console.WriteLine($"  frame {n}: 保存しました");
                }
            }
            Console.WriteLine($"完了 -> {Path.GetFullPath(outDir)}");
            return 0;
        }

        /// <summary>行優先 RGB8 バイト列を PNG として保存。</summary>
        private static void SaveRgb(string path, byte[] rgb, int width, int height)
        {
            using (var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                var rect = new Rectangle(0, 0, width, height);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int stride = data.Stride;
                    var row = new byte[stride];
                    IntPtr scan = data.Scan0;
                    for (int y = 0; y < height; y++)
                    {
                        int srcOff = y * width * 3;
                        for (int x = 0; x < width; x++)
                        {
                            // RGB(R,G,B) -> GDI 24bpp は BGR 順
                            row[x * 3 + 0] = rgb[srcOff + x * 3 + 2];
                            row[x * 3 + 1] = rgb[srcOff + x * 3 + 1];
                            row[x * 3 + 2] = rgb[srcOff + x * 3 + 0];
                        }
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, scan, stride);
                        scan = IntPtr.Add(scan, stride);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
