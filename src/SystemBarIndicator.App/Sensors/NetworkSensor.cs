using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;

namespace SystemBarIndicator.Sensors
{
    /// <summary>
    /// Measures network speed from the delta of each interface's byte counters.
    /// <see cref="IPInterfaceStatistics"/> is used instead of PerformanceCounter: the
    /// instance names of the "Network Interface" performance counters are derived from the
    /// adapter description and do not match <c>NetworkInterface.Name</c>, so matching by
    /// name frequently fails to find the counter.
    /// <para>
    /// The interface list is cached. Enumerating it (<c>GetAllNetworkInterfaces</c>) walks
    /// every adapter's addresses and measured ~14 ms per tick on a machine with a few virtual
    /// adapters, whereas reading one interface's counters is a single cheap call. The list is
    /// rebuilt when Windows reports an address change (an adapter connecting, disconnecting
    /// or appearing), when an interface stops answering, and every 30 s as a safety net.
    /// </para>
    /// </summary>
    public sealed class NetworkSensor : IDisposable
    {
        private static readonly TimeSpan RefreshPeriod = TimeSpan.FromSeconds(30);

        private readonly Dictionary<string, long> _prevReceived = new();
        private readonly Dictionary<string, long> _prevSent = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _lastCheck;
        private TimeSpan _lastRefresh;
        private NetworkInterface[] _interfaces = Array.Empty<NetworkInterface>();
        // Set from a thread-pool thread by NetworkChange, consumed by the polling thread.
        private volatile bool _refreshRequested = true;

        public NetworkSensor()
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

            RefreshInterfaces();
            foreach (var ni in _interfaces)
            {
                try
                {
                    var stats = ni.GetIPv4Statistics();
                    _prevReceived[ni.Id] = stats.BytesReceived;
                    _prevSent[ni.Id] = stats.BytesSent;
                }
                catch
                {
                    // interface with no available IPv4 statistics — skip it
                }
            }
            _lastCheck = _clock.Elapsed;
        }

        public (float downloadKBps, float uploadKBps) GetInfo()
        {
            // A monotonic clock: DateTime.Now jumps with clock corrections and DST, which
            // turned into a spike (or a zero) in the computed rate.
            var now = _clock.Elapsed;
            double elapsed = (now - _lastCheck).TotalSeconds;
            long totalDown = 0, totalUp = 0;

            if (_refreshRequested || now - _lastRefresh >= RefreshPeriod)
                RefreshInterfaces();

            foreach (var ni in _interfaces)
            {
                try
                {
                    var stats = ni.GetIPv4Statistics();
                    string id = ni.Id;

                    if (_prevReceived.TryGetValue(id, out long prevR))
                    {
                        long diff = stats.BytesReceived - prevR;
                        if (diff > 0) totalDown += diff;
                    }
                    if (_prevSent.TryGetValue(id, out long prevS))
                    {
                        long diff = stats.BytesSent - prevS;
                        if (diff > 0) totalUp += diff;
                    }

                    _prevReceived[id] = stats.BytesReceived;
                    _prevSent[id] = stats.BytesSent;
                }
                catch
                {
                    // Skipped this tick. Not a reason to rebuild the list: an interface that
                    // really went away raises a NetworkChange event (and the 30 s refresh
                    // catches the rest), while one that merely fails every call would
                    // otherwise force a full enumeration on every tick.
                }
            }

            _lastCheck = now;
            if (elapsed <= 0) return (0, 0);

            return ((float)(totalDown / 1024.0 / elapsed), (float)(totalUp / 1024.0 / elapsed));
        }

        private void RefreshInterfaces()
        {
            // Cleared before enumerating, so a change that lands mid-enumeration still
            // triggers another refresh.
            _refreshRequested = false;
            _lastRefresh = _clock.Elapsed;
            _interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToArray();

            // Forget the counters of interfaces that are gone. One that comes back later
            // starts from a fresh baseline instead of reporting everything it moved while
            // away as a single tick's worth of traffic (a spike that also inflates the
            // auto-scale peak for minutes).
            var current = new HashSet<string>(_interfaces.Select(ni => ni.Id));
            foreach (var id in _prevReceived.Keys.Where(id => !current.Contains(id)).ToList())
                _prevReceived.Remove(id);
            foreach (var id in _prevSent.Keys.Where(id => !current.Contains(id)).ToList())
                _prevSent.Remove(id);
        }

        private void OnNetworkChanged(object? sender, EventArgs e) => _refreshRequested = true;

        public void Dispose()
        {
            // Static events: without unsubscribing they would keep this object alive.
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        }
    }
}
