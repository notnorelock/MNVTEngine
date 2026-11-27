// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MNVTEngine.Audio.OpenAL;

/// <summary>
/// OpenAL Soft's playback devices (<c>ALC_ENUMERATE_ALL_EXT</c>, read from <c>soft_oal</c> directly) and an
/// <see cref="OpenALAudioOutput"/> on one of them.
/// </summary>
public sealed unsafe class OpenALOutputSource(ILogger logger) : IAudioOutputSource
{
    private const int AllDevicesSpecifier = 0x1013;
    private const int DeviceSpecifier = 0x1005;

    public IReadOnlyList<string> Devices
    {
        get
        {
            var getString = GetString();
            if (getString == null)
            {
                return [];
            }

            var all = ReadNames(getString(0, AllDevicesSpecifier));
            return all.Count > 0 ? all : ReadNames(getString(0, DeviceSpecifier));
        }
    }

    public IAudioOutput Open(string? device) => OpenALAudioOutput.Open(logger, string.IsNullOrEmpty(device) ? null : device);

    /// <summary>A list of names, each ending in a zero byte, ending in an empty name.</summary>
    internal static List<string> ReadNames(byte* list)
    {
        var names = new List<string>();
        while (list != null && *list != 0)
        {
            var length = 0;
            while (list[length] != 0)
            {
                length++;
            }

            names.Add(Encoding.UTF8.GetString(list, length));
            list += length + 1;
        }

        return names;
    }

    private static delegate* unmanaged[Cdecl]<nint, int, byte*> GetString()
    {
        foreach (var name in (string[])["soft_oal", "openal", "libopenal.so.1"])
        {
            if (NativeLibrary.TryLoad(name, typeof(OpenALOutputSource).Assembly, null, out var library)
                && NativeLibrary.TryGetExport(library, "alcGetString", out var export))
            {
                return (delegate* unmanaged[Cdecl]<nint, int, byte*>)export;
            }
        }

        return null;
    }
}
