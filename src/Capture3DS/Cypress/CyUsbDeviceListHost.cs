using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CyUSB;

namespace Capture3DS.Cypress
{
    // CyUSBが内部生成するFormは、作成したスレッドでメッセージを処理する必要がある。
    // USB転送は呼び出し側に残し、このスレッドは通知と一覧の生成・破棄を担当する。
    internal sealed class CyUsbDeviceListHost : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<USBDeviceList> _ready = new TaskCompletionSource<USBDeviceList>();
        private Control _dispatcher;
        private ApplicationContext _context;
        private bool _disposed;

        internal USBDeviceList Devices { get; }

        internal CyUsbDeviceListHost()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "LlSpa3CyUsbNotifications"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            try
            {
                Devices = _ready.Task.GetAwaiter().GetResult();
            }
            catch
            {
                _thread.Join();
                throw;
            }
        }

        private void Run()
        {
            USBDeviceList devices = null;
            try
            {
                using (var context = new ApplicationContext())
                using (var dispatcher = new Control())
                {
                    _context = context;
                    _dispatcher = dispatcher;
                    IntPtr unused = dispatcher.Handle;

                    // 通知スレッドから転送中のデバイスを自動破棄させない。
                    // 一覧を固定し、取得側の既存の終了・解放順序を維持する。
                    devices = new USBDeviceList(CyConst.DEVICES_CYUSB, IgnoreDeviceListChanges);
                    USBDeviceList readyDevices = devices;
                    dispatcher.BeginInvoke(new Action(() => _ready.TrySetResult(readyDevices)));
                    Application.Run(context);
                }
            }
            catch (Exception error)
            {
                _ready.TrySetException(error);
            }
            finally
            {
                // 起動に失敗した場合の後始末。通常終了時はDisposeで既に解放済み。
                if (!_ready.Task.IsCompleted)
                    _ready.TrySetException(new InvalidOperationException("CyUSB notification loop stopped before startup completed."));
                if (_ready.Task.Status != TaskStatus.RanToCompletion && devices != null)
                {
                    try { devices.Dispose(); }
                    catch { /* 起動時の元の例外を保持する。 */ }
                }
            }
        }

        private static void IgnoreDeviceListChanges(IntPtr notification, IntPtr removedDevice)
        {
            // SDKのPnPによる一覧更新・自動破棄だけを抑止する。
            // WinFormsの設定変更通知はメッセージループで処理する。
        }

        public void Dispose()
        {
            if (_disposed) return;

            // 呼び出し側の接続・取得・破棄は従来どおり直列に行う。
            _dispatcher.Invoke(new Action(() =>
            {
                Devices.Dispose();
                _disposed = true;
                _context.ExitThread();
            }));
            _thread.Join();
        }
    }
}
