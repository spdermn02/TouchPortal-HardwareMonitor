using System.IO.MemoryMappedFiles;
using System.Text;

namespace TouchPortalHardwareMonitor.Services;

/// <summary>
/// Reads instantaneous FPS from RivaTuner Statistics Server (RTSS) shared
/// memory ("RTSSSharedMemoryV2"). Works only while RTSS / MSI Afterburner is
/// running, but needs no elevation, no kernel driver and no ETW session.
/// </summary>
public sealed class RtssFpsProvider
{
    private const string SharedMemoryName = "RTSSSharedMemoryV2";
    private const uint Signature = 0x53535452; // 'RTSS'

    // App-entry field offsets (RTSS_SHARED_MEMORY_APP_ENTRY, V2 layout).
    private const int EntryProcessId = 0;
    private const int EntryName = 4;      // char szName[MAX_PATH=260]
    private const int EntryTime0 = 268;
    private const int EntryTime1 = 272;
    private const int EntryFrames = 276;

    public bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// FPS for the given process id if RTSS is tracking it; otherwise the most
    /// active tracked app; otherwise null.
    /// </summary>
    public FpsReading? GetFps(int foregroundPid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (acc.ReadUInt32(0) != Signature)
            {
                return null;
            }

            // Header: dwAppEntrySize@8, dwAppArrOffset@12, dwAppArrSize@16
            uint entrySize = acc.ReadUInt32(8);
            uint arrOffset = acc.ReadUInt32(12);
            uint arrSize = acc.ReadUInt32(16);
            if (entrySize == 0 || arrSize == 0)
            {
                return null;
            }

            FpsReading? best = null;
            double bestFps = 0;

            for (uint i = 0; i < arrSize; i++)
            {
                long entry = arrOffset + (long)i * entrySize;

                uint pid = acc.ReadUInt32(entry + EntryProcessId);
                if (pid == 0) continue;

                uint time0 = acc.ReadUInt32(entry + EntryTime0);
                uint time1 = acc.ReadUInt32(entry + EntryTime1);
                uint frames = acc.ReadUInt32(entry + EntryFrames);
                if (time1 <= time0) continue;

                double fps = frames * 1000.0 / (time1 - time0);
                if (fps <= 0) continue;

                var name = ReadName(acc, entry + EntryName);

                // Exact foreground match wins immediately.
                if ((int)pid == foregroundPid)
                {
                    return new FpsReading((float)fps, name, "RTSS");
                }

                if (fps > bestFps)
                {
                    bestFps = fps;
                    best = new FpsReading((float)fps, name, "RTSS");
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadName(MemoryMappedViewAccessor acc, long offset)
    {
        var bytes = new byte[260];
        acc.ReadArray(offset, bytes, 0, bytes.Length);
        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = bytes.Length;
        var raw = Encoding.ASCII.GetString(bytes, 0, len);
        int slash = raw.LastIndexOfAny(new[] { '\\', '/' });
        return slash >= 0 ? raw[(slash + 1)..] : raw;
    }
}
