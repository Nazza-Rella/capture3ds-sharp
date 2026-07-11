using System;

namespace Capture3DS
{
    /// <summary>対応キャプチャボードの種別。</summary>
    public enum Capture3DSModel
    {
        /// <summary>New 3DS LL 海外版キャプボ (FTDI FT60x / "N3DSXL")。</summary>
        N3dsxl,
        /// <summary>DS キャプボ (FTDI FT232H / FTD2)。</summary>
        DsFtd2,
        /// <summary>LL-SPA3 偽トロ (Cypress FX2 / CyUSB)。</summary>
        LlSpa3,
    }

    /// <summary>列挙で見つかった 1 台のデバイス情報。Open() に渡す。</summary>
    public sealed class Capture3DSDeviceInfo
    {
        public Capture3DSModel Model { get; }
        /// <summary>デバイスのシリアル番号。FTD3 では FT_Create のオープンキーになる。</summary>
        public string Serial { get; }
        /// <summary>デバイス説明文字列。</summary>
        public string Description { get; }
        /// <summary>SuperSpeed(USB3)なら true。</summary>
        public bool SuperSpeed { get; }

        public Capture3DSDeviceInfo(Capture3DSModel model, string serial, string description, bool superSpeed)
        {
            Model = model;
            Serial = serial ?? string.Empty;
            Description = description ?? string.Empty;
            SuperSpeed = superSpeed;
        }

        public override string ToString()
        {
            return $"{Description} (SN:{(string.IsNullOrEmpty(Serial) ? "-" : Serial)}, {(SuperSpeed ? "USB3" : "USB2")})";
        }

        /// <summary>UI 一覧向けの簡潔な表示名(機種名のみ)。</summary>
        public string DisplayName
        {
            get
            {
                switch (Model)
                {
                    case Capture3DSModel.N3dsxl: return "N3DSXL Capture Board";
                    case Capture3DSModel.DsFtd2: return "DS Capture Board";
                    case Capture3DSModel.LlSpa3: return "LL-SPA3";
                    default: return Model.ToString();
                }
            }
        }
    }

    /// <summary>接続済みデバイスの共通インターフェース。</summary>
    public interface ICapture3DSDevice : IDisposable
    {
        Capture3DSDeviceInfo Info { get; }
        /// <summary>キャプチャ開始前のハンドシェイク。失敗時は例外。</summary>
        void Connect();
        /// <summary>2D フレームを 1 枚読み出してデコードする。</summary>
        Capture3DSFrame ReadFrame();
    }

    /// <summary>キャプチャ層で発生したエラー。</summary>
    public sealed class Capture3DSException : Exception
    {
        public Capture3DSException(string message) : base(message) { }
        public Capture3DSException(string message, Exception inner) : base(message, inner) { }
    }
}