using System.Runtime.InteropServices;

namespace RouteDeck.Core.Native;

/// <summary>
/// Registers for route, interface and address notifications and raises one coalesced event
/// per burst. Windows can fire dozens of callbacks for a single adapter change.
/// </summary>
internal sealed unsafe class ChangeNotifier : IDisposable
{
    private readonly List<IntPtr> _handles = [];
    private readonly Timer _timer;
    private readonly TimeSpan _delay;
    private GCHandle _self;

    public ChangeNotifier(Action onChange, TimeSpan delay)
    {
        _delay = delay;
        _timer = new Timer(_ => onChange(), null, Timeout.Infinite, Timeout.Infinite);
        _self = GCHandle.Alloc(this);
        IntPtr context = GCHandle.ToIntPtr(_self);

        Register(IpHelper.NotifyRouteChange2(IpHelper.AF_UNSPEC, &OnNativeChange, context, false, out IntPtr route), route, "路由");
        Register(IpHelper.NotifyIpInterfaceChange(IpHelper.AF_UNSPEC, &OnNativeChange, context, false, out IntPtr iface), iface, "网卡");
        Register(IpHelper.NotifyUnicastIpAddressChange(IpHelper.AF_UNSPEC, &OnNativeChange, context, false, out IntPtr addr), addr, "地址");
    }

    public void Signal() => _timer.Change(_delay, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        foreach (IntPtr handle in _handles) IpHelper.CancelMibChangeNotify2(handle);
        _handles.Clear();
        _timer.Dispose();
        if (_self.IsAllocated) _self.Free();
    }

    private void Register(int result, IntPtr handle, string what)
    {
        if (result != IpHelper.ERROR_SUCCESS)
        {
            Dispose();
            throw Win32ErrorMessages.ToException(result, $"订阅{what}变化");
        }
        _handles.Add(handle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnNativeChange(IntPtr context, IntPtr row, int notificationType)
    {
        if (GCHandle.FromIntPtr(context).Target is ChangeNotifier notifier) notifier.Signal();
    }
}
