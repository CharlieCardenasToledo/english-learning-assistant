using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsLiveCaptionsReader.Services
{
    public class HardwareSpecs
    {
        public int CpuCores { get; set; }
        public string CpuName { get; set; } = "Unknown CPU";
        public double TotalRamGb { get; set; }
        public string GpuName { get; set; } = "Unknown GPU";
        public double FreeDiskGb { get; set; }
        public double TotalDiskGb { get; set; }
    }

    public static class HardwareDetector
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public static HardwareSpecs DetectHardware()
        {
            var specs = new HardwareSpecs();

            // 1. CPU Cores & Name
            try
            {
                specs.CpuCores = Environment.ProcessorCount;
                specs.CpuName = Registry.GetValue(@"HKEY_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString()?.Trim() ?? "Unknown CPU";
                if (specs.CpuName == "Unknown CPU")
                {
                    specs.CpuName = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString()?.Trim() ?? "Unknown CPU";
                }
            }
            catch
            {
                specs.CpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Intel/AMD Processor";
            }

            // 2. RAM Size
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    specs.TotalRamGb = Math.Round((double)memStatus.ullTotalPhys / (1024 * 1024 * 1024), 1);
                }
            }
            catch
            {
                specs.TotalRamGb = 8.0; // Fallback razonable
            }

            // 3. GPU Name
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    var val = Registry.GetValue(
                        $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\000{i}",
                        "DriverDesc", null);
                    if (val != null)
                    {
                        specs.GpuName = val.ToString()!;
                        break;
                    }
                }
            }
            catch
            {
                specs.GpuName = "Generic Display Adapter";
            }

            // 4. Disk Space (Drive C)
            try
            {
                var drive = new System.IO.DriveInfo("C");
                specs.FreeDiskGb = Math.Round((double)drive.AvailableFreeSpace / (1024 * 1024 * 1024), 1);
                specs.TotalDiskGb = Math.Round((double)drive.TotalSize / (1024 * 1024 * 1024), 1);
            }
            catch
            {
                // Fallback
            }

            return specs;
        }

        public static string GetModelRecommendation(HardwareSpecs specs)
        {
            bool hasDedicatedGpu = specs.GpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                   specs.GpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                                   specs.GpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                                   specs.GpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase);

            string gpuAdvise = hasDedicatedGpu
                ? $"\n💡 Se detectó GPU dedicada ({specs.GpuName}). Se recomienda habilitar aceleración GPU/CUDA en LM Studio y Whisper."
                : "";

            string diskAdvise = specs.FreeDiskGb < 10.0
                ? "\n⚠️ Espacio en disco bajo (menos de 10 GB libres). Podrías tener problemas para descargar o cargar nuevos modelos."
                : "";

            if (specs.TotalRamGb < 8.0)
            {
                return "⚙️ Recomendación: Dispositivo de gama baja. Usa modelos muy compactos como Llama-3.2-1B, Qwen-1.5B/3B, y Whisper modelo 'tiny' o 'base' con CPU thread tuning." + gpuAdvise + diskAdvise;
            }
            else if (specs.TotalRamGb < 16.0)
            {
                return "⚙️ Recomendación: Dispositivo de gama media. Se sugiere Llama-3.2-3B, Qwen-2.5-3B/7B, y Whisper modelo 'base' o 'small'." + gpuAdvise + diskAdvise;
            }
            else
            {
                return "⚙️ Recomendación: Dispositivo de gama alta. Capaz de ejecutar Llama-3-8B, Mistral-7B, y Whisper modelo 'small' o 'medium' de forma fluida." + gpuAdvise + diskAdvise;
            }
        }
    }
}

