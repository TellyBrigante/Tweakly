using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Récupère la VRAM réelle des cartes graphiques via DXGI
    /// (DedicatedVideoMemory, valeur 64-bit exacte — identique au Gestionnaire des tâches).
    /// Contourne la limite UInt32 ~4 Go de WMI Win32_VideoController.AdapterRAM.
    /// </summary>
    internal static class GpuInfo
    {
        public readonly record struct Adapter(string Name, long VramBytes, bool IsSoftware);

        public static List<Adapter> GetAdapters()
        {
            var list = new List<Adapter>();
            IDXGIFactory1? factory = null;
            try
            {
                var iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == null)
                    return list;

                for (uint i = 0; ; i++)
                {
                    IDXGIAdapter1? adapter = null;
                    try
                    {
                        // DXGI_ERROR_NOT_FOUND (0x887A0002) → fin d'énumération
                        if (factory.EnumAdapters1(i, out adapter) != 0 || adapter == null)
                            break;

                        if (adapter.GetDesc1(out var desc) != 0) continue;

                        bool software = (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                        long vram = (long)desc.DedicatedVideoMemory.ToUInt64();

                        list.Add(new Adapter(desc.Description?.Trim() ?? "", vram, software));
                    }
                    finally
                    {
                        if (adapter != null) Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            catch { /* DXGI indisponible → liste vide, l'appelant gère le fallback */ }
            finally
            {
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
            return list;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Interop DXGI
        // ═══════════════════════════════════════════════════════════════════════

        private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint   VendorId;
            public uint   DeviceId;
            public uint   SubSysId;
            public uint   Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public long   AdapterLuid;
            public uint   Flags;
        }

        // IDXGIFactory1 : IDXGIFactory : IDXGIObject : IUnknown
        // Les méthodes des interfaces de base doivent occuper leur slot vtable (stubs).
        [ComImport,
         Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            // --- IDXGIObject ---
            void StubSetPrivateData();
            void StubSetPrivateDataInterface();
            void StubGetPrivateData();
            void StubGetParent();
            // --- IDXGIFactory ---
            void StubEnumAdapters();
            void StubMakeWindowAssociation();
            void StubGetWindowAssociation();
            void StubCreateSwapChain();
            void StubCreateSoftwareAdapter();
            // --- IDXGIFactory1 ---
            [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 ppAdapter);
            [PreserveSig] int IsCurrent();
        }

        // IDXGIAdapter1 : IDXGIAdapter : IDXGIObject : IUnknown
        [ComImport,
         Guid("29038f61-3839-4626-91fd-086879011a05"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            // --- IDXGIObject ---
            void StubSetPrivateData();
            void StubSetPrivateDataInterface();
            void StubGetPrivateData();
            void StubGetParent();
            // --- IDXGIAdapter ---
            void StubEnumOutputs();
            void StubGetDesc();
            void StubCheckInterfaceSupport();
            // --- IDXGIAdapter1 ---
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
        }
    }
}
