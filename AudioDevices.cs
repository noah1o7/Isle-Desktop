using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Isle
{
    /// <summary>
    /// Audio-Ausgabegeräte über die Windows Core-Audio-Schnittstelle:
    /// auflisten, Standardgerät wechseln, Lautstärke und Stummschaltung.
    /// </summary>
    internal static class AudioDevices
    {
        public sealed record Device(string Id, string Name);

        private const int eRender = 0, eConsole = 0, eMultimedia = 1, eCommunications = 2;
        private const int DEVICE_STATE_ACTIVE = 1;
        private const int CLSCTX_ALL = 23;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out Win32.IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float levelDb, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // Nicht offiziell dokumentiert, aber seit Windows 7 der übliche Weg, das Standardgerät zu wechseln
        // (so machen es auch bekannte Tools wie EarTrumpet / SoundSwitch).
        [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        private class PolicyConfigClientCom { }

        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat();
            [PreserveSig] int GetDeviceFormat();
            [PreserveSig] int ResetDeviceFormat();
            [PreserveSig] int SetDeviceFormat();
            [PreserveSig] int GetProcessingPeriod();
            [PreserveSig] int SetProcessingPeriod();
            [PreserveSig] int GetShareMode();
            [PreserveSig] int SetShareMode();
            [PreserveSig] int GetPropertyValue();
            [PreserveSig] int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility();
        }

        private static IMMDeviceEnumerator? _enum;
        private static IMMDeviceEnumerator Enumerator => _enum ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

        private static IAudioEndpointVolume? _volume;
        private static string? _volumeDeviceId;
        private static Guid _ctx = Guid.Empty;

        public static List<Device> Outputs()
        {
            var list = new List<Device>();
            try
            {
                if (Enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var col) != 0) return list;
                col.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    if (col.Item(i, out var dev) != 0) continue;
                    dev.GetId(out string id);
                    list.Add(new Device(id, FriendlyName(dev) ?? "Lautsprecher"));
                    Marshal.ReleaseComObject(dev);
                }
                Marshal.ReleaseComObject(col);
            }
            catch { }
            return list;
        }

        public static string? DefaultId()
        {
            try
            {
                if (Enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var dev) != 0) return null;
                dev.GetId(out string id);
                Marshal.ReleaseComObject(dev);
                return id;
            }
            catch { return null; }
        }

        public static void SetDefault(string id)
        {
            try
            {
                var pc = (IPolicyConfig)new PolicyConfigClientCom();
                pc.SetDefaultEndpoint(id, eConsole);
                pc.SetDefaultEndpoint(id, eMultimedia);
                pc.SetDefaultEndpoint(id, eCommunications);
                Marshal.ReleaseComObject(pc);
            }
            catch { }
        }

        private static string? FriendlyName(IMMDevice dev)
        {
            try
            {
                if (dev.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;
                var key = new Win32.PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
                string? name = null;
                if (store.GetValue(ref key, out var pv) == 0)
                {
                    if (pv.vt == 31) name = Marshal.PtrToStringUni(pv.pointerValue);
                    Win32.PropVariantClear(ref pv);
                }
                Marshal.ReleaseComObject(store);
                return name;
            }
            catch { return null; }
        }

        /// <summary>Lautstärke-Steuerung des aktuellen Standardgeräts (wird bei Gerätewechsel neu geholt).</summary>
        private static IAudioEndpointVolume? Volume()
        {
            string? id = DefaultId();
            if (id == null) return null;
            if (_volume != null && id == _volumeDeviceId) return _volume;
            try
            {
                if (_volume != null) Marshal.ReleaseComObject(_volume);
                _volume = null;
                if (Enumerator.GetDevice(id, out var dev) != 0) return null;
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o) == 0)
                {
                    _volume = (IAudioEndpointVolume)o;
                    _volumeDeviceId = id;
                }
                Marshal.ReleaseComObject(dev);
            }
            catch { _volume = null; }
            return _volume;
        }

        /// <summary>0..1, oder null wenn kein Gerät.</summary>
        public static float? GetVolume()
        {
            try { return Volume() is { } v && v.GetMasterVolumeLevelScalar(out float l) == 0 ? l : null; }
            catch { return null; }
        }

        public static void SetVolume(float level)
        {
            try { Volume()?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _ctx); } catch { }
        }

        public static bool GetMute()
        {
            try { return Volume() is { } v && v.GetMute(out bool m) == 0 && m; }
            catch { return false; }
        }

        public static void SetMute(bool mute)
        {
            try { Volume()?.SetMute(mute, ref _ctx); } catch { }
        }
    }
}
