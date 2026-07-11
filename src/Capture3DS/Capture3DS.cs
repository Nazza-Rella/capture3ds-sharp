using System;
using System.Collections.Generic;
using System.IO;
using Capture3DS.Cypress;
using Capture3DS.Ftd2;
using Capture3DS.Ftd3;

namespace Capture3DS
{
    /// <summary>
    /// 3DS キャプチャボード読み取りの公開窓口。
    /// N3DSXL(FTD3)、DS(FTD2)、LL-SPA3(CyUSB) に対応。
    /// </summary>
    public static class Capture3DSApi
    {
        /// <summary>接続中の全対応デバイスを列挙する。</summary>
        public static IReadOnlyList<Capture3DSDeviceInfo> ListDevices()
        {
            var list = new List<Capture3DSDeviceInfo>();
            // 各バックエンドのネイティブ DLL が無くても他バックエンドの列挙を止めない。
            list.AddRange(SafeList(Ftd3N3dsxlDevice.ListDevices));
            list.AddRange(SafeList(DsFtd2Device.ListDevices));
            list.AddRange(SafeList(LlSpa3Device.ListDevices));
            return list;
        }

        /// <summary>指定デバイスを開く(Connect は呼び出し側で行う)。</summary>
        public static ICapture3DSDevice Open(Capture3DSDeviceInfo info)
        {
            switch (info.Model)
            {
                case Capture3DSModel.N3dsxl:
                    return Ftd3N3dsxlDevice.Open(info);
                case Capture3DSModel.DsFtd2:
                    return DsFtd2Device.Open(info);
                case Capture3DSModel.LlSpa3:
                    return LlSpa3Device.Open(info);
                default:
                    throw new Capture3DSException($"model not implemented yet: {info.Model}");
            }
        }

        /// <summary>
        /// LL-SPA3のプロダクトキーを取得する。Capture3DS.json → n3DS_viewのEEPROM
        /// キャッシュの順で探し、キャッシュから拾えた場合はJSONへ保存する。無ければnull。
        /// </summary>
        public static string GetLlSpa3ProductKey()
        {
            return LlSpa3OptimizeProtocol.TryLoadProductKey(out var key, out _) ? key : null;
        }

        /// <summary>LL-SPA3のプロダクトキーをCapture3DS.jsonへ保存する。書式不正・保存失敗はfalse。</summary>
        public static bool SetLlSpa3ProductKey(string key)
        {
            return LlSpa3OptimizeProtocol.TrySaveProductKeyToJson(key);
        }

        private static IReadOnlyList<Capture3DSDeviceInfo> SafeList(Func<IReadOnlyList<Capture3DSDeviceInfo>> lister)
        {
            try { return lister(); }
            catch (DllNotFoundException) { return Array.Empty<Capture3DSDeviceInfo>(); }
            catch (BadImageFormatException) { return Array.Empty<Capture3DSDeviceInfo>(); }
            catch (FileNotFoundException) { return Array.Empty<Capture3DSDeviceInfo>(); }
        }
    }
}